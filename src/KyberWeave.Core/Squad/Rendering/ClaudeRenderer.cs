using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Claude Code's native subagent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Subagent and skill contract verified against Claude Code documentation
/// (code.claude.com/docs/en/sub-agents and code.claude.com/docs/en/skills) on 2026-08-23:
/// subagents are stored at <c>.claude/agents/&lt;name&gt;.md</c> (file stem equals the
/// canonical identity — not Copilot's <c>.agent.md</c> double extension) containing Markdown
/// with YAML frontmatter. Required keys are <c>name</c> and <c>description</c>; optional
/// <c>model</c> accepts aliases <c>opus</c> / <c>sonnet</c> / <c>haiku</c> / <c>inherit</c>
/// (or full model ids). Skills are <c>.claude/skills/&lt;name&gt;/SKILL.md</c>.
/// </para>
/// <para>
/// Claude's <c>tools</c> frontmatter key is an allow-list. Omitting it inherits every tool
/// available to subagents — silent permission widening for any canonical <c>deny</c> — so
/// this renderer always emits an explicit list. Only <c>allow</c> grants a tool; <c>ask</c>
/// and <c>deny</c> both withhold. <c>ask</c> is recorded as
/// <see cref="SquadDegradationRecord"/> with code <c>safety-narrowed</c> because Claude's
/// <c>permissionMode</c> is session-wide for the subagent, not per-capability, and setting
/// it would either widen (<c>bypassPermissions</c> / <c>acceptEdits</c> / <c>dontAsk</c>)
/// or override the parent session preference (<c>default</c>). <c>permissionMode</c> is
/// therefore omitted entirely.
/// </para>
/// <para>
/// Base ungoverned tools on every agent: <c>TodoWrite</c>, <c>Skill</c>. MCP server-level
/// wildcards (<c>mcp__codegraph__*</c>, <c>mcp__kyber-weave__*</c>, <c>mcp__context7__*</c>)
/// are granted when <c>filesystem.read</c> is allowed unless the agent is a pure
/// orchestrator (profile id <c>orchestrator</c> or name in
/// <c>{conductor, conductor-v3}</c>). When <c>delegate: allow</c> and
/// <see cref="SquadAgent.DelegatesTo"/> is non-empty, the allow-list emits
/// <c>Agent(name1, name2, …)</c>; official docs state the parentheses roster is ignored
/// when the definition runs as a nested subagent, so that limitation is recorded as
/// <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// Skills carry <c>name</c>, <c>description</c>, and <c>license: MIT</c>. Multi-line
/// descriptions collapse to a single line. Primary agents (<c>conductor</c> and
/// <c>conductor-v3</c>) suppress their skill projections per the native single-projection
/// rule enforced by <see cref="SquadRendererRegistry"/>.
/// </para>
/// </remarks>
public sealed class ClaudeRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".claude/agents";
    private const string SkillsDirectory = ".claude/skills";

    private static readonly string[] SharedConductorIdentities = ["conductor", "conductor-v3"];

    private static readonly string[] BaseUngovernedTools = ["TodoWrite", "Skill"];

    private static readonly string[] StandardMcpTools =
    [
        "mcp__codegraph__*",
        "mcp__kyber-weave__*",
        "mcp__context7__*"
    ];

    /// <summary>
    /// Lowers the semantic capability vocabulary onto Claude Code's built-in tool names,
    /// verified against code.claude.com/docs/en/sub-agents on 2026-08-23.
    /// <c>network.publish</c> is absent deliberately: no built-in publish tool exists.
    /// <c>delegate</c> is handled separately so a non-empty
    /// <see cref="SquadAgent.DelegatesTo"/> can emit <c>Agent(roster)</c>.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityTools =
    [
        ("filesystem.read", ["Read"]),
        ("filesystem.search", ["Grep", "Glob"]),
        ("filesystem.write", ["Edit", "Write", "NotebookEdit"]),
        ("process.execute", ["Bash", "PowerShell"]),
        ("network.read", ["WebFetch", "WebSearch"]),
    ];

    /// <summary>
    /// Emission order, fixed so a rendered agent file is byte-stable regardless of how the
    /// profile's permissions enumerate. <c>Agent</c> is a placeholder: a roster form
    /// <c>Agent(name1, name2, …)</c> occupies the same slot when present.
    /// </summary>
    private static readonly string[] ToolOrder =
    [
        "TodoWrite",
        "Skill",
        "Read",
        "mcp__codegraph__*",
        "mcp__kyber-weave__*",
        "mcp__context7__*",
        "Grep",
        "Glob",
        "Edit",
        "Write",
        "NotebookEdit",
        "Bash",
        "PowerShell",
        "WebFetch",
        "WebSearch",
        "Agent"
    ];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithTypeConverter(new ClaudeToolsFlowSequenceConverter())
        .Build();

    /// <summary>
    /// YamlDotNet does not document <see cref="ISerializer"/> as thread-safe, and the
    /// registry may dispatch renderers concurrently; serialization takes this lock so a
    /// shared static instance cannot interleave emitter state.
    /// </summary>
    private static readonly object SerializerLock = new();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Claude];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Claude))
        {
            throw new ArgumentException(
                "ClaudeRenderer was asked to render a target other than Claude.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        foreach (SquadAgent agent in source.Agents)
        {
            files.Add(RenderAgent(
                agent,
                source.ModelProfiles.Profiles,
                source.CapabilityProfiles.Profiles));

            degradations.AddRange(BuildDegradationRecords(agent, source.CapabilityProfiles.Profiles));
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // Primary agents (conductor and conductor-v3) are native agents on Claude Code;
            // suppressing their skill projection adheres to the single-projection rule
            // enforced by SquadRendererRegistry.
            if (SharedConductorIdentities.Contains(skill.Name, StringComparer.Ordinal))
            {
                continue;
            }

            files.Add(RenderSkill(skill));
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description
        };

        string? model = ResolveClaudeModel(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        // Always emit tools: omitting the key inherits every subagent tool (widening).
        frontmatter["tools"] = new ClaudeToolsFlowSequence(ResolveTools(agent, capabilityProfiles));

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        return new SquadDeploymentFile(
            $"{AgentsDirectory}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            "claude");
    }

    private static SquadDeploymentFile RenderSkill(SquadSkill skill)
    {
        string singleLineDescription = string.Join(" ", skill.Description.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = skill.Name,
            ["description"] = singleLineDescription,
            ["license"] = "MIT"
        };

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, skill.InstructionBody);
        }

        return new SquadDeploymentFile(
            $"{SkillsDirectory}/{skill.Name}/SKILL.md",
            Encoding.UTF8.GetBytes(content),
            "claude");
    }

    private static string? ResolveClaudeModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("claude", out string? claudeModel))
        {
            // Docs default is inherit when the key is omitted; an explicit inherit is the
            // same deferral and must not appear in frontmatter.
            return string.Equals(claudeModel, "inherit", StringComparison.Ordinal)
                ? null
                : claudeModel;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal)
            ? null
            : profile.Default;
    }

    /// <summary>
    /// Lowers a capability profile onto Claude Code's closed tool allow-list. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction.
    /// </summary>
    private static IReadOnlyList<string> ResolveTools(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        HashSet<string> granted = new(BaseUngovernedTools, StringComparer.Ordinal);
        string? agentToolEntry = null;

        // An unresolvable profile grants nothing beyond the ungoverned tools. Falling back
        // to "grant everything" here would turn a source error into silent widening.
        if (capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            foreach ((string capability, string[] tools) in CapabilityTools)
            {
                if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                    decision == SquadPermissionDecision.Allow)
                {
                    foreach (string tool in tools)
                    {
                        granted.Add(tool);
                    }
                }
            }

            if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                delegateDecision == SquadPermissionDecision.Allow)
            {
                agentToolEntry = agent.DelegatesTo.Count > 0
                    ? $"Agent({string.Join(", ", agent.DelegatesTo)})"
                    : "Agent";
                granted.Add(agentToolEntry);
            }

            bool isPureOrchestrator =
                string.Equals(agent.CapabilityProfile, "orchestrator", StringComparison.Ordinal) ||
                SharedConductorIdentities.Contains(agent.Name, StringComparer.Ordinal);

            if (!isPureOrchestrator &&
                profile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision readDecision) &&
                readDecision == SquadPermissionDecision.Allow)
            {
                foreach (string mcpTool in StandardMcpTools)
                {
                    granted.Add(mcpTool);
                }
            }
        }

        List<string> ordered = [];
        foreach (string tool in ToolOrder)
        {
            if (string.Equals(tool, "Agent", StringComparison.Ordinal))
            {
                if (agentToolEntry is not null)
                {
                    ordered.Add(agentToolEntry);
                }

                continue;
            }

            if (granted.Contains(tool))
            {
                ordered.Add(tool);
            }
        }

        return ordered;
    }

    private static IEnumerable<SquadDegradationRecord> BuildDegradationRecords(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        // 'deny' needs no record: the rendered allow-list withholds the tool. Only 'ask'
        // loses meaning — Claude has no per-capability confirmation gate on subagents.
        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                Target: "claude",
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "safety-narrowed",
                InstructionDigest: agent.BodyDigest,
                Details: $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                    $"{string.Join(", ", narrowed)}. Claude Code's tool allow-list is binary " +
                    "and permissionMode is session-wide for the subagent, so these narrow to " +
                    "'deny' and the corresponding tools are withheld from the agent's 'tools' list.");
        }

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision == SquadPermissionDecision.Allow)
        {
            notExpressibleDetails.Add(
                "Capability 'network.publish' is allowed but no built-in Claude Code tool exists to express it.");
        }

        // Parentheses roster applies for `claude --agent` main-thread use; official docs
        // (2026-08-23) state it is ignored when the same file runs as a nested subagent.
        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow &&
            agent.DelegatesTo.Count > 0)
        {
            notExpressibleDetails.Add(
                "Claude Code ignores Agent(roster) parentheses when this definition " +
                "runs as a nested subagent; the permitted delegation roster is not enforced " +
                "for nested Task/Agent spawns. Roster: " +
                string.Join(", ", agent.DelegatesTo) + ".");
        }

        if (notExpressibleDetails.Count > 0)
        {
            yield return new SquadDegradationRecord(
                Target: "claude",
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "permission-not-expressible",
                InstructionDigest: agent.BodyDigest,
                Details: string.Join(" ", notExpressibleDetails));
        }
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="ClaudeToolsFlowSequenceConverter"/>.
    /// </summary>
    private sealed class ClaudeToolsFlowSequence(IEnumerable<string> tools) : List<string>(tools);

    /// <summary>
    /// Serializes Claude agent tools as an inline YAML flow sequence. Entries containing
    /// <c>(</c> or <c>*</c> emit as single-quoted scalars so YamlDotNet / consumers do not
    /// misparse MCP wildcards or <c>Agent(roster)</c> forms.
    /// </summary>
    private sealed class ClaudeToolsFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ClaudeToolsFlowSequence);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            throw new NotSupportedException("Deserialization of ClaudeToolsFlowSequence is not supported.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not ClaudeToolsFlowSequence tools)
            {
                return;
            }

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Flow));
            foreach (string tool in tools)
            {
                bool needsQuoting = tool.Contains('(', StringComparison.Ordinal) ||
                    tool.Contains('*', StringComparison.Ordinal);

                emitter.Emit(new Scalar(
                    AnchorName.Empty,
                    TagName.Empty,
                    tool,
                    needsQuoting ? ScalarStyle.SingleQuoted : ScalarStyle.Plain,
                    isPlainImplicit: true,
                    isQuotedImplicit: true));
            }

            emitter.Emit(new SequenceEnd());
        }
    }
}

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
/// (code.claude.com/docs/en/sub-agents and code.claude.com/docs/en/skills) on 2026-08-23, with
/// primary-agent entry-point facts re-verified on 2026-09-25: subagents are stored at
/// <c>.claude/agents/&lt;name&gt;.md</c> (file stem equals the canonical identity — not
/// Copilot's <c>.agent.md</c> double extension) containing Markdown with YAML frontmatter.
/// Required keys are <c>name</c> and <c>description</c>; optional <c>model</c> accepts aliases
/// <c>opus</c> / <c>sonnet</c> / <c>haiku</c> / <c>inherit</c> (or full model ids). Skills are
/// <c>.claude/skills/&lt;name&gt;/SKILL.md</c>.
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
/// orchestrator (profile id <c>orchestrator</c>). When <c>delegate: allow</c> and
/// <see cref="SquadAgent.DelegatesTo"/> is non-empty, the allow-list emits
/// <c>Agent(name1, name2, …)</c>; official docs state the parentheses roster is ignored
/// when the definition runs as a nested subagent, so that limitation is recorded as
/// <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// Skills carry <c>name</c>, <c>description</c>, and <c>license: MIT</c>. Multi-line
/// descriptions collapse to a single line. Profile-declared shared identities suppress
/// their skill projections per the native single-projection rule enforced by
/// <see cref="SquadRendererRegistry"/>.
/// </para>
/// <para>
/// <strong>Primary-agent entry point:</strong> A primary agent with a fallback profile
/// declaring <c>no-primary-agent: skill</c> renders as both a subagent at
/// <c>.claude/agents/&lt;name&gt;.md</c> and an entry-point skill at
/// <c>.claude/skills/&lt;name&gt;/SKILL.md</c>, invoked as <c>/&lt;name&gt;</c> in the main
/// conversation. The subagent file is kept for enforced invocation via <c>claude --agent
/// &lt;name&gt;</c> (the only mode where <c>Agent(roster)</c> and model are enforced). The skill
/// frontmatter contains exactly <c>name</c>, <c>description</c>, and <c>license</c>; its body
/// matches the canonical agent body verbatim, and its resource closure is projected beside it.
/// The skill's description stays in Claude's context, so Claude may auto-load <c>/&lt;name&gt;</c>
/// into the main conversation without the user typing it. Entry-point skill details merge into
/// the single existing <c>permission-not-expressible</c> record, which states that the session's
/// tools, permission mode, MCP servers and model apply to <c>/&lt;name&gt;</c>, and that
/// <c>claude --agent &lt;name&gt;</c> is the enforced alternative. Scope precedence differs:
/// skills resolve personal over project, so a project Squad install and a global one can place
/// <c>/&lt;name&gt;</c> and <c>@agent-&lt;name&gt;</c> at different versions. Under
/// <c>no-primary-agent: omit</c>, no entry-point skill is emitted and the agent's records remain
/// unchanged.
/// </para>
/// </remarks>
public sealed class ClaudeRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".claude/agents";
    private const string SkillsDirectory = ".claude/skills";

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

    /// <summary>
    /// Resolves the directory prefix for agents/skills based on deployment scope.
    /// Under Project scope, keeps `.claude/` prefix; under Global scope, removes it.
    /// </summary>
    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        // baseDirectory is like ".claude/agents" or ".claude/skills"
        // Under Project scope, return as-is
        // Under Global scope, strip the ".claude/" prefix
        if (scope == SquadDeploymentScope.Project)
        {
            return baseDirectory;
        }

        // Strip ".claude/" prefix for global scope
        return baseDirectory.StartsWith(".claude/", StringComparison.Ordinal)
            ? baseDirectory[".claude/".Length..]
            : baseDirectory;
    }

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
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

        // Read skill identities upfront so the primary-agent collision check can see whether
        // a canonical skill already occupies the entry-point identity before any file is built.
        HashSet<string> skillIdentities = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in a lowered primary agent's
        // permission-not-expressible details without a renderer change.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile principal = RenderAgent(
                agent,
                source.ModelProfiles.Profiles,
                source.CapabilityProfiles.Profiles,
                request.Scope);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, agent.Resources);

            if (agent.Invocation == SquadInvocation.Primary)
            {
                SquadFallbackProfile fallbackProfile = source.FallbackProfiles.Profiles[agent.Fallback];
                if (string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal))
                {
                    // Fail closed if a canonical skill occupies the entry-point identity:
                    // Claude renders both a subagent and an entry-point skill, so collision here
                    // cannot be resolved by role-prefixed fallback.
                    if (skillIdentities.Contains(agent.Name))
                    {
                        throw new SquadRenderValidationException(
                            $"Cannot render primary agent '{agent.Name}' as a Claude entry-point " +
                            $"skill: a canonical skill named '{agent.Name}' already occupies that " +
                            "identity. Claude renders both a subagent and an entry-point skill, " +
                            "so this is a fail-closed condition rather than a naming collision " +
                            "this renderer can resolve on its own.");
                    }

                    SquadDeploymentFile skillPrincipal = RenderPrimaryAgentEntryPointSkill(agent, request.Scope);
                    files.Add(skillPrincipal);
                    SquadResourceProjection.Append(files, skillPrincipal, agent.Resources);

                    degradations.AddRange(BuildPrimaryAgentDegradationRecords(
                        agent,
                        fallbackProfile,
                        source.CapabilityProfiles.Profiles,
                        capabilityVocabulary));
                }
                else if (string.Equals(fallbackProfile.NoPrimaryAgent, "omit", StringComparison.Ordinal))
                {
                    // Omit mode: render exactly as pre-T3 (no entry-point skill, byte-identical records).
                    degradations.AddRange(BuildDegradationRecords(agent, source.CapabilityProfiles.Profiles));
                }
                else
                {
                    throw new SquadRenderValidationException(
                        $"Fallback profile '{agent.Fallback}' declares unsupported " +
                        $"no-primary-agent value '{fallbackProfile.NoPrimaryAgent}' for primary " +
                        $"agent '{agent.Name}'.");
                }
            }
            else
            {
                degradations.AddRange(BuildDegradationRecords(agent, source.CapabilityProfiles.Profiles));
            }
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // A profile-declared shared identity has one canonical projection. Resolve the
            // set from source so removing or adding a shared identity never requires a
            // renderer-local roster change.
            if (sharedIdentities.Contains(skill.Name))
            {
                continue;
            }

            SquadDeploymentFile principal = RenderSkill(skill, request.Scope);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, skill.Resources);
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        SquadDeploymentScope scope)
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

        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, scope);
        return new SquadDeploymentFile(
            $"{agentsDir}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            "claude");
    }

    private static SquadDeploymentFile RenderPrimaryAgentEntryPointSkill(
        SquadAgent agent,
        SquadDeploymentScope scope)
    {
        string singleLineDescription = string.Join(" ", agent.Description.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = singleLineDescription,
            ["license"] = "MIT"
        };

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDir}/{agent.Name}/SKILL.md",
            Encoding.UTF8.GetBytes(content),
            "claude");
    }

    private static SquadDeploymentFile RenderSkill(SquadSkill skill, SquadDeploymentScope scope)
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

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDir}/{skill.Name}/SKILL.md",
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

            bool isPureOrchestrator = string.Equals(
                agent.CapabilityProfile,
                "orchestrator",
                StringComparison.Ordinal);

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

    private static IEnumerable<SquadDegradationRecord> BuildPrimaryAgentDegradationRecords(
        SquadAgent agent,
        SquadFallbackProfile fallbackProfile,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary)
    {
        bool isSkillMode = string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal);

        if (isSkillMode)
        {
            yield return new SquadDegradationRecord(
                Target: "claude",
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "role-skill-fallback",
                InstructionDigest: agent.BodyDigest,
                Details: $"Claude has a real primary-agent primitive only through " +
                    $"'claude --agent <name>' or the 'agent' setting; Claude Code enforces " +
                    $"tools, Agent(roster) and model only in those modes. Fallback profile " +
                    $"'{agent.Fallback}' declares no-primary-agent: skill, so this agent also " +
                    $"renders as an entry-point skill invoked as `/{agent.Name}` in the main " +
                    "conversation, where the primary-agent enforcement does not apply. The " +
                    "subagent file is kept.");
        }

        List<string> notExpressibleDetails = [];

        // Primary agents always declare every capability decision in permission-not-expressible,
        // so the entry point and subagent descriptions both state what is not enforced.
        notExpressibleDetails.Add(
            $"Capability decisions ({CapabilityDegradations.DescribeCapabilityDecisions(agent, capabilityProfiles, capabilityVocabulary)}).");

        if (isSkillMode)
        {
            // The entry-point skill runs in the main conversation under session-wide permissions,
            // not under the primary-agent enforcement that `claude --agent` provides.
            notExpressibleDetails.Add(
                $"The session's tools, permission mode, MCP servers and model apply to `/{agent.Name}`.");
        }

        if (capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile) &&
            profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow &&
            agent.DelegatesTo.Count > 0)
        {
            // Official docs state Agent(roster) is ignored when running as a nested subagent,
            // so the roster protection only applies when this agent is the primary via `claude --agent`.
            notExpressibleDetails.Add(
                "Claude Code ignores Agent(roster) parentheses when this definition " +
                "runs as a nested subagent; the permitted delegation roster is not enforced " +
                "for nested Task/Agent spawns. Roster: " +
                string.Join(", ", agent.DelegatesTo) + ".");
        }

        bool isPureOrchestrator = string.Equals(
            agent.CapabilityProfile,
            "orchestrator",
            StringComparison.Ordinal);
        if (isPureOrchestrator && isSkillMode)
        {
            // The MCP withholding in ResolveTools is only applied to the subagent allow-list;
            // the entry-point skill inherits session MCP servers, which may differ.
            notExpressibleDetails.Add(
                "The pure-orchestrator MCP withholding (no mcp__codegraph__*, mcp__kyber-weave__*, " +
                "mcp__context7__*) applies to the subagent file only, not the entry-point skill.");
        }

        if (isSkillMode)
        {
            notExpressibleDetails.Add(
                $"`claude --agent {agent.Name}` is the enforced alternative where tools, Agent(roster) and model are enforced.");
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

        SquadPermissionDecision executeDecision = profile is not null &&
            profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
            ? exec
            : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision = profile is not null &&
            profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
            ? write
            : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: "claude",
            canonicalIdentity: agent.Name,
            outputIdentity: agent.Name,
            instructionDigest: agent.BodyDigest,
            executeDecision: executeDecision,
            writeDecision: writeDecision,
            grantedShellTools: ["Bash", "PowerShell"],
            withheldWriteTools: ["Edit", "NotebookEdit", "Write"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
        }
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

        SquadPermissionDecision executeDecision = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
            ? exec
            : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
            ? write
            : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: "claude",
            canonicalIdentity: agent.Name,
            outputIdentity: agent.Name,
            instructionDigest: agent.BodyDigest,
            executeDecision: executeDecision,
            writeDecision: writeDecision,
            grantedShellTools: ["Bash", "PowerShell"],
            withheldWriteTools: ["Edit", "NotebookEdit", "Write"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
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

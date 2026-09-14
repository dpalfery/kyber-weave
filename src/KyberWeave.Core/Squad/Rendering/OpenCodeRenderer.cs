using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into OpenCode's native subagent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Subagent and skill contract verified against OpenCode specifications on 2026-09-14:
/// subagents are stored at <c>.opencode/agents/&lt;name&gt;.md</c> containing Markdown with
/// YAML frontmatter. Required keys are <c>name</c> and <c>description</c>; optional
/// <c>model</c> resolves from <c>models.yml</c> for harness <c>opencode</c> (omitted when
/// <c>inherit</c> or empty). Skills are stored at <c>.opencode/skills/&lt;name&gt;/SKILL.md</c>.
/// </para>
/// <para>
/// OpenCode's <c>tools</c> frontmatter key is an explicit allowlist. Omitting it inherits
/// ambient tool access — silent permission widening for any canonical <c>deny</c> — so this
/// renderer always emits an explicit list. Only <c>allow</c> grants a tool; <c>ask</c> and
/// <c>deny</c> both withhold. <c>ask</c> is recorded as <see cref="SquadDegradationRecord"/>
/// with code <c>safety-narrowed</c> because OpenCode subagents do not support interactive
/// per-capability confirmation gates.
/// </para>
/// <para>
/// Base ungoverned tools on every agent: <c>todo</c>, <c>skill</c>. Semantic capabilities lower
/// onto OpenCode's tool taxonomy: <c>filesystem.read</c> -&gt; <c>read</c>, <c>filesystem.search</c> -&gt;
/// <c>grep</c>, <c>glob</c>, <c>filesystem.write</c> -&gt; <c>edit</c>, <c>write</c>, <c>patch</c>,
/// <c>process.execute</c> -&gt; <c>bash</c>, <c>network.read</c> -&gt; <c>fetch</c>, <c>search</c>,
/// and <c>delegate</c> -&gt; <c>task</c> (or <c>task(roster)</c>). <c>network.publish</c> has no
/// native tool and is withheld, emitting <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// Skills carry <c>name</c>, single-line <c>description</c>, and <c>license: MIT</c>.
/// Profile-declared shared identities suppress their skill projections per the native
/// single-projection rule.
/// </para>
/// </remarks>
public sealed class OpenCodeRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".opencode/agents";
    private const string SkillsDirectory = ".opencode/skills";

    private static readonly string[] BaseUngovernedTools = ["todo", "skill"];

    /// <summary>
    /// Lowers the semantic capability vocabulary onto OpenCode's built-in tool names.
    /// <c>network.publish</c> is absent deliberately: no built-in publish tool exists.
    /// <c>delegate</c> is handled separately so a non-empty
    /// <see cref="SquadAgent.DelegatesTo"/> can emit <c>task(roster)</c>.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityTools =
    [
        ("filesystem.read", ["read"]),
        ("filesystem.search", ["grep", "glob"]),
        ("filesystem.write", ["edit", "write", "patch"]),
        ("process.execute", ["bash"]),
        ("network.read", ["fetch", "search"]),
    ];

    /// <summary>
    /// Emission order, fixed so a rendered agent file is byte-stable regardless of how the
    /// profile's permissions enumerate. <c>task</c> is a placeholder: a roster form
    /// <c>task(name1, name2, …)</c> occupies the same slot when present.
    /// </summary>
    private static readonly string[] ToolOrder =
    [
        "todo",
        "skill",
        "read",
        "grep",
        "glob",
        "edit",
        "write",
        "patch",
        "bash",
        "fetch",
        "search",
        "task"
    ];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithTypeConverter(new OpenCodeToolsFlowSequenceConverter())
        .Build();

    /// <summary>
    /// YamlDotNet does not document <see cref="ISerializer"/> as thread-safe, and the
    /// registry may dispatch renderers concurrently; serialization takes this lock so a
    /// shared static instance cannot interleave emitter state.
    /// </summary>
    private static readonly object SerializerLock = new();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.OpenCode];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.OpenCode))
        {
            throw new ArgumentException(
                "OpenCodeRenderer was asked to render a target other than OpenCode.",
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

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile principal = RenderAgent(
                agent,
                source.ModelProfiles.Profiles,
                source.CapabilityProfiles.Profiles);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, agent.Resources);

            degradations.AddRange(BuildDegradationRecords(agent, source.CapabilityProfiles.Profiles));
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

            SquadDeploymentFile principal = RenderSkill(skill);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, skill.Resources);
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

        string? model = ResolveOpenCodeModel(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        // Always emit tools: omitting the key inherits ambient tool access (widening).
        frontmatter["tools"] = new OpenCodeToolsFlowSequence(ResolveTools(agent, capabilityProfiles));

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        return new SquadDeploymentFile(
            $"{AgentsDirectory}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            SquadTargetCatalog.GetToken(SquadTarget.OpenCode));
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
            SquadTargetCatalog.GetToken(SquadTarget.OpenCode));
    }

    private static string? ResolveOpenCodeModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("opencode", out string? opencodeModel))
        {
            // Omit model key when override is inherit or empty.
            return string.Equals(opencodeModel, "inherit", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(opencodeModel)
                ? null
                : opencodeModel;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(profile.Default)
            ? null
            : profile.Default;
    }

    /// <summary>
    /// Lowers a capability profile onto OpenCode's closed tool allowlist. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction.
    /// </summary>
    private static IReadOnlyList<string> ResolveTools(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        HashSet<string> granted = new(BaseUngovernedTools, StringComparer.Ordinal);
        string? taskToolEntry = null;

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
                taskToolEntry = agent.DelegatesTo.Count > 0
                    ? $"task({string.Join(", ", agent.DelegatesTo)})"
                    : "task";
                granted.Add(taskToolEntry);
            }
        }

        List<string> ordered = [];
        foreach (string tool in ToolOrder)
        {
            if (string.Equals(tool, "task", StringComparison.Ordinal))
            {
                if (taskToolEntry is not null)
                {
                    ordered.Add(taskToolEntry);
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

        // 'deny' needs no record: the rendered allowlist withholds the tool. Only 'ask'
        // loses meaning — OpenCode has no per-capability confirmation gate on subagents.
        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                Target: SquadTargetCatalog.GetToken(SquadTarget.OpenCode),
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "safety-narrowed",
                InstructionDigest: agent.BodyDigest,
                Details: $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                    $"{string.Join(", ", narrowed)}. OpenCode's tool allowlist is binary " +
                    "and cannot prompt for per-capability confirmation, so these narrow to " +
                    "'deny' and the corresponding tools are withheld from the agent's 'tools' list.");
        }

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision != SquadPermissionDecision.Deny)
        {
            notExpressibleDetails.Add(
                "OpenCode has no native network publishing tool; tool withheld for capability 'network.publish'.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow &&
            agent.DelegatesTo.Count > 0)
        {
            notExpressibleDetails.Add(
                "OpenCode ignores task(roster) parentheses when this definition " +
                "runs as a nested subagent; the permitted delegation roster is not enforced " +
                "for nested task spawns. Roster: " +
                string.Join(", ", agent.DelegatesTo) + ".");
        }

        if (notExpressibleDetails.Count > 0)
        {
            yield return new SquadDegradationRecord(
                Target: SquadTargetCatalog.GetToken(SquadTarget.OpenCode),
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "permission-not-expressible",
                InstructionDigest: agent.BodyDigest,
                Details: string.Join(" ", notExpressibleDetails));
        }
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="OpenCodeToolsFlowSequenceConverter"/>.
    /// </summary>
    private sealed class OpenCodeToolsFlowSequence(IEnumerable<string> tools) : List<string>(tools);

    /// <summary>
    /// Serializes OpenCode agent tools as an inline YAML flow sequence. Entries containing
    /// <c>(</c> or <c>,</c> emit as single-quoted scalars so YamlDotNet and downstream parsers
    /// do not misparse <c>task(roster)</c> forms.
    /// </summary>
    private sealed class OpenCodeToolsFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(OpenCodeToolsFlowSequence);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            throw new NotSupportedException("Deserialization of OpenCodeToolsFlowSequence is not supported.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not OpenCodeToolsFlowSequence tools)
            {
                return;
            }

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Flow));
            foreach (string tool in tools)
            {
                bool needsQuoting = tool.Contains('(', StringComparison.Ordinal) ||
                    tool.Contains('*', StringComparison.Ordinal) ||
                    tool.Contains(',', StringComparison.Ordinal);

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

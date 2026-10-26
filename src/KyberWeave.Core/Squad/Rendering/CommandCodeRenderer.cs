using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Command Code's native custom-agent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Contract verified against the published <c>command-code</c> npm package, v1.84.0, read
/// 2026-10-17 (the CLI's own bundled agent and skill loaders, not its marketing pages):
/// custom agents are a single Markdown file with YAML frontmatter at
/// <c>.commandcode/agents/&lt;name&gt;.md</c> (personal <c>~/.commandcode/agents/</c>; project
/// wins a name clash). The agents directory is read flat, not recursively, so a resource
/// closure projected beside an agent never registers as an agent. Skills are
/// <c>.commandcode/skills/&lt;name&gt;/SKILL.md</c> (personal <c>~/.commandcode/skills/</c>).
/// Command Code also reads <c>.agents/skills/</c>; Squad does not write that compatibility tree.
/// </para>
/// <para>
/// Agent frontmatter keys are <c>name</c>, <c>description</c>, <c>tools</c>,
/// <c>disallowedTools</c>, <c>model</c>, <c>reasoningEffort</c>, <c>maxTurns</c>,
/// <c>permissionMode</c>, <c>background</c>, and <c>showOutput</c>; the frontmatter is parsed as
/// real YAML. <c>tools</c> is an allow-list of snake_case tool names, and unlike Factory or
/// Claude an omitted <c>tools</c> grants <em>no</em> tools. This renderer still always emits
/// it, as a flow sequence, so an empty grant is the explicit <c>tools: []</c>. Only
/// <c>allow</c> grants; <c>ask</c> withholds and records <c>safety-narrowed</c>. Reserved
/// names are <c>explore</c>, <c>plan</c>, <c>review</c>, and <c>general</c>; no canonical
/// agent uses one.
/// </para>
/// <para>
/// A subagent is never given the <c>agent</c> or <c>agent_output</c> tools, so
/// <c>delegate</c> is not expressible. MCP tools reach a subagent only when named in its
/// <c>tools</c> list; Squad names none, so parent MCP is not inherited.
/// </para>
/// <para>
/// Profile-declared shared identities suppress their skill projections per the native
/// single-projection rule. Under <see cref="SquadDeploymentScope.Global"/> the physical root is
/// <c>~/.commandcode</c>, so relative paths strip the <c>.commandcode/</c> prefix.
/// </para>
/// <para>
/// <strong>Primary-agent entry point:</strong> Command Code dispatches custom agents only as
/// subagents through its task tool and offers no way to start a session as one, so a primary
/// agent whose fallback profile declares <c>no-primary-agent: skill</c> renders as both an
/// agent at <c>.commandcode/agents/&lt;name&gt;.md</c> (kept for dispatch) and an entry-point
/// skill at <c>.commandcode/skills/&lt;name&gt;/SKILL.md</c>, invoked in the main session as
/// <c>/&lt;name&gt;</c>. This is the same shape as <see cref="FactoryRenderer"/>.
/// </para>
/// </remarks>
public sealed class CommandCodeRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".commandcode/agents";
    private const string SkillsDirectory = ".commandcode/skills";

    /// <summary>
    /// Lowers the semantic capability vocabulary onto Command Code's built-in tool names,
    /// verified against the <c>command-code</c> 1.84.0 tool catalog on 2026-10-17.
    /// <c>network.publish</c> is absent: no built-in tool expresses it.
    /// <c>delegate</c> is absent: subagents never receive the <c>agent</c> tool.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityTools =
    [
        ("filesystem.read", ["read_file"]),
        ("filesystem.search", ["read_directory", "grep", "glob"]),
        ("filesystem.write", ["write_file", "edit_file"]),
        ("process.execute", ["shell_command", "shell_output"]),
        ("network.read", ["web_search", "web_fetch"])
    ];

    /// <summary>
    /// Emission order, fixed so a rendered agent file is byte-stable regardless of how the
    /// profile's permissions enumerate.
    /// </summary>
    private static readonly string[] ToolOrder =
    [
        "read_file",
        "read_directory",
        "grep",
        "glob",
        "write_file",
        "edit_file",
        "shell_command",
        "shell_output",
        "web_search",
        "web_fetch"
    ];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithTypeConverter(new CommandCodeYamlFlowSequenceConverter())
        .Build();

    /// <summary>
    /// YamlDotNet does not document <see cref="ISerializer"/> as thread-safe, and the
    /// registry may dispatch renderers concurrently; serialization takes this lock so a
    /// shared static instance cannot interleave emitter state.
    /// </summary>
    private static readonly object SerializerLock = new();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.CommandCode];

    /// <summary>
    /// Under Project scope keeps the <c>.commandcode/</c> prefix; under Global scope the
    /// physical root is already <c>~/.commandcode</c>, so the relative path is the bare
    /// <c>agents/</c> / <c>skills/</c> form.
    /// </summary>
    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        if (scope == SquadDeploymentScope.Project)
        {
            return baseDirectory;
        }

        return baseDirectory.StartsWith(".commandcode/", StringComparison.Ordinal)
            ? baseDirectory[".commandcode/".Length..]
            : baseDirectory;
    }

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.CommandCode))
        {
            throw new ArgumentException(
                "CommandCodeRenderer was asked to render a target other than Command Code.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadSource source = SquadRenderSource.Load(request, SquadTarget.CommandCode);
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

        // Read skill identities upfront so the primary-agent collision check can see whether
        // a canonical skill already occupies the entry-point identity before any file is built.
        HashSet<string> skillIdentities = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);

        // A subagent shared identity is suppressed because the agent file carries the identical
        // body; a primary shared identity is the thin wrapper whose canonical skill IS the entry
        // point, so suppressing it would leave the wrapper pointing at nothing.
        HashSet<string> suppressedSkillIdentities =
            SquadSharedIdentityProjection.SuppressedSkillIdentities(source, sharedIdentities);

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
                    if (skillIdentities.Contains(agent.Name))
                    {
                        // A declared shared identity already has its canonical skill as the entry
                        // point at .commandcode/skills/<name>/SKILL.md, so emitting the entry-point
                        // skill too would project one identity twice. The canonical skill is the
                        // entry point; skip the duplicate rather than failing closed.
                        if (sharedIdentities.Contains(agent.Name))
                        {
                            degradations.AddRange(BuildPrimaryAgentDegradationRecords(
                                agent,
                                fallbackProfile,
                                source.CapabilityProfiles.Profiles,
                                $"{ResolvePrefixedDirectory(SkillsDirectory, request.Scope)}/{agent.Name}/SKILL.md",
                                true));
                        }
                        else
                        {
                            // Not a declared shared identity: Command Code renders both an agent and an
                            // entry-point skill for the identity, and a native target has no
                            // role-prefixed escape hatch, so this is a real collision.
                            throw new SquadRenderValidationException(
                                $"Cannot render primary agent '{agent.Name}' as a Command Code entry-point " +
                                $"skill: a canonical skill named '{agent.Name}' already occupies that " +
                                "identity. Command Code renders both an agent and an entry-point skill, " +
                                "so this is a fail-closed condition rather than a naming collision " +
                                "this renderer can resolve on its own.");
                        }
                    }
                    else
                    {
                        SquadDeploymentFile skillPrincipal = RenderPrimaryAgentEntryPointSkill(agent, request.Scope);
                        files.Add(skillPrincipal);
                        SquadResourceProjection.Append(files, skillPrincipal, agent.Resources);

                        degradations.AddRange(BuildPrimaryAgentDegradationRecords(
                            agent,
                            fallbackProfile,
                            source.CapabilityProfiles.Profiles,
                            skillPrincipal.RelativePath));
                    }
                }
                else if (string.Equals(fallbackProfile.NoPrimaryAgent, "omit", StringComparison.Ordinal))
                {
                    // Omit mode: the agent renders exactly as before, with no entry-point skill.
                    degradations.AddRange(BuildDegradationRecords(agent, source.CapabilityProfiles.Profiles));
                }
                else
                {
                    throw new SquadRenderValidationException(
                        $"Fallback profile '{agent.Fallback}' declares unsupported no-primary-agent " +
                        $"value '{fallbackProfile.NoPrimaryAgent}' for primary agent '{agent.Name}'.");
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
            // renderer-local roster change. A primary shared identity is the exception: its
            // canonical skill is the entry point and must be emitted.
            if (suppressedSkillIdentities.Contains(skill.Name))
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

        string? model = ResolveCommandCodeModel(agent, modelProfiles);
        if (model is not null)
        {
            (string bareModel, string? reasoningEffort) = ParseReasoningEffortSuffix(model);
            frontmatter["model"] = bareModel;
            if (reasoningEffort is not null)
            {
                frontmatter["reasoningEffort"] = reasoningEffort;
            }
        }

        // Always emit tools. Omitting it already grants nothing, but an explicit list keeps
        // the grant reviewable in the file, and an empty grant is the visible tools: [].
        frontmatter["tools"] = new CommandCodeYamlFlowSequence(ResolveTools(agent, capabilityProfiles));

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, scope);
        return new SquadDeploymentFile(
            $"{agentsDir}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            SquadTargetCatalog.GetToken(SquadTarget.CommandCode));
    }

    private static SquadDeploymentFile RenderSkill(SquadSkill skill, SquadDeploymentScope scope)
    {
        string singleLineDescription = string.Join(" ", skill.Description.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = skill.Name,
            ["description"] = singleLineDescription
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
            SquadTargetCatalog.GetToken(SquadTarget.CommandCode));
    }

    /// <summary>
    /// Renders a primary agent's entry-point skill: the canonical agent body verbatim under
    /// the canonical skill frontmatter shape, so the user can invoke the role in the main
    /// session where no session-parent selector exists.
    /// </summary>
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
            ["description"] = singleLineDescription
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
            SquadTargetCatalog.GetToken(SquadTarget.CommandCode));
    }

    private static readonly string[] AllowedReasoningEfforts = ["off", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// Splits an optional trailing <c>[reasoningEffort=&lt;level&gt;]</c> suffix off a model value.
    /// Command Code's agent frontmatter accepts <c>off | low | medium | high | xhigh | max</c>
    /// (<c>command-code</c> 1.84.0), so any other level throws <see cref="SquadRenderValidationException"/>.
    /// </summary>
    internal static (string BareModel, string? ReasoningEffort) ParseReasoningEffortSuffix(string modelValue)
    {
        const string suffixPrefix = "[reasoningEffort=";
        int start = modelValue.LastIndexOf(suffixPrefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return (modelValue, null);
        }

        if (start == 0 || !modelValue.EndsWith(']'))
        {
            throw new SquadRenderValidationException(
                $"Malformed reasoningEffort suffix in model value '{modelValue}': expected '<model>[reasoningEffort=<level>]'.");
        }

        string level = modelValue[(start + suffixPrefix.Length)..^1];
        if (Array.IndexOf(AllowedReasoningEfforts, level) < 0)
        {
            throw new SquadRenderValidationException(
                $"Invalid reasoningEffort '{level}' in model value '{modelValue}': Command Code accepts off, low, medium, high, xhigh, or max.");
        }

        string bareModel = modelValue[..start];
        if (string.IsNullOrWhiteSpace(bareModel) ||
            bareModel.Any(char.IsWhiteSpace) ||
            bareModel.Contains('[', StringComparison.Ordinal) ||
            bareModel.Contains(']', StringComparison.Ordinal))
        {
            throw new SquadRenderValidationException(
                $"Malformed model value '{modelValue}': the model id before the reasoningEffort suffix must be non-blank and free of whitespace and brackets (stacked suffixes are not allowed).");
        }

        return (bareModel, level);
    }

    private static string? ResolveCommandCodeModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("commandcode", out string? commandCodeModel))
        {
            return string.Equals(commandCodeModel, "inherit", StringComparison.Ordinal) ||
                   string.IsNullOrWhiteSpace(commandCodeModel)
                ? null
                : commandCodeModel;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal) ||
               string.IsNullOrWhiteSpace(profile.Default)
            ? null
            : profile.Default;
    }

    /// <summary>
    /// Lowers a capability profile onto Command Code's tool allow-list. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction.
    /// </summary>
    private static IReadOnlyList<string> ResolveTools(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        HashSet<string> granted = new(StringComparer.Ordinal);

        // An unresolvable profile grants nothing. Falling back to "grant everything"
        // here would turn a source error into silent widening.
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
        }

        return ToolOrder.Where(granted.Contains).ToArray();
    }

    /// <summary>
    /// Builds the degradation records for a primary agent that also renders as an
    /// entry-point skill: the fallback record naming the additional projection, then the
    /// agent's own records with the entry-point caveat folded into the shared
    /// permission-not-expressible record. The path is the scope-resolved deployment path the
    /// skill actually renders at, so a global install's records point at
    /// <c>skills/&lt;name&gt;/SKILL.md</c>, not the project-scope form.
    /// </summary>
    private static IEnumerable<SquadDegradationRecord> BuildPrimaryAgentDegradationRecords(
        SquadAgent agent,
        SquadFallbackProfile fallbackProfile,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        string entryPointSkillPath,
        bool reusedCanonicalSkill = false)
    {
        string entryPointDetails = reusedCanonicalSkill
            ? $"The same-name canonical skill at {entryPointSkillPath} is the entry point and " +
              "carries the role; no duplicate entry-point skill is emitted, because the " +
              "canonical skill already occupies that identity."
            : $"Fallback profile '{agent.Fallback}' declares no-primary-agent: " +
              $"{fallbackProfile.NoPrimaryAgent}, so this agent also renders as an entry-point " +
              $"skill at {entryPointSkillPath}.";

        yield return new SquadDegradationRecord(
            SquadTargetCatalog.GetToken(SquadTarget.CommandCode),
            agent.Name,
            agent.Name,
            "role-skill-fallback",
            agent.BodyDigest,
            "Command Code dispatches custom agents only as subagents through its task " +
            "tool and offers no way to start a session as one. " + entryPointDetails +
            " The entry point runs in the main session under the session's permissions — " +
            "the agent's explicit tools allow-list does not apply to it. The agent file is " +
            "kept for task dispatch.");

        foreach (SquadDegradationRecord record in BuildDegradationRecords(
                     agent,
                     capabilityProfiles,
                     $"The same-name entry point at {entryPointSkillPath} runs under the " +
                     "session's permissions; the agent's explicit tools allow-list does not " +
                     "apply to it."))
        {
            yield return record;
        }
    }

    private static IEnumerable<SquadDegradationRecord> BuildDegradationRecords(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        string? entryPointSkillNote = null)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                SquadTargetCatalog.GetToken(SquadTarget.CommandCode),
                agent.Name,
                agent.Name,
                "safety-narrowed",
                agent.BodyDigest,
                $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                $"{string.Join(", ", narrowed)}. Command Code has no per-tool approval key in agent " +
                "frontmatter, so these narrow to deny and the corresponding tools are withheld " +
                "from the agent's tools list.");
        }

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision != SquadPermissionDecision.Deny)
        {
            notExpressibleDetails.Add(
                "Capability 'network.publish' has no Command Code tool; web-publish is withheld.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision != SquadPermissionDecision.Deny)
        {
            notExpressibleDetails.Add(
                "Capability 'delegate' cannot spawn subagents; Command Code never gives a subagent the agent tool.");
        }

        notExpressibleDetails.Add(
            "MCP tools reach a Command Code subagent only when named in tools; Squad names none, so parent MCP was not inherited.");

        if (entryPointSkillNote is not null)
        {
            notExpressibleDetails.Add(entryPointSkillNote);
        }

        yield return new SquadDegradationRecord(
            SquadTargetCatalog.GetToken(SquadTarget.CommandCode),
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            string.Join(" ", notExpressibleDetails));

        SquadPermissionDecision executeDecision =
            profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
                ? exec
                : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision =
            profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
                ? write
                : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            SquadTargetCatalog.GetToken(SquadTarget.CommandCode),
            agent.Name,
            agent.Name,
            agent.BodyDigest,
            executeDecision,
            writeDecision,
            ["shell_command"],
            ["edit_file", "write_file"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
        }
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="CommandCodeYamlFlowSequenceConverter"/>.
    /// </summary>
    private sealed class CommandCodeYamlFlowSequence(IEnumerable<string> values) : List<string>(values);

    /// <summary>
    /// Serializes Command Code agent <c>tools</c> as an inline YAML flow
    /// sequence so an empty grant is the documented form <c>[]</c> rather than an omitted key.
    /// </summary>
    private sealed class CommandCodeYamlFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            return type == typeof(CommandCodeYamlFlowSequence);
        }

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            throw new NotSupportedException("Deserialization of CommandCodeYamlFlowSequence is not supported.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not CommandCodeYamlFlowSequence items)
            {
                return;
            }

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
            foreach (string item in items)
            {
                emitter.Emit(new Scalar(
                    AnchorName.Empty,
                    TagName.Empty,
                    item,
                    ScalarStyle.Plain,
                    true,
                    true));
            }

            emitter.Emit(new SequenceEnd());
        }
    }
}

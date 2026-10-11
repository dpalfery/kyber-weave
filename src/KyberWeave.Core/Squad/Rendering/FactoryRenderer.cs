using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Factory Droids' native custom-droid and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Contract verified against Factory documentation (docs.factory.ai/harness/subagents and
/// docs.factory.ai/harness/skills) on 2026-09-16: custom droids are Markdown with YAML
/// frontmatter at <c>.factory/droids/&lt;name&gt;.md</c> (never <c>.factory/agents/</c>);
/// personal droids live at <c>~/.factory/droids/&lt;name&gt;.md</c>. Skills are
/// <c>.factory/skills/&lt;name&gt;/SKILL.md</c> (personal <c>~/.factory/skills/</c>). Project
/// <c>.factory/</c> wins over personal on the same name. Squad does not write the
/// compatibility trees <c>~/.agents/skills/</c> or <c>~/.agent/skills/</c>.
/// </para>
/// <para>
/// Factory's <c>tools</c> key is an allow-list. Omitting it allows every tool — the same
/// silent widening CopilotRenderer remarks record for an unset Copilot <c>tools</c> key,
/// which is why ClaudeRenderer always emits an explicit list. This renderer therefore
/// never omits <c>tools</c> and never emits the rejected scalar <c>tools: all</c>. Only
/// <c>allow</c> grants a documented Factory ID; <c>ask</c> withholds and records
/// <c>safety-narrowed</c> (Factory disables <c>AskUser</c> on subagents).
/// <c>TodoWrite</c> and <c>Skill</c> are auto-injected and are not listed.
/// <c>ExitSpecMode</c> and <c>GenerateDroid</c> cannot be enabled. <c>Task</c> is not
/// available to a subagent.
/// </para>
/// <para>
/// Omitting <c>mcpServers</c> inherits the parent session's MCP tools (widening). This
/// renderer always emits <c>mcpServers: []</c> and records
/// <c>permission-not-expressible</c> that parent MCP was not inherited. Squad does not
/// invent Factory MCP server names.
/// </para>
/// <para>
/// Profile-declared shared identities suppress their skill projections per the native
/// single-projection rule (covering primary agents such as <c>conductor</c> when listed).
/// Under <see cref="SquadDeploymentScope.Global"/> the physical root is <c>~/.factory</c>,
/// so relative paths strip the <c>.factory/</c> prefix.
/// </para>
/// <para>
/// <strong>Primary-agent entry point:</strong> the Factory app dispatches custom droids as
/// subagents and offers no session-parent selector to start a conversation as one, so a
/// primary agent with a fallback profile declaring <c>no-primary-agent: skill</c> renders as
/// both a droid at <c>.factory/droids/&lt;name&gt;.md</c> (kept for Task dispatch) and an
/// entry-point skill at <c>.factory/skills/&lt;name&gt;/SKILL.md</c> the user can invoke in
/// the main session. The skill body matches the canonical agent body verbatim and its
/// resource closure is projected beside it; the skill runs under the session's permissions,
/// so the droid's explicit <c>tools</c> allow-list and <c>mcpServers: []</c> do not apply to
/// it. A canonical skill occupying the entry-point identity fails closed rather than
/// renaming either projection. Under <c>no-primary-agent: omit</c> no entry-point skill is
/// emitted.
/// </para>
/// </remarks>
public sealed class FactoryRenderer : ISquadRenderer
{
    private const string DroidsDirectory = ".factory/droids";
    private const string SkillsDirectory = ".factory/skills";

    /// <summary>
    /// Lowers the semantic capability vocabulary onto Factory's documented tool IDs,
    /// verified against docs.factory.ai/harness/subagents on 2026-09-16.
    /// <c>network.publish</c> is absent: no documented Factory tool expresses it.
    /// <c>delegate</c> is absent: Factory withholds <c>Task</c> from subagents.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityTools =
    [
        ("filesystem.read", ["Read"]),
        ("filesystem.search", ["LS", "Grep", "Glob"]),
        ("filesystem.write", ["Create", "Edit", "ApplyPatch"]),
        ("process.execute", ["Execute"]),
        ("network.read", ["WebSearch", "FetchUrl"]),
    ];

    /// <summary>
    /// Emission order, fixed so a rendered droid file is byte-stable regardless of how the
    /// profile's permissions enumerate.
    /// </summary>
    private static readonly string[] ToolOrder =
    [
        "Read",
        "LS",
        "Grep",
        "Glob",
        "Create",
        "Edit",
        "ApplyPatch",
        "Execute",
        "WebSearch",
        "FetchUrl"
    ];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithTypeConverter(new FactoryYamlFlowSequenceConverter())
        .Build();

    /// <summary>
    /// YamlDotNet does not document <see cref="ISerializer"/> as thread-safe, and the
    /// registry may dispatch renderers concurrently; serialization takes this lock so a
    /// shared static instance cannot interleave emitter state.
    /// </summary>
    private static readonly object SerializerLock = new();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Factory];

    /// <summary>
    /// Under Project scope keeps the <c>.factory/</c> prefix; under Global scope the
    /// physical root is already <c>~/.factory</c>, so the relative path is the bare
    /// <c>droids/</c> / <c>skills/</c> form.
    /// </summary>
    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        if (scope == SquadDeploymentScope.Project)
        {
            return baseDirectory;
        }

        return baseDirectory.StartsWith(".factory/", StringComparison.Ordinal)
            ? baseDirectory[".factory/".Length..]
            : baseDirectory;
    }

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Factory))
        {
            throw new ArgumentException(
                "FactoryRenderer was asked to render a target other than Factory.",
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
                        // point at .factory/skills/<name>/SKILL.md, so emitting the entry-point
                        // skill too would project one identity twice. The canonical skill is the
                        // entry point; skip the duplicate rather than failing closed.
                        if (sharedIdentities.Contains(agent.Name))
                        {
                            degradations.AddRange(BuildPrimaryAgentDegradationRecords(
                                agent,
                                fallbackProfile,
                                source.CapabilityProfiles.Profiles,
                                $"{ResolvePrefixedDirectory(SkillsDirectory, request.Scope)}/{agent.Name}/SKILL.md",
                                reusedCanonicalSkill: true));
                        }
                        else
                        {
                            // Not a declared shared identity: Factory renders both a droid and an
                            // entry-point skill for the identity, and a native target has no
                            // role-prefixed escape hatch, so this is a real collision.
                            throw new SquadRenderValidationException(
                                $"Cannot render primary agent '{agent.Name}' as a Factory entry-point " +
                                $"skill: a canonical skill named '{agent.Name}' already occupies that " +
                                "identity. Factory renders both a droid and an entry-point skill, " +
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
                    // Omit mode: the droid renders exactly as before, with no entry-point skill.
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

        string? model = ResolveFactoryModel(agent, modelProfiles);
        if (model is not null)
        {
            (string bareModel, string? reasoningEffort) = ParseReasoningEffortSuffix(model);
            frontmatter["model"] = bareModel;
            if (reasoningEffort is not null)
            {
                frontmatter["reasoningEffort"] = reasoningEffort;
            }
        }

        // Always emit tools: omitting the key allows every Factory tool (widening).
        // An empty grant is tools: [] (fail-closed), never omitted and never 'all'.
        frontmatter["tools"] = new FactoryYamlFlowSequence(ResolveTools(agent, capabilityProfiles));

        // Omitting mcpServers inherits parent MCP. Emit [] so that widening does not happen.
        frontmatter["mcpServers"] = new FactoryYamlFlowSequence([]);

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        string droidsDir = ResolvePrefixedDirectory(DroidsDirectory, scope);
        return new SquadDeploymentFile(
            $"{droidsDir}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            SquadTargetCatalog.GetToken(SquadTarget.Factory));
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
            SquadTargetCatalog.GetToken(SquadTarget.Factory));
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
            SquadTargetCatalog.GetToken(SquadTarget.Factory));
    }

    private static readonly string[] AllowedReasoningEfforts = ["low", "medium", "high"];

    /// <summary>
    /// Splits an optional trailing <c>[reasoningEffort=&lt;level&gt;]</c> suffix off a model value.
    /// Factory's droid frontmatter accepts only <c>low | medium | high</c> (docs.factory.ai,
    /// subagents), so any other level throws <see cref="SquadRenderValidationException"/>.
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
                $"Invalid reasoningEffort '{level}' in model value '{modelValue}': Factory accepts low, medium, or high.");
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

    private static string? ResolveFactoryModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("factory", out string? factoryModel))
        {
            return string.Equals(factoryModel, "inherit", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(factoryModel)
                ? null
                : factoryModel;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(profile.Default)
            ? null
            : profile.Default;
    }

    /// <summary>
    /// Lowers a capability profile onto Factory's documented tool allow-list. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction.
    /// </summary>
    private static IReadOnlyList<string> ResolveTools(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        HashSet<string> granted = new(StringComparer.Ordinal);

        // An unresolvable profile grants nothing. Falling back to "grant everything"
        // here would turn a source error into silent widening (Factory omit = all tools).
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
            Target: SquadTargetCatalog.GetToken(SquadTarget.Factory),
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "role-skill-fallback",
            InstructionDigest: agent.BodyDigest,
            Details: "The Factory app dispatches custom droids as subagents and exposes no " +
                "session-parent selector to start a conversation as one. " + entryPointDetails +
                " The entry point runs in the main session under the session's permissions — " +
                "the droid's explicit tools allow-list does not apply to it. The droid file is " +
                "kept for Task dispatch.");

        foreach (SquadDegradationRecord record in BuildDegradationRecords(
            agent,
            capabilityProfiles,
            entryPointSkillNote:
                $"The same-name entry point at {entryPointSkillPath} runs under the " +
                "session's permissions; the droid's explicit tools allow-list and " +
                "mcpServers: [] do not apply to it."))
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
                Target: SquadTargetCatalog.GetToken(SquadTarget.Factory),
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "safety-narrowed",
                InstructionDigest: agent.BodyDigest,
                Details: $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                    $"{string.Join(", ", narrowed)}. Factory subagents disable AskUser, so these " +
                    "narrow to deny and the corresponding tools are withheld from the droid's tools list.");
        }

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision != SquadPermissionDecision.Deny)
        {
            notExpressibleDetails.Add(
                "Capability 'network.publish' has no documented Factory tool; web-publish is withheld.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision != SquadPermissionDecision.Deny)
        {
            notExpressibleDetails.Add(
                "Capability 'delegate' cannot spawn subagents; Factory withholds the Task tool from custom droids.");
        }

        notExpressibleDetails.Add(
            "mcpServers: [] excludes every MCP server so parent MCP was not inherited.");

        if (entryPointSkillNote is not null)
        {
            notExpressibleDetails.Add(entryPointSkillNote);
        }

        yield return new SquadDegradationRecord(
            Target: SquadTargetCatalog.GetToken(SquadTarget.Factory),
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: string.Join(" ", notExpressibleDetails));

        SquadPermissionDecision executeDecision = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
            ? exec
            : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
            ? write
            : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: SquadTargetCatalog.GetToken(SquadTarget.Factory),
            canonicalIdentity: agent.Name,
            outputIdentity: agent.Name,
            instructionDigest: agent.BodyDigest,
            executeDecision: executeDecision,
            writeDecision: writeDecision,
            grantedShellTools: ["Execute"],
            withheldWriteTools: ["ApplyPatch", "Create", "Edit"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
        }
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="FactoryYamlFlowSequenceConverter"/>.
    /// </summary>
    private sealed class FactoryYamlFlowSequence(IEnumerable<string> values) : List<string>(values);

    /// <summary>
    /// Serializes Factory droid <c>tools</c> and <c>mcpServers</c> as inline YAML flow
    /// sequences so an empty grant is the documented form <c>[]</c> rather than an omitted key.
    /// </summary>
    private sealed class FactoryYamlFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(FactoryYamlFlowSequence);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            throw new NotSupportedException("Deserialization of FactoryYamlFlowSequence is not supported.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not FactoryYamlFlowSequence items)
            {
                return;
            }

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Flow));
            foreach (string item in items)
            {
                emitter.Emit(new Scalar(
                    AnchorName.Empty,
                    TagName.Empty,
                    item,
                    ScalarStyle.Plain,
                    isPlainImplicit: true,
                    isQuotedImplicit: true));
            }

            emitter.Emit(new SequenceEnd());
        }
    }
}

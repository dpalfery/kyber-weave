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
            frontmatter["model"] = model;
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

    private static IEnumerable<SquadDegradationRecord> BuildDegradationRecords(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
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

        yield return new SquadDegradationRecord(
            Target: SquadTargetCatalog.GetToken(SquadTarget.Factory),
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: string.Join(" ", notExpressibleDetails));
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

using System.Text;
using System.Text.Json.Nodes;
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
/// <b>Arbiter block (Req 8.1, 8.2, 22.2).</b> When the render request carries an enabled
/// <see cref="SquadArbiterWiring"/> under <see cref="SquadDeploymentScope.Project"/>, the
/// renderer returns a <c>factory</c> <see cref="SquadRenderedBlock"/> for
/// <c>.factory/hooks.json</c> holding <c>PreToolUse</c> and <c>PostToolUse</c> matcher
/// groups with matcher <c>Task</c>, each with one <c>kyber-weave-arbiter hook --harness
/// factory</c> command hook carrying the wiring timeout ([F11]). Factory reads event
/// names at the top level of <c>hooks.json</c> — no <c>hooks</c> wrapper — so each entry
/// <em>is</em> one matcher group and the block splices under the event containers. The
/// command carries no <c>--caller</c>: a shared hook file gates project-wide, not per
/// agent (design §10.4), and only documented fields are written. A null or disabled
/// wiring, or Global scope (no project configuration to enforce from, Req 22.4), renders
/// no block. Rendering the block is still not enough to splice it: when the user keeps
/// hooks under the <c>hooks</c> key of <c>.factory/settings.json</c> and
/// <c>.factory/hooks.json</c> is absent, creating one would silently disable them, so
/// <see cref="FactoryHooksShadowing"/> has the lifecycle drop the block and record
/// <c>arbiter-not-enforced</c> (§10.8, R18).
/// </para>
/// </remarks>
public sealed class FactoryRenderer : ISquadRenderer
{
    private const string DroidsDirectory = ".factory/droids";
    private const string SkillsDirectory = ".factory/skills";
    private const string ArbiterCommand = "kyber-weave-arbiter hook --harness factory";
    private const string ArbiterMatcher = "Task";

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

        // Absence and emptiness are different claims here: FactoryHooksShadowing reads
        // null as "no block was rendered" and an empty list as "a block was rendered
        // and it held nothing", and only the first is true when the wiring is absent.
        // Antigravity and Devin normalise the same way; this renderer's own byte-for-byte
        // claim is about Files, which is untouched either way.
        IReadOnlyList<SquadRenderedBlock> blocks = BuildArbiterBlocks(request);
        return Task.FromResult(new SquadRenderResult(
            true,
            files,
            degradations,
            [],
            [],
            blocks.Count > 0 ? blocks : null));
    }

    /// <summary>
    /// Builds Squad's owned <c>.factory/hooks.json</c> block, or nothing when the Arbiter
    /// is not enforced at project scope. The render stays byte-identical without the
    /// wiring because files are untouched either way: the block travels separately for the
    /// deployment plan to splice. Whether the plan may splice it at all is decided later,
    /// by <see cref="FactoryHooksShadowing"/> at plan-build time.
    /// </summary>
    private static IReadOnlyList<SquadRenderedBlock> BuildArbiterBlocks(SquadRenderRequest request)
    {
        if (request.Arbiter is null || !request.Arbiter.Enabled || request.Scope != SquadDeploymentScope.Project)
        {
            return [];
        }

        return [BuildArbiterBlock(request.Arbiter.HookTimeoutSeconds)];
    }

    private static SquadRenderedBlock BuildArbiterBlock(int timeoutSeconds) =>
        new(
            SquadTargetCatalog.GetToken(SquadTarget.Factory),
            SquadHookJsonBlock.RelativePath(SquadHookBlockFormat.Factory),
            SquadHookBlockFormat.Factory,
            [
                new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(timeoutSeconds)),
                new SquadRenderedBlockEntry("PostToolUse", MatcherGroup(timeoutSeconds)),
            ]);

    /// <summary>One F11 matcher group: event names are the file's top level, so the
    /// entry carries no <c>hooks</c> wrapper around the group itself.</summary>
    private static JsonObject MatcherGroup(int timeoutSeconds) =>
        new()
        {
            ["matcher"] = ArbiterMatcher,
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = ArbiterCommand,
                    ["timeout"] = timeoutSeconds,
                }),
        };

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

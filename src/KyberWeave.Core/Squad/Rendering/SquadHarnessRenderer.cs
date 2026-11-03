using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// The shared render pipeline every shipped harness renderer runs on: one skeleton that guards
/// the request, loads the source and assembles the result, around a harness that supplies only
/// its <see cref="SquadHarnessSpec"/> and the format decisions that are truly its own.
/// </summary>
/// <remarks>
/// <para>
/// Thirteen renderers used to restate the same guard, directory resolution, skill document,
/// model lowering and skill loop, each a little differently. A fix to one reached the others only
/// if someone copied it, and the per-harness test suites pinned the copies. The pipeline owns those
/// steps once, so a harness's code is what makes it different (ADR 0055).
/// </para>
/// <para>
/// The default <see cref="Render"/> is the native shape: each agent in source order through
/// <see cref="RenderAgent"/>, then the canonical skills, then any Arbiter wiring. A harness whose
/// output order differs (Warp lowers every agent onto a skill and projects resources last)
/// overrides <see cref="Render"/> and still uses the context's shared steps.
/// </para>
/// </remarks>
public abstract class SquadHarnessRenderer : ISquadRenderer
{
    private protected SquadHarnessRenderer(SquadHarnessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Spec = spec;
        SupportedTargets = [spec.Target];
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; }

    private protected SquadHarnessSpec Spec { get; }

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != Spec.Target))
        {
            throw new ArgumentException(
                $"{GetType().Name} was asked to render a target other than {Spec.DisplayName}.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadHarnessRenderContext context = new(Spec, request, SquadRenderSource.Load(request, Spec.Target));
        return Task.FromResult(Render(context));
    }

    /// <summary>Renders the loaded source; the default is the native agent-then-skill shape.</summary>
    private protected virtual SquadRenderResult Render(SquadHarnessRenderContext context)
    {
        foreach (SquadAgent agent in context.Source.Agents)
        {
            RenderAgent(context, agent);
        }

        RenderCanonicalSkills(context);
        RenderArbiter(context);
        return context.ToResult();
    }

    /// <summary>Emits one canonical agent's projection, resources and degradation records.</summary>
    private protected abstract void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent);

    /// <summary>Emits every canonical skill a native target does not suppress.</summary>
    private protected virtual void RenderCanonicalSkills(SquadHarnessRenderContext context) =>
        context.EmitCanonicalSkills(skill => context.RenderSkillDocument(skill.Name, skill.Description, skill.InstructionBody));

    /// <summary>Emits Arbiter wiring when the request enables it; the default emits nothing.</summary>
    private protected virtual void RenderArbiter(SquadHarnessRenderContext context)
    {
    }
}

/// <summary>
/// One render in progress: the request and loaded source, the output being accumulated, and the
/// steps every harness shares.
/// </summary>
internal sealed class SquadHarnessRenderContext
{
    private HashSet<string>? _sharedIdentities;
    private HashSet<string>? _suppressedSkillIdentities;
    private HashSet<string>? _skillNames;
    private string[]? _capabilityVocabulary;
    private object? _harnessState;

    public SquadHarnessRenderContext(SquadHarnessSpec spec, SquadRenderRequest request, SquadSource source)
    {
        Spec = spec;
        Request = request;
        Source = source;
    }

    public SquadHarnessSpec Spec { get; }

    public SquadRenderRequest Request { get; }

    public SquadSource Source { get; }

    public SquadDeploymentScope Scope => Request.Scope;

    public string Token => Spec.Token;

    public List<SquadDeploymentFile> Files { get; } = [];

    public List<SquadDegradationRecord> Degradations { get; } = [];

    public List<SquadRenderedBlock> Blocks { get; } = [];

    public List<SquadRenderWarning> Warnings { get; } = [];

    public IReadOnlyDictionary<string, SquadCapabilityProfile> CapabilityProfiles =>
        Source.CapabilityProfiles.Profiles;

    public IReadOnlyDictionary<string, SquadModelProfile> ModelProfiles => Source.ModelProfiles.Profiles;

    public string AgentsDirectory => ResolveDirectory(
        Spec.AgentsSubdirectory ??
        throw new InvalidOperationException($"Harness '{Token}' has no agent directory."));

    public string SkillsDirectory => ResolveDirectory("skills");

    /// <summary>Every profile's shared identities, read from source rather than a renderer roster.</summary>
    public IReadOnlySet<string> SharedIdentities => _sharedIdentities ??= Source.FallbackProfiles.Profiles.Values
        .SelectMany(profile => profile.SharedIdentities)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The shared identities whose canonical skill a native target suppresses because the agent
    /// file carries the identical body. A primary shared identity's skill is its entry point and is
    /// never suppressed.
    /// </summary>
    public IReadOnlySet<string> SuppressedSkillIdentities => _suppressedSkillIdentities ??=
        SquadSharedIdentityProjection.SuppressedSkillIdentities(Source, SharedIdentities);

    public IReadOnlySet<string> SkillNames => _skillNames ??= Source.Skills
        .Select(skill => skill.Name)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The declared capability vocabulary, sorted: a capability added to
    /// <c>profiles/capabilities.yml</c> appears in degradation text without a renderer change.
    /// </summary>
    public IReadOnlyList<string> CapabilityVocabulary => _capabilityVocabulary ??=
        [.. Source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

    /// <summary>
    /// The Arbiter wiring to render, or null. A null or disabled wiring renders byte for byte as
    /// before the field existed, and global scope has no project configuration to enforce from
    /// (Req 22.4).
    /// </summary>
    public SquadArbiterWiring? EnabledArbiter =>
        Request.Arbiter is { Enabled: true } && Scope == SquadDeploymentScope.Project ? Request.Arbiter : null;

    /// <summary>
    /// The harness's own per-render state, created once on first use: a renderer is shared across
    /// concurrent renders, so state that would otherwise be a field lives on the render instead.
    /// </summary>
    public T GetOrCreateState<T>(Func<SquadHarnessRenderContext, T> create)
        where T : class =>
        (T)(_harnessState ??= create(this));

    public string ResolveDirectory(string subdirectory) => Spec.ResolveDirectory(subdirectory, Scope);

    public string? ResolveModel(SquadAgent agent) =>
        SquadHarnessLowering.ResolveModel(agent, ModelProfiles, Token, Spec.ModelPolicy);

    public string SingleLine(string value) => SquadHarnessLowering.SingleLine(value, Spec.DescriptionLineBreaks);

    public SquadDeploymentFile CreateFile(string relativePath, string content) =>
        new(relativePath, Encoding.UTF8.GetBytes(content), Token);

    /// <summary>Adds a rendered principal and projects its resource closure beside it.</summary>
    public void AddPrincipal(SquadDeploymentFile principal, IReadOnlyList<SquadResource> resources)
    {
        Files.Add(principal);
        SquadResourceProjection.Append(Files, principal, resources);
    }

    public void AddDegradation(SquadDegradationRecord? record)
    {
        if (record is not null)
        {
            Degradations.Add(record);
        }
    }

    /// <summary>A degradation record for <paramref name="agent"/> under its own name on this target.</summary>
    public SquadDegradationRecord Degradation(SquadAgent agent, string code, string details) =>
        new(Token, agent.Name, agent.Name, code, agent.BodyDigest, details);

    /// <summary>
    /// Decides how a primary-invocation agent projects on a harness with no primary-agent
    /// primitive, from its fallback profile's <c>no-primary-agent</c> value. A canonical skill
    /// already holding the identity is reused when the identity is declared shared, and otherwise
    /// fails closed with <paramref name="collisionMessage"/>: a native target has no <c>role-</c>
    /// escape hatch.
    /// </summary>
    public SquadPrimaryLowering ResolvePrimaryLowering(SquadAgent agent, Func<string> collisionMessage)
    {
        SquadFallbackProfile fallbackProfile = Source.FallbackProfiles.Profiles[agent.Fallback];
        if (string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal))
        {
            if (!SkillNames.Contains(agent.Name))
            {
                return SquadPrimaryLowering.EntryPointSkill;
            }

            return SharedIdentities.Contains(agent.Name)
                ? SquadPrimaryLowering.ReuseCanonicalSkill
                : throw new SquadRenderValidationException(collisionMessage());
        }

        if (string.Equals(fallbackProfile.NoPrimaryAgent, "omit", StringComparison.Ordinal))
        {
            return SquadPrimaryLowering.Omit;
        }

        throw new SquadRenderValidationException(
            $"Fallback profile '{agent.Fallback}' declares unsupported no-primary-agent " +
            $"value '{fallbackProfile.NoPrimaryAgent}' for primary agent '{agent.Name}'.");
    }

    /// <summary>
    /// The declared capabilities <paramref name="agent"/>'s profile does not deny, in vocabulary
    /// order; empty when the profile does not resolve.
    /// </summary>
    public IReadOnlyList<string> NonDenyCapabilities(SquadAgent agent) =>
        CapabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile)
            ? CapabilityVocabulary
                .Where(capability =>
                    profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                    decision != SquadPermissionDecision.Deny)
                .ToList()
            : [];

    /// <summary>
    /// The record for capability decisions a harness cannot express, or null when none are
    /// constrained. <paramref name="reason"/> completes "but …" and names why the harness cannot
    /// express them.
    /// </summary>
    public SquadDegradationRecord? CapabilitiesNotExpressible(
        SquadAgent agent,
        IReadOnlyList<string> constrained,
        string reason,
        string artifact = "agent") =>
        constrained.Count == 0
            ? null
            : Degradation(
                agent,
                "permission-not-expressible",
                $"Capability profile '{agent.CapabilityProfile}' constrains {string.Join(", ", constrained)} " +
                $"but {reason}; the deployed {artifact}'s behaviour is governed by the harness default, not " +
                "the canonical profile.");

    /// <summary>
    /// Adds an agent file whose in-closure <c>.md</c> links are rewritten to <c>.markdown</c>, and
    /// projects its resources under that guard, for a harness whose agent loader scans
    /// <paramref name="discoveryGlob"/> and would register a projected <c>.md</c> resource as an
    /// agent (ADR 0040).
    /// </summary>
    public void AddDiscoveryGuardedAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, object?> frontmatter,
        SquadYamlEmitter emitter,
        string discoveryGlob)
    {
        SquadDeploymentFile principal = CreateFile(
            $"{AgentsDirectory}/{agent.Name}.md",
            emitter.Compose(frontmatter, SquadAgentResourceDiscoveryGuard.RewriteBody(agent)));
        Files.Add(principal);
        SquadAgentResourceDiscoveryGuard.Append(
            Files,
            agent,
            principal,
            agent.Resources,
            Degradations,
            Token,
            discoveryGlob);
    }

    /// <summary>Adds the owned Arbiter plugin shim when the request enables Arbiter.</summary>
    public void AddArbiterPluginShim(string relativePath)
    {
        if (EnabledArbiter is not null)
        {
            Files.Add(CreateFile(relativePath, ArbiterPluginShim.Render(Token)));
        }
    }

    /// <summary>
    /// Adds Squad's owned block for a shared hook file whose containers are <c>PreToolUse</c> and
    /// <c>PostToolUse</c> matcher groups, when the request enables Arbiter. Files are untouched
    /// either way: the block travels separately for the deployment plan to splice.
    /// </summary>
    public void AddArbiterMatcherGroupBlock(SquadHookBlockFormat format, string matcher)
    {
        if (EnabledArbiter is not { } arbiter)
        {
            return;
        }

        string commandLine = ArbiterHookWiring.HookCommandLine(Token);
        Blocks.Add(new SquadRenderedBlock(
            Token,
            SquadHookJsonBlock.RelativePath(format),
            format,
            [
                new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(matcher, commandLine, arbiter.HookTimeoutSeconds)),
                new SquadRenderedBlockEntry("PostToolUse", MatcherGroup(matcher, commandLine, arbiter.HookTimeoutSeconds))
            ]));
    }

    /// <summary>
    /// The skill document every harness shares: <c>name</c>, a single-line <c>description</c> and
    /// <c>license: MIT</c>, in that order, at <c>skills/&lt;name&gt;/SKILL.md</c>, then any
    /// <paramref name="additionalFrontmatter"/> a harness's entry-point skill carries. No model or
    /// permission keys: a skill cannot express the capability lattice.
    /// </summary>
    public SquadDeploymentFile RenderSkillDocument(
        string name,
        string description,
        string instructionBody,
        SquadYamlEmitter? emitter = null,
        IReadOnlyDictionary<string, object?>? additionalFrontmatter = null)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = SingleLine(description),
            ["license"] = "MIT"
        };
        if (additionalFrontmatter is not null)
        {
            foreach ((string key, object? value) in additionalFrontmatter)
            {
                frontmatter[key] = value;
            }
        }

        return CreateFile(
            $"{SkillsDirectory}/{name}/SKILL.md",
            (emitter ?? SquadYamlEmitter.Default).Compose(frontmatter, instructionBody));
    }

    /// <summary>
    /// Emits each canonical skill through <paramref name="render"/>, with its resources, except a
    /// suppressed shared identity, which has one projection: the agent file.
    /// </summary>
    public void EmitCanonicalSkills(Func<SquadSkill, SquadDeploymentFile> render)
    {
        foreach (SquadSkill skill in Source.Skills)
        {
            if (SuppressedSkillIdentities.Contains(skill.Name))
            {
                continue;
            }

            AddPrincipal(render(skill), skill.Resources);
        }
    }

    /// <summary>
    /// The accumulated result. <paramref name="emptyBlocks"/> keeps an empty block list rather than
    /// null, for the renderers whose result always carried one.
    /// </summary>
    public SquadRenderResult ToResult(bool emptyBlocks = false) =>
        new(true, Files, Degradations, Warnings, [], Blocks.Count > 0 || emptyBlocks ? Blocks : null);

    private static JsonObject MatcherGroup(string matcher, string commandLine, int timeoutSeconds) =>
        new()
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = commandLine,
                    ["timeout"] = timeoutSeconds
                })
        };

    /// <summary>
    /// Rejects a canonical name that is not a single portable path segment, before any file is
    /// built from it.
    /// </summary>
    public static void ValidateCanonicalName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.Trim('.').Length == 0)
        {
            throw new SquadRenderValidationException(
                $"Canonical name '{name}' is not a valid single-segment path for rendering.");
        }
    }
}

/// <summary>How a primary-invocation agent projects where the harness has no primary-agent primitive.</summary>
internal enum SquadPrimaryLowering
{
    /// <summary><c>no-primary-agent: skill</c>: the agent body renders as its own entry-point skill.</summary>
    EntryPointSkill,

    /// <summary>
    /// <c>no-primary-agent: skill</c> on a shared identity: the same-name canonical skill is the
    /// entry point, so nothing is emitted for the agent beyond what the skill loop emits.
    /// </summary>
    ReuseCanonicalSkill,

    /// <summary><c>no-primary-agent: omit</c>: no entry point is emitted.</summary>
    Omit
}

/// <summary>A YAML serializer that is safe to share across concurrent renders.</summary>
/// <remarks>
/// YamlDotNet does not document <see cref="ISerializer"/> as thread-safe, and the registry may
/// dispatch renderers concurrently, so every serialization takes this emitter's lock.
/// </remarks>
internal sealed class SquadYamlEmitter
{
    private readonly ISerializer _serializer;
    private readonly Lock _gate = new();

    public SquadYamlEmitter(ISerializer serializer)
    {
        _serializer = serializer;
    }

    /// <summary>The plain serializer, for harnesses whose frontmatter needs no type converter.</summary>
    public static SquadYamlEmitter Default { get; } = new(new SerializerBuilder().Build());

    public string Serialize(object graph)
    {
        lock (_gate)
        {
            return _serializer.Serialize(graph);
        }
    }

    /// <summary>Composes a frontmatter-plus-body Markdown document.</summary>
    public string Compose(IReadOnlyDictionary<string, object?> frontmatter, string body) =>
        SquadMarkdownDocument.Compose(Serialize(frontmatter), body);
}

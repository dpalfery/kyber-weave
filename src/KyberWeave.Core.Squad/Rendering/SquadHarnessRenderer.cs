using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Validation;
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
/// steps once, so a harness's code is what makes it different (ADR 0056).
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
    /// <summary>
    /// Where canonical text names the harness that will run it, e.g. a run-start step that has to
    /// report which harness it started.
    /// </summary>
    private const string HarnessPlaceholder = "{{harness}}";

    private protected SquadHarnessRenderer(SquadHarnessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Spec = spec;
        SupportedTargets = [spec.Target];
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; }

    /// <summary>The harness facts this renderer runs on; the shared contract suite reads them too.</summary>
    internal SquadHarnessSpec Spec { get; }

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

        // Argument and target errors above throw at the call; a failure loading or rendering the
        // source faults the returned task, as an asynchronous method's failure does.
        try
        {
            SquadSource source = SquadRenderSource.Load(request, Spec.Target);

            // Path safety first, then the same validation the loader applies to source read from
            // disk, so a preloaded source is held to every reference rule (profiles, target-scoped
            // profiles, shared identities) rather than to a renderer-local subset of them.
            ValidateOutputNames(source);
            SquadSourceValidator.Validate(
                source.Bundle,
                source.Agents,
                source.Skills,
                source.ModelProfiles,
                source.CapabilityProfiles,
                source.FallbackProfiles);

            // The canonical text carries a {{harness}} placeholder where the name of the harness
            // running it belongs; this is the one place it is answered, after validation so every
            // source rule still reads what the author wrote.
            //
            // The shared worker baseline is applied here for the same reason: it is one paragraph, it
            // reaches every worker on every round trip, and this pipeline is the only step all
            // thirteen harnesses share, so a rule written once cannot reach twelve of them.
            SquadSource renderSource = SquadWorkerBaselineInjector.Apply(
                SubstituteHarnessToken(source, Spec.Token),
                SquadWorkerBaselineInjector.ReadBlock(request.SourceDirectory));
            return Task.FromResult(Render(new SquadHarnessRenderContext(Spec, request, renderSource)));
        }
        catch (OperationCanceledException canceled) when (canceled.CancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<SquadRenderResult>(canceled.CancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<SquadRenderResult>(exception);
        }
    }

    /// <summary>
    /// Canonical names and resource paths become directory and file names in every harness's tree,
    /// so anything carrying path syntax is rejected here, before any file is built from it, rather
    /// than left to the registry's portable-path check after the render. Only path syntax is
    /// rejected: a name such as "a..b" is one portable segment and renders as it always has.
    /// </summary>
    private static void ValidateOutputNames(SquadSource source)
    {
        foreach (SquadAgent agent in source.Agents)
        {
            SquadHarnessRenderContext.ValidatePathSegment(agent.Name);
            ValidateResourcePaths(agent.Name, agent.Resources);
        }

        foreach (SquadSkill skill in source.Skills)
        {
            SquadHarnessRenderContext.ValidatePathSegment(skill.Name);
            ValidateResourcePaths(skill.Name, skill.Resources);
        }
    }

    /// <summary>
    /// Holds each resource path to the same portable-path policy the registry applies to the
    /// rendered tree, so the early gate and the final check cannot disagree about what is safe.
    /// </summary>
    private static void ValidateResourcePaths(string owner, IReadOnlyList<SquadResource> resources)
    {
        foreach (SquadResource resource in resources)
        {
            try
            {
                if (string.Equals(SquadPathPolicy.NormalizeRelativePath(resource.RelativePath), resource.RelativePath, StringComparison.Ordinal))
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is SquadPathContainmentException or SquadDeploymentConflictException)
            {
                throw new SquadRenderValidationException(
                    $"Resource '{resource.RelativePath}' owned by '{owner}' is not a portable relative path: {exception.Message}",
                    exception);
            }

            throw new SquadRenderValidationException(
                $"Resource '{resource.RelativePath}' owned by '{owner}' is not a canonical portable relative path.");
        }
    }

    /// <summary>
    /// Replaces the canonical <see cref="HarnessPlaceholder"/> with <paramref name="token"/>, over
    /// every piece of canonical text a render can emit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deployed run cannot work out which harness it is in from its surroundings: the folder it
    /// happened to run in was wrong once (a Command Code run inside the <c>Devin</c> checkout), and
    /// so was an environment variable (<c>OPENCODE_CONFIG</c> set in a Claude session). So the
    /// canonical text asks the question and the catalogue answers it here, per target.
    /// </para>
    /// <para>
    /// This is the whole of the substitution, deliberately: agent bodies, skill bodies,
    /// descriptions and resource content are the surfaces a harness's prose travels on, and every
    /// renderer reaches all of them through this pipeline. Substituting per renderer is what would
    /// leave the defect this method exists to prevent, where an agent file carries the token and
    /// its skill projection still carries the braces.
    /// </para>
    /// <para>
    /// A text with no placeholder comes back as the same string instance, so a corpus that has not
    /// adopted the placeholder renders byte for byte as it did before.
    /// </para>
    /// </remarks>
    private static SquadSource SubstituteHarnessToken(SquadSource source, string token)
    {
        static string ReplaceText(string text, string replacement) =>
            text.Replace(HarnessPlaceholder, replacement, StringComparison.Ordinal);

        static IReadOnlyList<SquadResource> ReplaceResources(IReadOnlyList<SquadResource> resources, string replacement) =>
            [.. resources.Select(resource => resource with { Content = ReplaceText(resource.Content, replacement) })];

        return source with
        {
            Agents = [.. source.Agents.Select(agent => agent with
            {
                Description = ReplaceText(agent.Description, token),
                InstructionBody = ReplaceText(agent.InstructionBody, token),
                Resources = ReplaceResources(agent.Resources, token)
            })],
            Skills = [.. source.Skills.Select(skill => skill with
            {
                Description = ReplaceText(skill.Description, token),
                InstructionBody = ReplaceText(skill.InstructionBody, token),
                Resources = ReplaceResources(skill.Resources, token)
            })]
        };
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
    private readonly Dictionary<Type, object> _harnessState = [];

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
    /// before the field existed. An enabled request stays visible at global scope so the render
    /// can lower the request's global-scope command-line context without changing the byte-wise
    /// output of an omitted or disabled wiring.
    /// </summary>
    public SquadArbiterWiring? EnabledArbiter =>
        Request.Arbiter is { Enabled: true } ? Request.Arbiter : null;

    /// <summary>
    /// The harness's own per-render state, created once on first use: a renderer is shared across
    /// concurrent renders, so state that would otherwise be a field lives on the render instead.
    /// </summary>
    public T GetOrCreateState<T>(Func<SquadHarnessRenderContext, T> create)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(create);
        if (_harnessState.TryGetValue(typeof(T), out object? existing))
        {
            return (T)existing;
        }

        T created = create(this) ?? throw new InvalidOperationException(
            $"Harness '{Token}' produced no {typeof(T).Name} render state.");
        _harnessState.Add(typeof(T), created);
        return created;
    }

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

    /// <summary>The fallback profile <paramref name="agent"/> names, failing closed when none is declared.</summary>
    public SquadFallbackProfile FallbackProfileFor(SquadAgent agent) =>
        Source.FallbackProfiles.Profiles.TryGetValue(agent.Fallback, out SquadFallbackProfile? profile)
            ? profile
            : throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' references fallback profile '{agent.Fallback}', which " +
                "profiles/fallbacks.yml does not declare. Declared profiles: " +
                $"{string.Join(", ", Source.FallbackProfiles.Profiles.Keys.Order(StringComparer.Ordinal))}.");

    /// <summary>
    /// Decides how a primary-invocation agent projects on a harness with no primary-agent
    /// primitive, from its fallback profile's <c>no-primary-agent</c> value. A canonical skill
    /// already holding the identity is reused when the identity is declared shared, and otherwise
    /// fails closed with <paramref name="collisionMessage"/>: a native target has no <c>role-</c>
    /// escape hatch.
    /// </summary>
    public SquadPrimaryLowering ResolvePrimaryLowering(SquadAgent agent, Func<string> collisionMessage)
    {
        SquadFallbackProfile fallbackProfile = FallbackProfileFor(agent);
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
    /// Lowers a primary-invocation agent onto a harness with no primary-agent primitive: onto the
    /// form <paramref name="renderLowered"/> emits (a skill or a command) for
    /// <c>no-primary-agent: skill</c>, onto the same-name canonical skill for a shared identity,
    /// or onto nothing for <c>omit</c>, which records <c>omitted</c>.
    /// </summary>
    /// <param name="agent">The primary-invocation agent.</param>
    /// <param name="collisionMessage">The fail-closed message when a canonical skill already holds the identity.</param>
    /// <param name="renderLowered">Renders the lowered form.</param>
    /// <param name="loweredRecords">
    /// The records for a lowered agent, given its fallback profile and whether it reuses the
    /// canonical skill.
    /// </param>
    public void AddLoweredPrimaryAgent(
        SquadAgent agent,
        Func<string> collisionMessage,
        Func<SquadDeploymentFile> renderLowered,
        Func<SquadFallbackProfile, bool, IEnumerable<SquadDegradationRecord>> loweredRecords)
    {
        SquadPrimaryLowering lowering = ResolvePrimaryLowering(agent, collisionMessage);
        SquadFallbackProfile fallbackProfile = FallbackProfileFor(agent);
        switch (lowering)
        {
            case SquadPrimaryLowering.ReuseCanonicalSkill:
                // The canonical skill is the entry point and is emitted by the skill loop, so no
                // lowered form is emitted here; one identity, one projection.
                Degradations.AddRange(loweredRecords(fallbackProfile, true));
                break;
            case SquadPrimaryLowering.EntryPointSkill:
                AddPrincipal(renderLowered(), agent.Resources);
                Degradations.AddRange(loweredRecords(fallbackProfile, false));
                break;
            default:
                Degradations.Add(Degradation(
                    agent,
                    "omitted",
                    $"Fallback profile '{agent.Fallback}' declares no-primary-agent: omit; " +
                    $"{Spec.DisplayName} has no primary-agent primitive for this agent to render onto, " +
                    "so no file is emitted."));
                break;
        }
    }

    /// <summary>
    /// The dual projection a harness uses when it renders a primary agent through an additional
    /// entry-point form (ADR 0034): the agent file is already emitted, and a primary agent whose
    /// profile declares <c>no-primary-agent: skill</c> also renders as an entry-point skill, unless
    /// the same-name canonical skill already is the entry point. <paramref name="agentForm"/> names
    /// the agent file's form in the fail-closed collision message, e.g. <c>a droid</c>.
    /// </summary>
    /// <param name="agent">The canonical agent, already emitted as an agent file.</param>
    /// <param name="agentForm">How the collision message names the agent file's form.</param>
    /// <param name="renderEntryPointSkill">Renders the entry-point skill.</param>
    /// <param name="entryPointRecords">
    /// The records for a primary agent with an entry point, given its fallback profile, the entry
    /// point's path and whether it is the reused canonical skill.
    /// </param>
    /// <param name="agentRecords">The records for any other agent.</param>
    public void AddEntryPointSkillProjection(
        SquadAgent agent,
        string agentForm,
        Func<SquadDeploymentFile> renderEntryPointSkill,
        Func<SquadFallbackProfile, string, bool, IEnumerable<SquadDegradationRecord>> entryPointRecords,
        Func<IEnumerable<SquadDegradationRecord>> agentRecords)
    {
        if (agent.Invocation != SquadInvocation.Primary)
        {
            Degradations.AddRange(agentRecords());
            return;
        }

        SquadPrimaryLowering lowering = ResolvePrimaryLowering(agent, () =>
            $"Cannot render primary agent '{agent.Name}' as a {Spec.DisplayName} entry-point " +
            $"skill: a canonical skill named '{agent.Name}' already occupies that " +
            $"identity. {Spec.DisplayName} renders both {agentForm} and an entry-point skill, " +
            "so this is a fail-closed condition rather than a naming collision " +
            "this renderer can resolve on its own.");
        SquadFallbackProfile fallbackProfile = FallbackProfileFor(agent);
        switch (lowering)
        {
            case SquadPrimaryLowering.ReuseCanonicalSkill:
                // A declared shared identity already has its canonical skill as the entry point, so
                // emitting the entry-point skill too would project one identity twice.
                Degradations.AddRange(entryPointRecords(fallbackProfile, $"{SkillsDirectory}/{agent.Name}/SKILL.md", true));
                break;
            case SquadPrimaryLowering.EntryPointSkill:
                SquadDeploymentFile skill = renderEntryPointSkill();
                AddPrincipal(skill, agent.Resources);
                Degradations.AddRange(entryPointRecords(fallbackProfile, skill.RelativePath, false));
                break;
            default:
                // Omit mode: the agent file renders exactly as before, with no entry-point skill.
                Degradations.AddRange(agentRecords());
                break;
        }
    }

    /// <summary>
    /// The declared capabilities <paramref name="agent"/>'s profile does not deny, in vocabulary
    /// order.
    /// </summary>
    public IReadOnlyList<string> NonDenyCapabilities(SquadAgent agent)
    {
        // An unresolvable profile is a source error, not an absence of constraints: reading it as
        // "nothing constrained" would drop the record that says the harness cannot enforce them.
        if (!CapabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' references capability profile '{agent.CapabilityProfile}', which " +
                "profiles/capabilities.yml does not declare. Declared profiles: " +
                $"{string.Join(", ", CapabilityProfiles.Keys.Order(StringComparer.Ordinal))}.");
        }

        return CapabilityVocabulary
            .Where(capability =>
                profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision != SquadPermissionDecision.Deny)
            .ToList();
    }

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
    public void AddArbiterPluginShim(string projectRelativePath, string? globalRelativePath = null)
    {
        if (EnabledArbiter is null)
        {
            return;
        }

        string relativePath = Scope == SquadDeploymentScope.Global
            ? globalRelativePath ?? projectRelativePath
            : projectRelativePath;

        Files.Add(CreateFile(relativePath, ArbiterPluginShim.Render(Token, Scope)));
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

        string commandLine = ArbiterHookWiring.HookCommandLine(Token, Scope);
        string relativePath = Scope == SquadDeploymentScope.Global
            && SquadHookJsonBlock.RelativePath(format).StartsWith("." + Token + "/", StringComparison.OrdinalIgnoreCase)
            ? SquadHookJsonBlock.RelativePath(format)[(Token.Length + 2)..]
            : SquadHookJsonBlock.RelativePath(format);
        Blocks.Add(new SquadRenderedBlock(
            Token,
            relativePath,
            format,
            [
                new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(matcher, commandLine, arbiter.HookTimeoutSeconds)),
                new SquadRenderedBlockEntry("PostToolUse", MatcherGroup(matcher, commandLine, arbiter.HookTimeoutSeconds))
            ]));
    }

    /// <summary>
    /// The skill document every harness shares: <c>name</c>, a single-line <c>description</c> and
    /// the spec's <c>license</c>, in that order, at <c>skills/&lt;name&gt;/SKILL.md</c>, then any
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
            ["description"] = SingleLine(description)
        };
        if (Spec.SkillLicense is not null)
        {
            frontmatter["license"] = Spec.SkillLicense;
        }

        if (additionalFrontmatter is not null)
        {
            foreach ((string key, object? value) in additionalFrontmatter)
            {
                // The shared keys are the document's identity; an entry-point extra may add keys,
                // never replace them.
                if (!frontmatter.TryAdd(key, value) || string.Equals(key, "license", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Skill frontmatter key '{key}' is reserved by the shared skill document.",
                        nameof(additionalFrontmatter));
                }
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
    /// Rejects a canonical name that would not stay one portable path segment: blank, carrying a
    /// separator, made only of dots (<c>.</c>, <c>..</c>), or failing the registry's
    /// <see cref="SquadPathPolicy"/> segment rules.
    /// </summary>
    public static void ValidatePathSegment(string name)
    {
        bool portable;
        try
        {
            portable = !string.IsNullOrWhiteSpace(name) &&
                       !name.Contains('/', StringComparison.Ordinal) &&
                       name.Trim('.').Length != 0 &&
                       string.Equals(SquadPathPolicy.NormalizeRelativePath(name), name, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is SquadPathContainmentException or SquadDeploymentConflictException)
        {
            portable = false;
        }

        if (!portable)
        {
            throw new SquadRenderValidationException(
                $"Canonical name '{name}' is not a valid single-segment path for rendering.");
        }
    }

    /// <summary>
    /// The stricter name check Warp and Antigravity have always applied: everything
    /// <see cref="ValidatePathSegment"/> rejects, plus any name containing <c>..</c>.
    /// </summary>
    public static void ValidateCanonicalName(string name)
    {
        ValidatePathSegment(name);
        if (name.Contains("..", StringComparison.Ordinal))
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

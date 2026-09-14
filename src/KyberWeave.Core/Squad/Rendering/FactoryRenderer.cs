using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Factory Droids' native agent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Native agent target: canonical agents render as Markdown with YAML frontmatter at
/// <c>.factory/agents/&lt;name&gt;.md</c>. Skills render at
/// <c>.factory/skills/&lt;name&gt;/SKILL.md</c>. Frontmatter requires <c>name</c> and
/// <c>description</c>; optional <c>model</c> resolves from <c>models.yml</c> for harness
/// <c>factory</c> (omitted when <c>inherit</c> or unresolved). The Markdown-with-frontmatter
/// shape matches every other native Markdown harness in this repository; Factory's own
/// public schema was not verified against live documentation at implementation time
/// (2026-09-14), so no additional keys are invented.
/// </para>
/// <para>
/// Permission degradation follows Copilot's degradation-over-guessing rule and Codex's
/// concrete pattern: Factory's agent frontmatter permission model is unverified, so no
/// permission-equivalent field is emitted. Non-deny profile decisions are recorded as
/// <see cref="SquadDegradationRecord"/> with code <c>permission-not-expressible</c> rather
/// than guessing a mapping that could silently widen access.
/// </para>
/// <para>
/// Profile-declared shared identities suppress their skill projections per the native
/// single-projection rule (covering primary agents such as <c>conductor</c> when listed).
/// </para>
/// </remarks>
public sealed class FactoryRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".factory/agents";
    private const string SkillsDirectory = ".factory/skills";

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    /// <summary>
    /// YamlDotNet does not document <see cref="ISerializer"/> as thread-safe, and the
    /// registry may dispatch renderers concurrently; serialization takes this lock so a
    /// shared static instance cannot interleave emitter state.
    /// </summary>
    private static readonly object SerializerLock = new();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Factory];

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

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in degradation text without a renderer
        // change. Sorted for deterministic details strings.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile principal = RenderAgent(
                agent,
                source.ModelProfiles.Profiles);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, agent.Resources);

            SquadDegradationRecord? degradation = BuildDegradationRecord(
                agent,
                source.CapabilityProfiles.Profiles,
                capabilityVocabulary);
            if (degradation is not null)
            {
                degradations.Add(degradation);
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

            SquadDeploymentFile principal = RenderSkill(skill);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, skill.Resources);
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
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

        // Permission-equivalent fields are deliberately omitted: Factory's permission
        // vocabulary is unverified, and inventing one would risk silent widening.

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        return new SquadDeploymentFile(
            $"{AgentsDirectory}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            SquadTargetCatalog.GetToken(SquadTarget.Factory));
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

    private static SquadDegradationRecord? BuildDegradationRecord(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            return null;
        }

        List<string> unexpressed = capabilityVocabulary
            .Where(cap => profile.Permissions.TryGetValue(cap, out SquadPermissionDecision decision) &&
                          decision != SquadPermissionDecision.Deny)
            .ToList();

        if (unexpressed.Count == 0)
        {
            return null;
        }

        string details =
            $"Capability profile '{agent.CapabilityProfile}' constrains {string.Join(", ", unexpressed)} but " +
            "Factory agent frontmatter has no verified permission mapping; no permission-equivalent " +
            "field was emitted, so the deployed agent's behaviour is governed by the harness default, " +
            "not the canonical profile.";

        return new SquadDegradationRecord(
            Target: SquadTargetCatalog.GetToken(SquadTarget.Factory),
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: details);
    }
}

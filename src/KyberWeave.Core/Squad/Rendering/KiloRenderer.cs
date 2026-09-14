using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Kilo's native agent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Native agent target: canonical agents render as Markdown with YAML frontmatter at
/// <c>.kilo/agents/&lt;name&gt;.md</c>. Required keys are <c>name</c>, <c>description</c>, and <c>mode</c>
/// (<c>primary</c> or <c>subagent</c>); optional <c>model</c> resolves from <c>models.yml</c> for harness <c>kilo</c>
/// (omitted when <c>inherit</c> or empty).
/// </para>
/// <para>
/// Canonical skills render as harness skills at <c>.kilo/skills/&lt;name&gt;/SKILL.md</c>
/// with YAML frontmatter containing <c>name</c>, single-line <c>description</c>, and <c>license: MIT</c>.
/// Per the native single-projection rule, profile-declared shared identities suppress their
/// skill projections.
/// </para>
/// <para>
/// Permission degradation: Kilo agent configuration has no frontmatter tool allow-list or
/// capability permission lattice. Configured profile decisions (including allow, ask, and deny)
/// are recorded as structured degradations with code <c>permission-not-expressible</c> rather
/// than inventing unenforceable fields or silently dropping constraints (preventing capability widening).
/// </para>
/// </remarks>
public sealed class KiloRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".kilo/agents";
    private const string SkillsDirectory = ".kilo/skills";

    private static readonly ThreadLocal<ISerializer> YamlSerializer = new(
        () => new SerializerBuilder().Build());

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Kilo];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Kilo))
        {
            throw new ArgumentException(
                "KiloRenderer was asked to render a target other than Kilo.",
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
            ["description"] = agent.Description,
            ["mode"] = agent.Invocation == SquadInvocation.Primary ? "primary" : "subagent"
        };

        string? model = ResolveKiloModel(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        string content = SquadMarkdownDocument.Compose(
            YamlSerializer.Value!,
            frontmatter,
            agent.InstructionBody);

        return new SquadDeploymentFile(
            $"{AgentsDirectory}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            "kilo");
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

        string content = SquadMarkdownDocument.Compose(
            YamlSerializer.Value!,
            frontmatter,
            skill.InstructionBody);

        return new SquadDeploymentFile(
            $"{SkillsDirectory}/{skill.Name}/SKILL.md",
            Encoding.UTF8.GetBytes(content),
            "kilo");
    }

    private static string? ResolveKiloModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("kilo", out string? kiloModel))
        {
            return string.Equals(kiloModel, "inherit", StringComparison.Ordinal)
                ? null
                : kiloModel;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal)
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

        // Kilo has no capability permission lattice. All configured permissions (allow, ask,
        // deny) are unexpressed at the harness boundary; recording deny constraints ensures
        // canonical restrictions are not silently dropped (preventing capability widening).
        List<string> unexpressed = capabilityVocabulary
            .Where(cap => profile.Permissions.ContainsKey(cap))
            .ToList();

        if (unexpressed.Count == 0)
        {
            return null;
        }

        string details =
            $"Capability profile '{agent.CapabilityProfile}' constrains {string.Join(", ", unexpressed)} but Kilo agents cannot express capability permissions; the deployed agent's behaviour is governed by the harness default, not the canonical profile.";

        return new SquadDegradationRecord(
            Target: "kilo",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: details);
    }
}

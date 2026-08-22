using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Antigravity workspace skills via role-skill
/// lowering (Antigravity has no native agent primitive).
/// </summary>
/// <remarks>
/// <para>
/// Contract verified against Google's Antigravity documentation on 2026-08-22
/// (antigravity.google/docs/skills): workspace skills default to <c>.agents/skills/</c>, with
/// backward support for <c>.agent/skills/</c>; empirically confirmed for AGY CLI and IDE
/// (atamel.dev, 2026-07-01).
/// </para>
/// <para>
/// Antigravity is a fallback target in <see cref="SquadRendererRegistry"/>: every agent
/// lowers to a skill, conductors reuse their shared canonical skill identity, and
/// distinct-body collisions emit both the canonical skill and a <c>role-</c>-prefixed
/// skill. Deployed <c>SKILL.md</c> files carry no capability enforcement, so non-deny
/// profile decisions are recorded as <c>permission-not-expressible</c> rather than
/// invented into frontmatter.
/// </para>
/// </remarks>
public sealed class AntigravityRenderer : ISquadRenderer
{
    private const string SkillsDirectory = ".agents/skills";

    private static readonly string[] SharedConductorIdentities = ["conductor", "conductor-v3"];

    private static readonly string[] DistinctBodyCollisions =
    [
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev",
        "test-dev"
    ];

    /// <summary>
    /// The capability vocabulary from <c>profiles/capabilities.yml</c>. Listed here so
    /// permission degradations stay ordered and complete even if a profile dictionary
    /// enumerates in a different order.
    /// </summary>
    private static readonly string[] CapabilityVocabulary =
    [
        "filesystem.read",
        "filesystem.search",
        "filesystem.write",
        "process.execute",
        "network.read",
        "network.publish",
        "delegate"
    ];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Antigravity];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Antigravity))
        {
            throw new ArgumentException(
                "AntigravityRenderer was asked to render a target other than Antigravity.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        foreach (SquadSkill skill in source.Skills)
        {
            files.Add(RenderSkill(skill.Name, skill.Description, skill.InstructionBody));
        }

        HashSet<string> skillNames = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (SquadAgent agent in source.Agents)
        {
            if (SharedConductorIdentities.Contains(agent.Name, StringComparer.Ordinal))
            {
                // Shared identity: the canonical conductor skill is already emitted; emitting
                // role-conductor would duplicate the projection the registry forbids.
                degradations.Add(new SquadDegradationRecord(
                    Target: "antigravity",
                    CanonicalIdentity: agent.Name,
                    OutputIdentity: agent.Name,
                    Code: "role-skill-fallback",
                    InstructionDigest: agent.BodyDigest,
                    Details: "Reused identical shared canonical skill; agent primitive lowered to skill."));
            }
            else if (DistinctBodyCollisions.Contains(agent.Name, StringComparer.Ordinal) &&
                     skillNames.Contains(agent.Name))
            {
                string outputIdentity = $"role-{agent.Name}";
                files.Add(RenderSkill(outputIdentity, agent.Description, agent.InstructionBody));
                degradations.Add(new SquadDegradationRecord(
                    Target: "antigravity",
                    CanonicalIdentity: agent.Name,
                    OutputIdentity: outputIdentity,
                    Code: "role-skill-fallback",
                    InstructionDigest: agent.BodyDigest,
                    Details: "Agent lowered to role-prefixed skill to preserve distinct canonical skill."));
            }
            else
            {
                files.Add(RenderSkill(agent.Name, agent.Description, agent.InstructionBody));
                degradations.Add(new SquadDegradationRecord(
                    Target: "antigravity",
                    CanonicalIdentity: agent.Name,
                    OutputIdentity: agent.Name,
                    Code: "role-skill-fallback",
                    InstructionDigest: agent.BodyDigest,
                    Details: "Agent lowered to role skill."));
            }

            SquadDegradationRecord? permission = BuildPermissionDegradation(agent, source.CapabilityProfiles.Profiles);
            if (permission is not null)
            {
                degradations.Add(permission);
            }
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static SquadDeploymentFile RenderSkill(string name, string description, string instructionBody)
    {
        // Insertion order is the frontmatter contract: name, description, license. No model
        // or permission keys — models.yml has no antigravity entry, and skills cannot express
        // the capability lattice.
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = ToSingleLineScalar(description),
            ["license"] = "MIT"
        };

        string yaml = YamlSerializer.Serialize(frontmatter);
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("---\n");

        string normalizedBody = instructionBody.Replace("\r\n", "\n");
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        return new SquadDeploymentFile(
            $"{SkillsDirectory}/{name}/SKILL.md",
            Encoding.UTF8.GetBytes(builder.ToString()),
            "antigravity");
    }

    private static SquadDegradationRecord? BuildPermissionDegradation(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            return null;
        }

        // Antigravity skills are instruction-only: any non-deny decision is unenforceable
        // at the harness boundary, so it is named rather than invented into frontmatter.
        List<string> constrained = CapabilityVocabulary
            .Where(capability =>
                profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision != SquadPermissionDecision.Deny)
            .ToList();

        if (constrained.Count == 0)
        {
            return null;
        }

        return new SquadDegradationRecord(
            Target: "antigravity",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: $"Capability profile '{agent.CapabilityProfile}' constrains " +
                $"{string.Join(", ", constrained)} but Antigravity skills cannot express " +
                "capability permissions; the deployed skill's behaviour is governed by the " +
                "harness default, not the canonical profile.");
    }

    /// <summary>
    /// YAML frontmatter scalars must stay single-line; a folded canonical description
    /// would otherwise emit a block scalar and break consumers that expect a plain string.
    /// </summary>
    private static string ToSingleLineScalar(string value) =>
        string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

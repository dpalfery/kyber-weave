using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into GitHub Copilot's native custom-agent and
/// agent-skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Contract verified against GitHub's own documentation on 2026-08-16: agents are
/// <c>.github/agents/&lt;name&gt;.agent.md</c> (the double extension is load-bearing —
/// <c>&lt;name&gt;.md</c> is not recognized), frontmatter requires only <c>description</c>,
/// and the body is capped at 30,000 characters. Skills are
/// <c>.github/skills/&lt;name&gt;/SKILL.md</c> requiring <c>name</c> and <c>description</c>.
/// </para>
/// <para>
/// Copilot's <c>tools</c> frontmatter key is a flat allow-list of platform-specific tool
/// names ("Tool names vary across GitHub Copilot platforms" — the docs' own words) with no
/// published mapping to Kyber-Squad's semantic capability vocabulary
/// (<c>filesystem.write</c>, <c>network.publish</c>, ...). Guessing a mapping would either
/// under-grant (breaking the agent) or over-grant (a silent permission widening — exactly
/// what <see cref="SquadRendererRegistry"/> is built to catch). So <c>tools</c> is left
/// unset — Copilot's documented default is "all available tools" — and every capability
/// profile that denies or asks for anything is instead recorded as a
/// <see cref="SquadDegradationRecord"/> with code <c>permission-not-expressible</c>. That
/// keeps the gap visible in the deployment receipt rather than silently granting it.
/// </para>
/// </remarks>
public sealed class CopilotRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".github/agents";
    private const string SkillsDirectory = ".github/skills";
    private const int MaxAgentBodyCharacters = 30_000;

    private static readonly string[] SharedConductorIdentities = ["conductor", "conductor-v3"];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Copilot];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Copilot))
        {
            throw new ArgumentException(
                "CopilotRenderer was asked to render a target other than Copilot.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];
        List<SquadRenderWarning> warnings = [];

        foreach (SquadAgent agent in source.Agents)
        {
            files.Add(RenderAgent(agent, source.ModelProfiles.Profiles, warnings));

            SquadDegradationRecord? degradation = BuildPermissionDegradation(agent, source.CapabilityProfiles.Profiles);
            if (degradation is not null)
            {
                degradations.Add(degradation);
            }
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // Conductor and conductor-v3 are native primary agents on Copilot; suppressing
            // their skill projection here is the single-projection rule
            // SquadRendererRegistry enforces on every native target.
            if (SharedConductorIdentities.Contains(skill.Name, StringComparer.Ordinal))
            {
                continue;
            }

            files.Add(RenderSkill(skill));
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, warnings, []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        List<SquadRenderWarning> warnings)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description
        };

        string? model = ResolveCopilotModel(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        if (agent.Invocation == SquadInvocation.Subagent)
        {
            // Subagents are dispatched by the conductor, not chosen directly by a human —
            // "user-invocable: false" is Copilot's closest equivalent. Primary agents
            // (conductor, conductor-v3) leave this at its default (true, omitted).
            frontmatter["user-invocable"] = false;
        }

        // metadata values are kept as flat strings rather than nested arrays: GitHub's
        // docs describe metadata only as "key-value pairs for annotation" without
        // specifying accepted value types, and a flat string is the safe reading until
        // that is verified against a live agent.
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["capability-profile"] = agent.CapabilityProfile,
            ["fallback"] = agent.Fallback
        };
        if (agent.DelegatesTo.Count > 0)
        {
            metadata["delegates-to"] = string.Join(", ", agent.DelegatesTo);
        }

        if (agent.Aliases.Count > 0)
        {
            metadata["aliases"] = string.Join(", ", agent.Aliases);
        }

        frontmatter["metadata"] = metadata;

        string yaml = YamlSerializer.Serialize(frontmatter);
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("---\n");

        string normalizedBody = agent.InstructionBody.Replace("\r\n", "\n");
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        string content = builder.ToString();
        if (content.Length > MaxAgentBodyCharacters)
        {
            warnings.Add(new SquadRenderWarning(
                "agent-body-too-long",
                $"Agent '{agent.Name}' is {content.Length} characters; Copilot caps custom agent files at {MaxAgentBodyCharacters}.",
                "copilot"));
        }

        return new SquadDeploymentFile(
            $"{AgentsDirectory}/{agent.Name}.agent.md",
            Encoding.UTF8.GetBytes(content),
            "copilot");
    }

    private static SquadDeploymentFile RenderSkill(SquadSkill skill)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = skill.Name,
            ["description"] = skill.Description,
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

        string normalizedBody = skill.InstructionBody.Replace("\r\n", "\n");
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        return new SquadDeploymentFile(
            $"{SkillsDirectory}/{skill.Name}/SKILL.md",
            Encoding.UTF8.GetBytes(builder.ToString()),
            "copilot");
    }

    private static string? ResolveCopilotModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("copilot", out string? copilotModel))
        {
            return copilotModel;
        }

        // "inherit" means defer to whatever the user has selected in Copilot's own model
        // picker — Copilot's documented default when `model` is omitted entirely.
        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal)
            ? null
            : profile.Default;
    }

    private static SquadDegradationRecord? BuildPermissionDegradation(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            return null;
        }

        List<string> restricted = profile.Permissions
            .Where(pair => pair.Value != SquadPermissionDecision.Allow)
            .Select(pair => $"{pair.Key}: {pair.Value.ToString().ToLowerInvariant()}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        if (restricted.Count == 0)
        {
            return null;
        }

        return new SquadDegradationRecord(
            Target: "copilot",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: $"Capability profile '{agent.CapabilityProfile}' restricts " +
                $"{string.Join(", ", restricted)}, which Copilot's flat tool allow-list " +
                "cannot enforce. The agent file omits 'tools' (Copilot's all-tools default) " +
                "rather than claim a mapping that does not exist.");
    }
}

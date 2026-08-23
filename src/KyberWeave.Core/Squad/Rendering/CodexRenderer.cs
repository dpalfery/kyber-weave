using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Codex's native agent TOML and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Native agent target: canonical agents render as Codex's native agent TOML primitive at
/// <c>.codex/agents/&lt;name&gt;.toml</c>. Codex agent TOML files contain the required
/// top-level fields <c>name</c>, <c>description</c>, and multi-line
/// <c>developer_instructions</c>, plus optional <c>model</c>.
/// </para>
/// <para>
/// Canonical skills render as harness skills at <c>.codex/skills/&lt;name&gt;/SKILL.md</c>
/// with YAML frontmatter containing <c>name</c>, <c>description</c>, and <c>license: MIT</c>.
/// Per the native single-projection rule, primary agents (<c>conductor</c> and <c>conductor-v3</c>)
/// suppress their skill projections.
/// </para>
/// <para>
/// Model resolution resolves the agent model from <c>models.yml</c> for target <c>codex</c>,
/// falling back to the profile's default when not <c>inherit</c>.
/// </para>
/// <para>
/// Permission degradation: Codex agent configuration has no frontmatter tool allow-list or
/// capability permission lattice. Non-deny profile decisions are recorded as structured
/// degradations with code <c>permission-not-expressible</c> rather than inventing unenforceable fields.
/// </para>
/// </remarks>
public sealed class CodexRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".codex/agents";
    private const string SkillsDirectory = ".codex/skills";

    private static readonly string[] SharedConductorIdentities = ["conductor", "conductor-v3"];

    private static readonly string[] GovernedCapabilities =
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
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Codex];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Codex))
        {
            throw new ArgumentException(
                "CodexRenderer was asked to render a target other than Codex.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        foreach (SquadAgent agent in source.Agents)
        {
            files.Add(RenderAgent(
                agent,
                source.ModelProfiles.Profiles));

            SquadDegradationRecord? degradation = BuildDegradationRecord(agent, source.CapabilityProfiles.Profiles);
            if (degradation is not null)
            {
                degradations.Add(degradation);
            }
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // Primary agents (conductor and conductor-v3) are native primary agents on Codex;
            // suppressing their skill projection adheres to the single-projection rule
            // enforced by SquadRendererRegistry.
            if (SharedConductorIdentities.Contains(skill.Name, StringComparer.Ordinal))
            {
                continue;
            }

            files.Add(RenderSkill(skill));
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        StringBuilder builder = new();
        builder.Append("name = \"");
        builder.Append(EscapeTomlString(agent.Name));
        builder.Append("\"\ndescription = \"");
        builder.Append(EscapeTomlString(agent.Description));
        builder.Append("\"\n");

        string? model = ResolveCodexModel(agent, modelProfiles);
        if (model is not null)
        {
            builder.Append("model = \"");
            builder.Append(EscapeTomlString(model));
            builder.Append("\"\n");
        }

        string normalizedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalizedBody.EndsWith('\n'))
        {
            normalizedBody += "\n";
        }

        builder.Append("developer_instructions = \"\"\"\n");
        builder.Append(EscapeTomlMultiline(normalizedBody));
        builder.Append("\"\"\"\n");

        string content = builder.ToString();
        return new SquadDeploymentFile(
            $"{AgentsDirectory}/{agent.Name}.toml",
            Encoding.UTF8.GetBytes(content),
            "codex");
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

        string content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, skill.InstructionBody);

        return new SquadDeploymentFile(
            $"{SkillsDirectory}/{skill.Name}/SKILL.md",
            Encoding.UTF8.GetBytes(content),
            "codex");
    }

    private static string? ResolveCodexModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("codex", out string? codexModel))
        {
            return codexModel;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal)
            ? null
            : profile.Default;
    }

    private static SquadDegradationRecord? BuildDegradationRecord(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            return null;
        }

        List<string> unexpressed = GovernedCapabilities
            .Where(cap => profile.Permissions.TryGetValue(cap, out SquadPermissionDecision decision) &&
                          decision != SquadPermissionDecision.Deny)
            .ToList();

        if (unexpressed.Count == 0)
        {
            return null;
        }

        string details =
            $"Capability profile '{agent.CapabilityProfile}' constrains {string.Join(", ", unexpressed)} but Codex agents cannot express capability permissions; the deployed agent's behaviour is governed by the harness default, not the canonical profile.";

        return new SquadDegradationRecord(
            Target: "codex",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: details);
    }

    private static string EscapeTomlString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal)
             .Replace("\r", "\\r", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal)
             .Replace("\t", "\\t", StringComparison.Ordinal);

    /// <remarks>
    /// Backslashes must be escaped before triple quotes: otherwise a body containing
    /// <c>\"""</c> (or any backslash) produces invalid TOML when quotes are escaped first.
    /// </remarks>
    private static string EscapeTomlMultiline(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
}

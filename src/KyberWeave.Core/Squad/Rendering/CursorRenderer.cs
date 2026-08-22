using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Cursor's native subagent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Subagent and skill contract verified against Cursor documentation (cursor.com/docs/subagents
/// and cursor.com/docs/skills) on 2026-08-22: subagents are stored at <c>.cursor/agents/&lt;name&gt;.md</c>
/// containing Markdown with YAML frontmatter. Frontmatter supports <c>name</c>, <c>description</c>,
/// optional <c>model</c>, and optional <c>readonly</c>. No <c>tools</c> allow-list key exists in Cursor's
/// subagent frontmatter.
/// </para>
/// <para>
/// Permission lowering maps onto Cursor's boolean <c>readonly</c> field: when an agent's capability
/// profile allows <c>filesystem.write</c> or <c>process.execute</c>, the <c>readonly</c> key is omitted
/// (deferring to Cursor's default of <c>false</c>). When both are withheld (<c>ask</c> or <c>deny</c>),
/// <c>readonly: true</c> is emitted to restrict file modifications and state-changing terminal executions.
/// Capabilities that cannot be expressed in Cursor's frontmatter are recorded as structured degradations
/// (<c>permission-not-expressible</c>).
/// </para>
/// <para>
/// Skills are rendered to <c>.cursor/skills/&lt;name&gt;/SKILL.md</c> with frontmatter containing
/// <c>name</c>, <c>description</c>, and <c>license: MIT</c>. Per the native single-projection rule,
/// primary agents (<c>conductor</c> and <c>conductor-v3</c>) suppress their skill projections.
/// </para>
/// </remarks>
public sealed class CursorRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".cursor/agents";
    private const string SkillsDirectory = ".cursor/skills";

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

    /// <summary>
    /// The only pair of capabilities Cursor's <c>readonly</c> boolean can enforce; kept as a
    /// field per CA1861 because the degradation builder runs per agent.
    /// </summary>
    private static readonly string[] ReadOnlyEnforcedCapabilities = ["filesystem.write", "process.execute"];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Cursor];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Cursor))
        {
            throw new ArgumentException(
                "CursorRenderer was asked to render a target other than Cursor.",
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
                source.ModelProfiles.Profiles,
                source.CapabilityProfiles.Profiles));

            SquadDegradationRecord? degradation = BuildDegradationRecord(agent, source.CapabilityProfiles.Profiles);
            if (degradation is not null)
            {
                degradations.Add(degradation);
            }
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // Primary agents (conductor and conductor-v3) are native agents on Cursor;
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
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description
        };

        string? model = ResolveCursorModel(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        if (IsReadOnly(agent, capabilityProfiles))
        {
            frontmatter["readonly"] = true;
        }

        string content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);

        return new SquadDeploymentFile(
            $"{AgentsDirectory}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            "cursor");
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
            "cursor");
    }

    private static string? ResolveCursorModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("cursor", out string? cursorModel))
        {
            return cursorModel;
        }

        // Defer to Cursor's model default (omit model key) when fallback profile default is 'inherit'.
        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal)
            ? null
            : profile.Default;
    }

    private static bool IsReadOnly(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            return false;
        }

        bool allowsWrite = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
                           writeDecision == SquadPermissionDecision.Allow;

        bool allowsExecute = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision executeDecision) &&
                             executeDecision == SquadPermissionDecision.Allow;

        // If either write or execute is allowed, readonly is omitted (default false).
        // If both are withheld (ask or deny), readonly is set to true.
        return !allowsWrite && !allowsExecute;
    }

    private static SquadDegradationRecord? BuildDegradationRecord(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            return null;
        }

        List<string> nonDenyCapabilities = GovernedCapabilities
            .Where(cap => profile.Permissions.TryGetValue(cap, out SquadPermissionDecision decision) &&
                          decision != SquadPermissionDecision.Deny)
            .ToList();

        // An all-deny profile is completely consistent with readonly: true and needs no degradation record.
        if (nonDenyCapabilities.Count == 0)
        {
            return null;
        }

        bool isReadOnly = IsReadOnly(agent, capabilityProfiles);

        // For readonly: true agents, filesystem.write and process.execute are enforced by the boolean
        // and excluded from unexpressed capability accounting.
        List<string> unexpressed = isReadOnly
            ? nonDenyCapabilities.Where(cap => cap is not ("filesystem.write" or "process.execute")).ToList()
            : nonDenyCapabilities;

        // Without the readonly boolean, Cursor grants file edits and terminal execution. A
        // canonical deny for either capability is therefore not enforced by the rendered
        // file, so it is named in the record rather than dropped silently.
        List<string> unenforcedDenials = isReadOnly
            ? []
            : ReadOnlyEnforcedCapabilities
                .Where(cap => profile.Permissions.TryGetValue(cap, out SquadPermissionDecision decision) &&
                              decision == SquadPermissionDecision.Deny)
                .ToList();

        StringBuilder details = new();
        if (unexpressed.Count > 0)
        {
            details.Append($"Cursor subagent configuration cannot express fine-grained permissions for: {string.Join(", ", unexpressed)}.");
        }
        else
        {
            details.Append("Cursor subagent configuration enforces readonly mode, but cannot express other fine-grained permissions.");
        }

        if (unenforcedDenials.Count > 0)
        {
            details.Append($" Canonical denies not enforced without readonly: {string.Join(", ", unenforcedDenials)}.");
        }

        if (agent.DelegatesTo.Count > 0)
        {
            details.Append(" Permitted delegation roster cannot be pinned in subagent frontmatter (Cursor resolves subagents at Task-call time).");
        }

        return new SquadDegradationRecord(
            Target: "cursor",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: details.ToString());
    }
}

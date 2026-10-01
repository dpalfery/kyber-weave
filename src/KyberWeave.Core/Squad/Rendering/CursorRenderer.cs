using System.Globalization;
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
/// profile-declared shared identities suppress their skill projections.
/// </para>
/// <para>
/// <b>Primary-agent lowering.</b> Cursor has no primary-agent selection primitive, so a
/// canonical agent with <see cref="SquadInvocation.Primary"/> cannot render at
/// <c>.cursor/agents/&lt;name&gt;.md</c> the way subagent-invocation agents do. Its fallback
/// profile decides the outcome: <c>no-primary-agent: skill</c> renders it as a top-level
/// <c>.cursor/skills/&lt;name&gt;/SKILL.md</c> entry-point skill (frontmatter exactly
/// <c>name</c>, single-line <c>description</c>, <c>license: MIT</c> — no
/// <c>disable-model-invocation</c> key, so auto-load stays allowed) plus its resource
/// closure projected beside it, while the <c>agents/&lt;name&gt;.md</c> subagent principal
/// is not emitted; <c>no-primary-agent: omit</c> emits nothing and records
/// <c>omitted</c>. Because a top-level skill enforces none of the capability lattice, the
/// lowered skill records <c>permission-not-expressible</c> for the declared vocabulary via
/// <see cref="CapabilityDegradations"/>. Cursor has separate agent and skill namespaces, so
/// if the lowered identity is already occupied by a canonical skill, rendering fails closed
/// with <see cref="SquadRenderValidationException"/> naming it.
/// </para>
/// </remarks>
public sealed class CursorRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".cursor/agents";
    private const string SkillsDirectory = ".cursor/skills";

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

    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        if (scope == SquadDeploymentScope.Project)
        {
            return baseDirectory;
        }

        return baseDirectory.StartsWith(".cursor/", StringComparison.Ordinal)
            ? baseDirectory[".cursor/".Length..]
            : baseDirectory;
    }

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
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

        // Read upfront so the primary-agent fail-closed check can see whether a
        // canonical skill already occupies the lowered identity before any file is built,
        // regardless of which loop encounters the collision first.
        HashSet<string> skillIdentities = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in a lowered primary agent's
        // permission-not-expressible details without a renderer change.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        foreach (SquadAgent agent in source.Agents)
        {
            if (agent.Invocation == SquadInvocation.Subagent)
            {
                SquadDeploymentFile principal = RenderAgent(
                    agent,
                    source.ModelProfiles.Profiles,
                    source.CapabilityProfiles.Profiles,
                    request.Scope);
                files.Add(principal);
                SquadResourceProjection.Append(files, principal, agent.Resources);

                SquadDegradationRecord? degradation = BuildDegradationRecord(agent, source.CapabilityProfiles.Profiles);
                if (degradation is not null)
                {
                    degradations.Add(degradation);
                }

                continue;
            }

            SquadFallbackProfile fallbackProfile = source.FallbackProfiles.Profiles[agent.Fallback];
            if (string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal))
            {
                if (skillIdentities.Contains(agent.Name))
                {
                    throw new SquadRenderValidationException(
                        $"Cannot lower primary agent '{agent.Name}' to a Cursor skill: a canonical " +
                        $"skill named '{agent.Name}' already occupies that identity. Cursor has " +
                        "separate agent and skill namespaces with no role-prefixed fallback " +
                        "mechanism, so this is a fail-closed condition rather than a naming " +
                        "collision this renderer can resolve on its own.");
                }

                SquadDeploymentFile principal =
                    RenderSkill(agent.Name, agent.Description, agent.InstructionBody, request.Scope);
                files.Add(principal);
                SquadResourceProjection.Append(files, principal, agent.Resources);

                degradations.Add(BuildPrimaryAgentDegradation(
                    agent,
                    fallbackProfile,
                    source.CapabilityProfiles.Profiles,
                    capabilityVocabulary));
            }
            else if (string.Equals(fallbackProfile.NoPrimaryAgent, "omit", StringComparison.Ordinal))
            {
                degradations.Add(new SquadDegradationRecord(
                    "cursor",
                    agent.Name,
                    agent.Name,
                    "omitted",
                    agent.BodyDigest,
                    $"Fallback profile '{agent.Fallback}' declares " +
                    "no-primary-agent: omit; Cursor has no primary-agent primitive for this " +
                    "agent to render onto, so no file is emitted."));
            }
            else
            {
                throw new SquadRenderValidationException(
                    $"Fallback profile '{agent.Fallback}' declares unsupported " +
                    $"no-primary-agent value '{fallbackProfile.NoPrimaryAgent}' for primary " +
                    $"agent '{agent.Name}'.");
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

            SquadDeploymentFile principal =
                RenderSkill(skill.Name, skill.Description, skill.InstructionBody, request.Scope);
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

        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, scope);
        return new SquadDeploymentFile(
            $"{agentsDir}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            "cursor");
    }

    private static SquadDeploymentFile RenderSkill(string name, string description, string instructionBody,
        SquadDeploymentScope scope)
    {
        string singleLineDescription = string.Join(" ", description.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = singleLineDescription,
            ["license"] = "MIT"
        };

        string content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, instructionBody);

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDir}/{name}/SKILL.md",
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

        // If either enforced capability is allowed, readonly is omitted (default false).
        // If both are withheld (ask or deny), readonly is set to true.
        return !ReadOnlyEnforcedCapabilities.Any(capability =>
            profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
            decision == SquadPermissionDecision.Allow);
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
            ? nonDenyCapabilities
                .Where(cap => !ReadOnlyEnforcedCapabilities.Contains(cap, StringComparer.Ordinal))
                .ToList()
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
            details.Append(CultureInfo.InvariantCulture, $"Cursor subagent configuration cannot express fine-grained permissions for: {string.Join(", ", unexpressed)}.");
        }
        else
        {
            details.Append("Cursor subagent configuration enforces readonly mode, but cannot express other fine-grained permissions.");
        }

        if (unenforcedDenials.Count > 0)
        {
            details.Append(CultureInfo.InvariantCulture, $" Canonical denies not enforced without readonly: {string.Join(", ", unenforcedDenials)}.");
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

    private static SquadDegradationRecord BuildPrimaryAgentDegradation(
        SquadAgent agent,
        SquadFallbackProfile fallbackProfile,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary)
    {
        string rosterText = agent.DelegatesTo.Count > 0
            ? string.Join(", ", agent.DelegatesTo)
            : "(none declared)";

        return new SquadDegradationRecord(
            Target: "cursor",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: "Cursor has no primary-agent selection primitive, so fallback profile " +
            $"'{agent.Fallback}' declares no-primary-agent: {fallbackProfile.NoPrimaryAgent} " +
            "and this agent renders as a top-level Cursor skill instead of a " +
            "'.cursor/agents/' file. Capability decisions " +
            $"({CapabilityDegradations.DescribeCapabilityDecisions(agent, capabilityProfiles, capabilityVocabulary)}) " +
            "are not enforced: a top-level Cursor skill runs under the harness default tool " +
            "set, not the canonical capability lattice, and its delegates-to roster " +
            $"({rosterText}) is instruction-only, not a runtime-enforced allow-list.");
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

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
/// <para>
/// <b>Arbiter block (Req 6.2, 8.2, 22.2).</b> When the render request carries an
/// enabled <see cref="SquadArbiterWiring"/> under
/// <see cref="SquadDeploymentScope.Project"/>, the renderer returns a <c>cursor</c>
/// <see cref="SquadRenderedBlock"/> for <c>.cursor/hooks.json</c> holding
/// <c>preToolUse</c> and <c>postToolUse</c> entries matching <c>Task</c> with
/// <c>failClosed: true</c> and the wiring timeout ([F6]). The command carries no
/// <c>--caller</c>: a shared hook file gates project-wide, not per agent. A null or
/// disabled wiring, or Global scope (no project configuration to enforce from,
/// Req 22.4), renders no block and leaves the owned files byte-identical.
/// </para>
/// </remarks>
public sealed class CursorRenderer : SquadHarnessRenderer
{
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

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.Cursor,
        "Cursor",
        ".cursor",
        "agents",
        SquadModelPolicy.HarnessValueVerbatim);

    public CursorRenderer()
        : base(HarnessSpec)
    {
    }

    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        if (agent.Invocation == SquadInvocation.Subagent)
        {
            context.AddPrincipal(RenderAgentFile(context, agent), agent.Resources);
            context.AddDegradation(BuildDegradationRecord(agent, context.CapabilityProfiles));
            return;
        }

        context.AddLoweredPrimaryAgent(
            agent,
            () =>
                $"Cannot lower primary agent '{agent.Name}' to a Cursor skill: a canonical " +
                $"skill named '{agent.Name}' already occupies that identity. Cursor has " +
                "separate agent and skill namespaces with no role-prefixed fallback " +
                "mechanism, so this is a fail-closed condition rather than a naming " +
                "collision this renderer can resolve on its own.",
            () => context.RenderSkillDocument(agent.Name, agent.Description, agent.InstructionBody),
            (fallbackProfile, reused) =>
            [
                BuildPrimaryAgentDegradation(
                    agent,
                    fallbackProfile,
                    context.CapabilityProfiles,
                    context.CapabilityVocabulary,
                    reused)
            ]);
    }

    /// <summary>Squad's owned block for the shared <c>.cursor/hooks.json</c>.</summary>
    private protected override void RenderArbiter(SquadHarnessRenderContext context)
    {
        if (context.EnabledArbiter is { } arbiter)
        {
            context.Blocks.Add(BuildArbiterBlock(arbiter.HookTimeoutSeconds, context.Scope));
        }
    }

    private static SquadDeploymentFile RenderAgentFile(SquadHarnessRenderContext context, SquadAgent agent)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description
        };

        string? model = context.ResolveModel(agent);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        if (IsReadOnly(agent, context.CapabilityProfiles))
        {
            frontmatter["readonly"] = true;
        }

        return context.CreateFile(
            $"{context.AgentsDirectory}/{agent.Name}.md",
            SquadYamlEmitter.Default.Compose(frontmatter, agent.InstructionBody));
    }

    private static SquadRenderedBlock BuildArbiterBlock(int timeoutSeconds, SquadDeploymentScope scope) =>
        new(
            "cursor",
            scope == SquadDeploymentScope.Global ? "hooks.json" : SquadHookJsonBlock.RelativePath(SquadHookBlockFormat.Cursor),
            SquadHookBlockFormat.Cursor,
            [
                new SquadRenderedBlockEntry("preToolUse", ArbiterHookEntry(timeoutSeconds, scope)),
                new SquadRenderedBlockEntry("postToolUse", ArbiterHookEntry(timeoutSeconds, scope)),
            ]);

    private static JsonObject ArbiterHookEntry(int timeoutSeconds, SquadDeploymentScope scope) => new()
    {
        ["command"] = ArbiterHookWiring.HookCommandLine(SquadTarget.Cursor, scope),
        ["matcher"] = "Task",
        ["timeout"] = timeoutSeconds,
        ["failClosed"] = true,
    };

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
            details.Append(CultureInfo.InvariantCulture,
                $"Cursor subagent configuration cannot express fine-grained permissions for: {string.Join(", ", unexpressed)}.");
        }
        else
        {
            details.Append(
                "Cursor subagent configuration enforces readonly mode, but cannot express other fine-grained permissions.");
        }

        if (unenforcedDenials.Count > 0)
        {
            details.Append(CultureInfo.InvariantCulture,
                $" Canonical denies not enforced without readonly: {string.Join(", ", unenforcedDenials)}.");
        }

        if (agent.DelegatesTo.Count > 0)
        {
            details.Append(
                " Permitted delegation roster cannot be pinned in subagent frontmatter (Cursor resolves subagents at Task-call time).");
        }

        return new SquadDegradationRecord(
            "cursor",
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            details.ToString());
    }

    private static SquadDegradationRecord BuildPrimaryAgentDegradation(
        SquadAgent agent,
        SquadFallbackProfile fallbackProfile,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary,
        bool reusedCanonicalSkill = false)
    {
        string rosterText = agent.DelegatesTo.Count > 0
            ? string.Join(", ", agent.DelegatesTo)
            : "(none declared)";

        string projectionText = reusedCanonicalSkill
            ? "this agent reuses the same-name canonical skill as its entry point, because the " +
              "canonical skill already occupies that identity; no separate agent file is emitted"
            : "this agent renders as a top-level Cursor skill instead of a '.cursor/agents/' file";

        return new SquadDegradationRecord(
            "cursor",
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            "Cursor has no primary-agent selection primitive, so fallback profile " +
            $"'{agent.Fallback}' declares no-primary-agent: {fallbackProfile.NoPrimaryAgent} and " +
            projectionText + ". Capability decisions " +
            $"({CapabilityDegradations.DescribeCapabilityDecisions(agent, capabilityProfiles, capabilityVocabulary)}) " +
            "are not enforced: a top-level Cursor skill runs under the harness default tool " +
            "set, not the canonical capability lattice, and its delegates-to roster " +
            $"({rosterText}) is instruction-only, not a runtime-enforced allow-list.");
    }
}

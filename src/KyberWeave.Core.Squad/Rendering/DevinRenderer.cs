using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into the custom-subagent and skill formats read by Devin
/// Desktop's local agent, Devin Local, which shares the Devin CLI's discovery and file formats.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source of truth.</b> Devin Desktop is Cognition's desktop app, shipped on 2026-06-02 as
/// the successor to Windsurf. Its local agent, Devin Local, is the Devin CLI's agent harness,
/// so it uses the CLI's skill and subagent formats and discovery. The facts below were read on
/// 2026-09-27 from Devin's published documentation (<c>docs.devin.ai/desktop/devin-local</c>,
/// <c>docs.devin.ai/cli/subagents</c>, <c>docs.devin.ai/cli/extensibility/skills</c> and its
/// <c>creating-skills</c> reference, <c>docs.devin.ai/cli/reference/permissions</c>,
/// <c>docs.devin.ai/cli/reference/configuration/read-config-from</c>, and the tool-name table in
/// <c>docs.devin.ai/cli/extensibility/hooks/lifecycle-hooks</c>) and from the Devin CLI
/// changelog. The oldest build this output is correct for is v3000.11.1 (2026-09-21), the
/// first release whose <c>allowed-tools</c> and permission rules recognize <c>write</c>.
/// Anything below that says "not documented" is a reading this renderer takes the
/// non-broadening side of, and each is recorded as a degradation rather than asserted as a
/// mapping.
/// </para>
/// <para>
/// <b>Discovery roots.</b> Custom subagents load from <c>.devin/agents/</c> and
/// <c>.agents/agents/</c> in the workspace, and from <c>agents/</c> in the Devin user
/// configuration directory (<c>~/.config/devin</c>, or <c>%APPDATA%\devin</c> on Windows — see
/// <see cref="SquadGlobalRoots"/>). Skills load natively from <c>.devin/skills/</c> and
/// <c>.agents/skills/</c>, and from <c>skills/</c> in the user directory. This renderer writes
/// only the <c>.devin/</c> roots. Devin additionally imports other tools' trees by default
/// through <c>read_config_from</c> — <c>.windsurf/skills/</c>, <c>.claude/skills/</c> and
/// <c>.claude/commands/</c>, and <c>.github/skills/</c> — and <c>.agents/</c> is where
/// <see cref="AntigravityRenderer"/> writes. Squad's own Claude, Copilot, and Antigravity
/// output is therefore visible to Devin too, so a repository carrying both would load each
/// Squad identity twice; <see cref="DevinImportOverlap"/> reports that from
/// <c>squad doctor</c> rather than this renderer writing Devin's configuration to prevent it.
/// </para>
/// <para>
/// <b>Directory layout for agents.</b> Devin reads a subagent from either
/// <c>agents/&lt;name&gt;.md</c> or <c>agents/&lt;name&gt;/AGENT.md</c>. This renderer emits the
/// directory form only. The flat form would put an agent's resource closure at
/// <c>agents/&lt;name&gt;/…</c> beside <c>agents/&lt;name&gt;.md</c> — the very directory
/// Devin's other layout claims for that name, leaving one identity in both layouts at once. Inside
/// <c>agents/&lt;name&gt;/</c> the closure sits one level deeper, beneath its authored
/// <c>&lt;name&gt;/</c> prefix, so authored links resolve verbatim (as they do for
/// <see cref="AntigravityRenderer"/>) and no resource can land where Devin looks for
/// <c>AGENT.md</c>, <c>AGENTS.md</c>, <c>agent.md</c>, or <c>agents.md</c>.
/// </para>
/// <para>
/// <b>Agent frontmatter.</b> A subagent profile is YAML frontmatter followed by the system
/// prompt. Its documented keys are <c>name</c>, <c>description</c>, <c>model</c>,
/// <c>allowed-tools</c> (alias <c>tools</c>), and <c>max-nesting</c>. This renderer emits
/// <c>name</c>, <c>description</c>, <c>model</c> (only when a <c>devin:</c> harness value
/// resolves to something other than <c>inherit</c>), and <c>allowed-tools</c>, nothing else.
/// Omitting <c>model</c> does not inherit the parent's model on Devin: a custom subagent with
/// no <c>model</c> runs on the default subagent model, which a server-side router picks at
/// spawn time unless an administrator pins one. On a subagent profile <c>allowed-tools</c> is a
/// hard restriction, so it is always emitted; what an empty list means is not documented, so a
/// resolved grant of nothing is a render error rather than an empty list that might mean
/// "every tool".
/// </para>
/// <para>
/// <b>Capability lowering.</b> Devin's core tool names are <c>read</c>,
/// <c>notebook_read</c>, <c>grep</c>, <c>glob</c>, <c>edit</c>, <c>write</c>,
/// <c>apply_patch</c>, <c>notebook_edit</c>, <c>exec</c> with its companions
/// <c>get_output</c>, <c>write_to_process</c>, and <c>kill_shell</c>, <c>webfetch</c>,
/// <c>web_search</c>, <c>todo_write</c>, and <c>skill</c>. Every tool that performs a
/// capability is granted with it, because the tool a model reaches for varies by model: a GPT
/// model edits through <c>apply_patch</c> when <c>agent.codex_tools</c> is on, and a
/// write-capable agent granted only <c>edit</c> and <c>write</c> would then be unable to edit at
/// all. <c>todo_write</c> and <c>skill</c> are granted on every subagent, matching
/// <see cref="ClaudeRenderer"/>'s ungoverned base: the first writes no file and executes
/// nothing, and the second opens only the skill tree this renderer deploys — withholding it
/// would deploy skills no subagent could reach. Only <see cref="SquadPermissionDecision.Allow"/>
/// grants anything further. <c>network.publish</c> has no tool, so an <c>allow</c> for it
/// records <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// <b><c>ask</c> narrows to withheld.</b> Devin prompts for a foreground subagent's tool call
/// only when the session's permission mode does not already approve it — Accept Edits, Smart,
/// Bypass, and Autonomous each auto-approve some of these tools, and a grant made once in a
/// session carries into every later subagent. A background subagent never prompts: it
/// auto-denies anything not already approved. A subagent profile has no <c>permissions</c> key
/// with which to force the prompt, so the approval <c>ask</c> requires cannot be guaranteed,
/// and the tool is withheld and recorded as <c>safety-narrowed</c>, as on Claude, Pi, and ZCode.
/// </para>
/// <para>
/// <b>MCP is granted by concrete tool name, on two entitlements.</b> Devin names an MCP tool
/// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> in its permission rules, so the tools declared in
/// <c>toolchain.yml</c>'s <c>required-mcp-tools</c> are appended to <c>allowed-tools</c> in that
/// form. <c>kyber-weave</c> is granted on the file-read grant alone, the pure orchestrator
/// included, because it carries <c>agent_coding_standards</c> (ADR 0050); <c>codegraph</c> and
/// <c>context7</c> keep the carve-out a role that only routes work has no use for. The
/// orchestrator never exercises the widened half of that rule on Devin: <c>conductor</c> is a
/// primary agent, Devin lowers a primary agent to a <em>skill</em>, and a Devin skill carries no
/// <c>allowed-tools</c> — this renderer emits that key only on the subagent path. So there is no
/// surface on which the orchestrator could be granted anything, and none is fabricated: emitting
/// an <c>allowed-tools</c> list on a skill would auto-approve tools, which is the opposite of the
/// restriction the key means elsewhere. Devin's generic MCP tools
/// (<c>mcp_list_tools</c>, <c>mcp_call_tool</c>, and the rest) are never granted: they reach
/// every configured server's tools, not the declared ones. The servers themselves are the
/// operator's to configure in Devin's <c>mcp_config.json</c>; this renderer never writes it.
/// </para>
/// <para>
/// <b>Delegation is withheld from subagents.</b> The parent agent dispatches a named profile
/// through <c>run_subagent</c>, and a profile reaches further subagents only when its
/// <c>max-nesting</c> allows it. Devin has no runtime-enforced delegation roster — no
/// <c>allowed_subagents</c> equivalent — so granting nested delegation would reach every
/// profile, not the canonical <c>delegates-to</c> list. This renderer therefore emits neither
/// <c>run_subagent</c> nor <c>max-nesting</c>, and an allowed <c>delegate</c> records
/// <c>permission-not-expressible</c> naming the roster that becomes unreachable. The canonical
/// bodies of the delegating roles say what to do when no agent-invocation tool is available.
/// The lowered conductor is unaffected: it runs in the main Devin Local session, where subagent
/// dispatch is always available.
/// </para>
/// <para>
/// <b>Primary-agent lowering.</b> Devin has no primary-agent primitive: Devin Local is the
/// only top-level agent, and a custom profile is always dispatched as a subagent. A
/// <see cref="SquadInvocation.Primary"/> agent whose fallback profile declares
/// <c>no-primary-agent: skill</c> therefore renders as <c>.devin/skills/&lt;name&gt;/SKILL.md</c>,
/// recording <c>role-skill-fallback</c> and <c>permission-not-expressible</c>; <c>omit</c> emits
/// nothing and records <c>omitted</c>. As on Pi, agents and skills are separate namespaces, so a
/// canonical skill already occupying the lowered identity fails the render rather than taking
/// a <c>role-</c> prefix. The lowered skill carries <c>triggers: [user]</c>, so Devin runs it
/// only when the operator asks for it. That follows <see cref="ZCodeRenderer"/>'s reasoning for
/// a slash command — an orchestrator that seizes a turn by description match is worse than one
/// started on purpose — and it matters more on Devin, because Devin Cloud discovers the same
/// <c>.devin/skills/</c> tree but loads no custom subagents ("CLI/Desktop-only today"), so an
/// auto-invoked conductor there would route work to agents that do not exist.
/// </para>
/// <para>
/// <b>Skills carry no tool keys.</b> On a skill,
/// <c>allowed-tools</c> auto-approves the listed tools rather than restricting them, and
/// <c>permissions.allow</c> does the same, so neither is ever emitted. <c>permissions.deny</c>
/// is the documented way to block a tool while an inline skill runs, and would narrow a lowered
/// primary agent — but Devin documents neither how long an inline skill counts as running nor
/// whether its deny rules reach the subagents it dispatches meanwhile. The conductor's profile
/// denies <c>edit</c>, <c>write</c>, and <c>exec</c>, the very tools every implementer it
/// dispatches needs, so emitting the denial risks disabling the whole roster rather than the
/// conductor. It is withheld until a real install shows the rules stay in the conductor's own
/// turn, and the lowered agent's capability decisions are recorded as not enforced.
/// </para>
/// <para>
/// <b>Arbiter block (Req 6.3, 8.1, 8.2, 22.2).</b> When the render request carries an
/// enabled <see cref="SquadArbiterWiring"/> under <see cref="SquadDeploymentScope.Project"/>,
/// the renderer returns a <c>devin</c> owned block for <c>.devin/hooks.v1.json</c> holding
/// <c>PreToolUse</c> and <c>PostToolUse</c> matcher groups matching <c>run_subagent</c>, each
/// carrying the shared project-wide hook <c>kyber-weave-arbiter hook --harness devin</c> with
/// the wiring's timeout ([F12]; the whole file is the hooks object in the Claude Code format,
/// so the matcher groups sit at the top level). No whole file is emitted; the deployment plan
/// splices the block into whatever the user already has. Because Devin documents no
/// <c>PostToolUse</c> output field, post-dispatch outcomes are logged and reported by
/// <c>audit</c> but never delivered back to the harness, so the render records
/// <c>arbiter-not-enforced</c> with details <c>no-post-dispatch-feedback</c> for every
/// dispatcher agent (design §10.5). A null or disabled wiring, or Global scope (no project
/// configuration to enforce from, Req 22.4), renders no block and leaves the owned files
/// byte-identical.
/// </para>
/// </remarks>
public sealed class DevinRenderer : SquadHarnessRenderer
{
    private const string TargetToken = "devin";

    /// <summary>Matches Devin's subagent dispatch tool ([F12]). The vendor documents only
    /// that <c>run_subagent</c> "takes a profile"; no namespaced spelling of the tool is
    /// documented, so the matcher is the bare name exactly.</summary>
    private const string ArbiterMatcher = "^run_subagent$";

    private const string ArbiterServer = "kyber-weave-arbiter";

    /// <summary>
    /// Lowers the semantic capability vocabulary onto Devin's core tool names, every tool that
    /// performs a capability included. <c>todo_write</c> and <c>skill</c> are granted on every
    /// subagent regardless of capability profile. <c>network.publish</c> is absent because no tool
    /// expresses it, and <c>delegate</c> because granting it cannot keep the roster (see the
    /// class remarks).
    /// </summary>
    /// <remarks>
    /// Fixed tool emission order, so a rendered file is byte-stable regardless of how the
    /// capability profile's permissions happen to enumerate.
    /// </remarks>
    private static readonly SquadToolMap Tools = new(
        [
            ("filesystem.read", ["read", "notebook_read"]),
            ("filesystem.search", ["grep", "glob"]),
            ("filesystem.write", ["edit", "write", "apply_patch", "notebook_edit"]),
            ("process.execute", ["exec", "get_output", "write_to_process", "kill_shell"]),
            ("network.read", ["webfetch", "web_search"])
        ],
        [
            "todo_write", "skill", "read", "notebook_read", "grep", "glob", "edit", "write",
            "apply_patch", "notebook_edit", "exec", "get_output", "write_to_process", "kill_shell",
            "webfetch", "web_search"
        ],
        ["todo_write", "skill"]);

    /// <summary>
    /// The lowered primary agent's <c>triggers</c>: the operator may start it, the model may not
    /// (see the class remarks).
    /// </summary>
    private static readonly string[] UserOnlyTriggers = ["user"];

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.Devin,
        "Devin",
        ".devin",
        "agents",
        SquadModelPolicy.InheritOmitted,
        SquadLineBreaks.LineFeedOnly,
        SkillLicense: null);

    public DevinRenderer()
        : base(HarnessSpec)
    {
    }

    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        if (agent.Invocation == SquadInvocation.Subagent)
        {
            McpPlan mcp = context.GetOrCreateState(McpPlan.Create);
            context.AddPrincipal(RenderSubagent(context, agent, mcp), agent.Resources);
            context.Degradations.AddRange(BuildSubagentDegradations(agent, context.CapabilityProfiles, mcp.ServerNames));
            return;
        }

        context.AddLoweredPrimaryAgent(
            agent,
            () =>
                $"Cannot lower primary agent '{agent.Name}' to a Devin skill: a canonical " +
                $"skill named '{agent.Name}' already occupies that identity. Devin keeps " +
                "agents and skills in separate namespaces with no role-prefixed fallback, " +
                "so this fails closed rather than renaming either one.",
            // The lowered skill runs only when the operator starts it (ADR 0025 decision 7).
            () => context.RenderSkillDocument(
                agent.Name,
                agent.Description,
                agent.InstructionBody,
                additionalFrontmatter: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["triggers"] = UserOnlyTriggers
                }),
            (fallbackProfile, reused) => BuildPrimaryAgentDegradations(
                agent,
                fallbackProfile,
                context.CapabilityProfiles,
                context.CapabilityVocabulary,
                reused));
    }

    /// <summary>
    /// The Arbiter block and its degradation travel with the render only when the wiring is
    /// enabled at project scope; otherwise the render stays byte-identical to omitting the wiring
    /// entirely (Req 22.4). Under Global scope the receipt keeps the wiring-derived global-scope
    /// record instead.
    /// </summary>
    private protected override void RenderArbiter(SquadHarnessRenderContext context)
    {
        if (context.EnabledArbiter is null)
        {
            return;
        }

        context.AddArbiterMatcherGroupBlock(SquadHookBlockFormat.Devin, ArbiterMatcher);
        context.Degradations.AddRange(NoPostDispatchFeedbackDegradations(context.Source));
    }

    /// <summary>
    /// The render-time <c>arbiter-not-enforced</c> records accompanying Devin's block, one
    /// per dispatcher agent.
    /// </summary>
    /// <remarks>
    /// Devin's <c>PostToolUse</c> documents no output field ([F12]), so a dispatch that
    /// slipped past <c>PreToolUse</c> is logged and reported by <c>audit</c> but never
    /// delivered back — the design §10.5 "no hook support at a decision point" family,
    /// recorded at render time per target and agent. The dispatchers own the loss: their
    /// dispatches are the ones whose outcomes cannot reach a decision point, so the roster
    /// comes from <see cref="ArbiterHookWiring.Dispatchers"/> rather than a renderer-local
    /// copy of it. The records are agent-scoped, carrying the agent's real identity and
    /// body digest, because a renderer-emitted degradation must reference a known agent to
    /// pass render validation — the wiring's target-scoped <c>arbiter</c> records are a
    /// receipt-level derivation appended after that validation. The reason goes in
    /// <see cref="SquadDegradationRecord.Details"/> and the receipt keeps the code; it is
    /// never a runtime condition.
    /// </remarks>
    private static IEnumerable<SquadDegradationRecord> NoPostDispatchFeedbackDegradations(SquadSource source)
    {
        IReadOnlySet<string> dispatchers = ArbiterHookWiring.Dispatchers(source);
        return source.Agents
            .Where(agent => dispatchers.Contains(agent.Name))
            .Select(agent => new SquadDegradationRecord(
                Target: TargetToken,
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "arbiter-not-enforced",
                InstructionDigest: agent.BodyDigest,
                Details: "no-post-dispatch-feedback"));
    }

    private static SquadDeploymentFile RenderSubagent(
        SquadHarnessRenderContext context,
        SquadAgent agent,
        McpPlan mcp)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = context.SingleLine(agent.Description)
        };

        string? model = context.ResolveModel(agent);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        frontmatter["allowed-tools"] = ResolveTools(
            agent,
            context.CapabilityProfiles,
            mcp.OrchestratorExcludedToolNames,
            mcp.UniversalToolNames);

        return context.CreateFile(
            $"{context.AgentsDirectory}/{agent.Name}/AGENT.md",
            SquadYamlEmitter.Default.Compose(frontmatter, agent.InstructionBody));
    }

    /// <summary>
    /// Lowers a capability profile onto Devin's tool names. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants, which keeps the lowering
    /// non-broadening by construction; an unresolvable profile grants only the ungoverned base.
    /// </summary>
    private static string[] ResolveTools(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> orchestratorExcludedMcpToolNames,
        IReadOnlyList<string> universalMcpToolNames)
    {
        string profileName = agent.DevinCapabilityProfile ?? agent.CapabilityProfile;

        // MCP names append after the built-ins: they come from source, so they cannot join the
        // fixed renderer-local order, and each group is already server-then-tool from
        // CapabilityDegradations.
        string[] resolved =
        [
            .. Tools.Resolve(profileName, capabilityProfiles),
            .. CapabilityDegradations.ResolveMcpToolNames(
                profileName,
                capabilityProfiles,
                orchestratorExcludedMcpToolNames,
                universalMcpToolNames)
        ];
        if (resolved.Length == 0)
        {
            // Unreachable while the ungoverned base is non-empty, and deliberately loud if that
            // ever changes: what Devin does with an empty allow-list is not documented, and the
            // one reading this renderer must never risk is "every tool".
            throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' resolved to an empty Devin 'allowed-tools' list, which " +
                "cannot be emitted as a restriction.");
        }

        return resolved;
    }

    private static IEnumerable<SquadDegradationRecord> BuildSubagentDegradations(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> mcpServerNames)
    {
        string profileName = agent.DevinCapabilityProfile ?? agent.CapabilityProfile;
        if (!capabilityProfiles.TryGetValue(profileName, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        // 'deny' needs no record: 'allowed-tools' withholds it. Only 'ask' loses meaning.
        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                TargetToken,
                agent.Name,
                agent.Name,
                "safety-narrowed",
                agent.BodyDigest,
                $"Capability profile '{profileName}' requires 'ask' for " +
                $"{string.Join(", ", narrowed)}. Devin prompts for a subagent's tool call " +
                "only in the foreground and only when the session's permission mode has not " +
                "already approved it, a background subagent never prompts, and a subagent " +
                "profile cannot force the prompt, so these narrow to withheld: the " +
                "corresponding tools are absent from the agent's 'allowed-tools' list.");
        }

        List<string> notExpressible = [];

        IReadOnlyList<string> withheldServerNames =
            CapabilityDegradations.WithheldMcpServerNames(profileName, capabilityProfiles, mcpServerNames);
        if (withheldServerNames.Count > 0)
        {
            notExpressible.Add(
                CapabilityDegradations.DescribeWithheldMcp(
                    profileName, withheldServerNames, capabilityProfiles));
        }

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publish) &&
            publish == SquadPermissionDecision.Allow)
        {
            notExpressible.Add(
                "Capability 'network.publish' is allowed but no Devin tool exists to express it.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow)
        {
            string roster = agent.DelegatesTo.Count > 0
                ? string.Join(", ", agent.DelegatesTo)
                : "(none declared)";
            notExpressible.Add(
                $"Capability 'delegate' is allowed, but the canonical delegates-to roster ({roster}) " +
                "cannot be enforced: Devin has no 'allowed_subagents' equivalent, so granting " +
                "nested delegation through 'max-nesting' and 'run_subagent' would reach every " +
                "profile. Neither is emitted, and this subagent cannot delegate on Devin.");
        }

        if (notExpressible.Count > 0)
        {
            yield return new SquadDegradationRecord(
                TargetToken,
                agent.Name,
                agent.Name,
                "permission-not-expressible",
                agent.BodyDigest,
                string.Join(" ", notExpressible));
        }

        SquadPermissionDecision executeDecision =
            profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
                ? exec
                : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision =
            profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
                ? write
                : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            TargetToken,
            agent.Name,
            agent.Name,
            agent.BodyDigest,
            executeDecision,
            writeDecision,
            ["exec"],
            ["edit", "write", "apply_patch", "notebook_edit"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
        }
    }

    private static IEnumerable<SquadDegradationRecord> BuildPrimaryAgentDegradations(
        SquadAgent agent,
        SquadFallbackProfile fallbackProfile,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary,
        bool reusedCanonicalSkill = false)
    {
        string projectionText = reusedCanonicalSkill
            ? "this agent reuses the same-name canonical skill as its entry point, because the " +
              "canonical skill already occupies that identity; no separate lowered skill is emitted"
            : "this agent renders as a Devin skill instead of a '.devin/agents/' profile";

        yield return new SquadDegradationRecord(
            TargetToken,
            agent.Name,
            agent.Name,
            "role-skill-fallback",
            agent.BodyDigest,
            "Devin has no primary-agent primitive: Devin Local is the only top-level " +
            "agent, and a custom profile is always dispatched as a subagent. Fallback profile " +
            $"'{agent.Fallback}' declares no-primary-agent: {fallbackProfile.NoPrimaryAgent}, " +
            "so " + projectionText + ".");

        string rosterText = agent.DelegatesTo.Count > 0
            ? string.Join(", ", agent.DelegatesTo)
            : "(none declared)";

        yield return new SquadDegradationRecord(
            TargetToken,
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            "Capability decisions " +
            $"({CapabilityDegradations.DescribeCapabilityDecisions(agent, capabilityProfiles, capabilityVocabulary)}) " +
            "are not enforced: a Devin skill runs under the session's permissions. Its " +
            "'allowed-tools' and 'permissions.allow' would pre-approve rather than restrict, " +
            "and 'permissions.deny' is not emitted because Devin does not document whether " +
            "it reaches the subagents the skill dispatches, which need the tools this " +
            $"profile denies. Its delegates-to roster ({rosterText}) is instruction-only.");
    }

    /// <summary>
    /// The MCP tool names a render reads once from canonical source rather than a renderer-local
    /// list, so a tool renamed upstream is a source edit, and so the doctor check reads the same
    /// roster. The split into entitlement classes, and the order each class is emitted in, live in
    /// <see cref="CapabilityDegradations"/>: the partition is the one thing two targets must not
    /// each decide. The Arbiter server is granted separately, only through the Arbiter's own grant
    /// path, so it is excluded from the standard MCP partition on every target.
    /// </summary>
    private sealed record McpPlan(
        IReadOnlyList<string> OrchestratorExcludedToolNames,
        IReadOnlyList<string> UniversalToolNames,
        IReadOnlyList<string> ServerNames)
    {
        public static McpPlan Create(SquadHarnessRenderContext context)
        {
            IReadOnlyDictionary<string, IReadOnlyList<string>> standardMcpTools =
                context.Source.Toolchain.RequiredMcpTools
                    .Where(entry => !string.Equals(entry.Key, ArbiterServer, StringComparison.Ordinal))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            return new McpPlan(
                CapabilityDegradations.OrchestratorExcludedMcpToolNames(standardMcpTools),
                CapabilityDegradations.UniversalMcpToolNames(standardMcpTools),
                CapabilityDegradations.DeclaredMcpServerNames(standardMcpTools));
        }
    }
}

using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into OpenCode's native subagent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Subagent and skill contract verified against OpenCode specifications on 2026-09-14:
/// subagents are stored at <c>.opencode/agents/&lt;name&gt;.md</c> containing Markdown with
/// YAML frontmatter. Required keys are <c>name</c>, <c>description</c>, and <c>mode</c>
/// (<c>primary</c> for conductor, <c>subagent</c> for subagents); optional <c>model</c> resolves
/// from <c>models.yml</c> for harness <c>opencode</c> (omitted when <c>inherit</c> or empty).
/// Skills are stored at <c>.opencode/skills/&lt;name&gt;/SKILL.md</c>.
/// </para>
/// <para>
/// OpenCode's <c>permission</c> frontmatter key is an explicit map, and <b>an omitted key is
/// not a withheld one</b>: agent permissions merge with the global config, whose documented
/// behaviour is that most permissions default to <c>allow</c> (opencode.ai/docs/permissions,
/// read 2026-09-21; <c>external_directory</c> and <c>doom_loop</c> are the stated exceptions).
/// Emitting only the granted keys therefore hands back every canonical <c>deny</c> and
/// <c>ask</c> as an ambient allow — the exact escalation the non-broadening guarantee forbids.
/// This renderer consequently pins <b>every</b> key in the taxonomy explicitly, writing
/// <c>deny</c> wherever the lattice does not grant. Only <c>allow</c> grants a permission;
/// <c>ask</c> and <c>deny</c> both withhold. <c>ask</c> is recorded as <see cref="SquadDegradationRecord"/>
/// with code <c>safety-narrowed</c> because OpenCode subagents do not support interactive
/// per-capability confirmation gates.
/// </para>
/// <para>
/// Base ungoverned permissions on every agent: <c>todowrite</c>, <c>skill</c>, <c>question</c>.
/// The first two write nothing and execute nothing; <c>question</c> asks the operator rather
/// than touching the machine, so none of the three can broaden a canonical decision.
/// <c>external_directory</c> and <c>doom_loop</c> map to no capability and are therefore always
/// denied. <c>lsp</c> is governed by <c>filesystem.read</c>: language-server introspection is
/// reading code. Semantic capabilities lower
/// onto OpenCode's permission taxonomy: <c>filesystem.read</c> -&gt; <c>read</c>, <c>filesystem.search</c> -&gt;
/// <c>grep</c>, <c>glob</c>, <c>list</c>, <c>filesystem.write</c> -&gt; <c>edit</c>, <c>process.execute</c> -&gt; <c>bash</c>,
/// <c>network.read</c> -&gt; <c>webfetch</c>, <c>websearch</c>, and <c>delegate</c> -&gt; pattern-based <c>task</c> rules.
/// Server-scoped MCP mapping for the declared <c>kw-docs</c> server grants <c>kw-docs_*</c> to every agent
/// with <c>filesystem.read: allow</c>, pure orchestrators included, excluding shared identities whose
/// canonical skill is the only projection emitted. The orchestrator keeps the grant because that server
/// carries <c>docs_get_coding_standard_for_technology</c> (ADR 0050). The code-graph server is mapped the
/// same way but keeps the orchestrator carve-out, so <c>codegraph_*</c> reaches exactly the agents
/// <see cref="CapabilityDegradations.GrantsOrchestratorExcludedMcpServers"/> entitles: the permission map
/// is where this harness gates MCP reach, and an agent with no key for a server cannot call its tools.
/// Its key is emitted only when granted, never as a deny. A library-lookup server is not mapped, because
/// the carve-out was widened for the code graph and not for library lookups.
/// <c>network.publish</c> has no native tool and is withheld, emitting <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// Skills carry <c>name</c>, single-line <c>description</c>, and <c>license: MIT</c>.
/// Profile-declared shared identities suppress their skill projections per the native
/// single-projection rule.
/// </para>
/// <para>
/// <strong>Arbiter plugin shim (Req 6.1, 25.1):</strong> When the render request carries an
/// enabled <see cref="SquadArbiterWiring"/> under <see cref="SquadDeploymentScope.Project"/>
/// scope, the renderer emits the owned file <c>.opencode/plugins/kyber-arbiter.ts</c> from
/// <see cref="ArbiterPluginShim"/> for harness token <c>opencode</c>. Nothing is rendered
/// when the wiring is null or disabled, or the scope is Global (Req 22.2), so a render
/// without Arbiter is byte-identical to before the field existed.
/// </para>
/// <para>
/// <b>Agent resources are projected beside their principal with a discovery-guard
/// extension.</b> OpenCode's agent loader scans every config root recursively for
/// <c>{agent,agents}/**/*.md</c> files, dotfiles included, and derives an agent name from
/// each match's path (verified against <c>anomalyco/opencode</c>,
/// <c>packages/core/src/config/plugin/agent.ts</c>, on 2026-10-19), so projecting an
/// agent's resource closure beside its principal with a <c>.md</c> extension would
/// register every reference file as a phantom agent. Agent-owned <c>.md</c> resources
/// therefore deploy with a <c>.markdown</c> extension — invisible to the
/// <c>*.md</c>-only glob while staying co-located with, and readable beside, their owning
/// agent — and the owner's authored links are rewritten <c>.md</c> to <c>.markdown</c>
/// via the shared closure-driven rewriter. The deviation from the repository's "authored
/// links resolve verbatim" rule is recorded as <c>resource-links-rewritten</c> so it is
/// visible in the receipt. Skill resources keep the ordinary
/// <see cref="SquadResourceProjection"/> treatment: skill discovery only admits
/// <c>SKILL.md</c> one level deep and never mistakes them for agents.
/// </para>
/// </remarks>
public sealed class OpenCodeRenderer : SquadHarnessRenderer
{
    private const string KyberWeaveMcpPermission = $"{McpServerNames.Docs}_*";

    private static readonly string[] BaseUngovernedPermissions = ["todowrite", "skill", "question"];

    /// <summary>
    /// Lowers the semantic capability vocabulary onto OpenCode's built-in permission names.
    /// <c>network.publish</c> is absent deliberately: no built-in publish tool exists.
    /// <c>delegate</c> is handled separately to emit pattern-based <c>task</c> rules.
    /// </summary>
    private static readonly (string Capability, string[] Permissions)[] CapabilityPermissions =
    [
        ("filesystem.read", ["read", "lsp"]),
        ("filesystem.search", ["grep", "glob", "list"]),
        ("filesystem.write", ["edit"]),
        ("process.execute", ["bash"]),
        ("network.read", ["webfetch", "websearch"])
    ];

    /// <summary>
    /// Emission order, fixed so a rendered agent file is byte-stable regardless of how the
    /// profile's permissions enumerate.
    /// </summary>
    /// <remarks>
    /// Every key OpenCode documents is listed, because a key this renderer leaves out is a key
    /// the global config grants by default. <c>external_directory</c> and <c>doom_loop</c> have
    /// no canonical capability and so are never granted, but they are still emitted: relying on
    /// OpenCode's own default for them would make this map's safety depend on a default the
    /// operator can change.
    /// </remarks>
    private static readonly string[] PermissionOrder =
    [
        "todowrite",
        "skill",
        "question",
        "read",
        "lsp",
        "grep",
        "glob",
        "list",
        "edit",
        "bash",
        "webfetch",
        "websearch",
        "external_directory",
        "doom_loop",
        KyberWeaveMcpPermission,
        "task"
    ];

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.OpenCode,
        "OpenCode",
        ".opencode",
        "agents",
        SquadModelPolicy.InheritOrBlankOmitted);

    public OpenCodeRenderer()
        : base(HarnessSpec)
    {
    }

    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description,
            ["mode"] = agent.Invocation == SquadInvocation.Primary ? "primary" : "subagent"
        };

        string? model = context.ResolveModel(agent);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        // Always emit permission map: omitting it inherits ambient tool access (widening). Only a
        // suppressed shared identity withholds the KyberWeave MCP permission: the primary wrappers
        // edit files and run commands, so withholding it would strip the tools their role exists
        // to use.
        frontmatter["permission"] = ResolvePermissions(
            agent,
            context.CapabilityProfiles,
            context.SuppressedSkillIdentities,
            context.GetOrCreateState(McpPlan.Create).CodeGraphServerNames);

        context.AddDiscoveryGuardedAgent(agent, frontmatter, SquadYamlEmitter.Default, "{agent,agents}/**/*.md");
        context.Degradations.AddRange(BuildDegradationRecords(agent, context.CapabilityProfiles));
    }

    private protected override void RenderArbiter(SquadHarnessRenderContext context) =>
        context.AddArbiterPluginShim(".opencode/plugins/kyber-arbiter.ts", "plugins/kyber-arbiter.ts");


    /// <summary>
    /// Lowers a capability profile onto OpenCode's closed permission map. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction.
    /// </summary>
    private static Dictionary<string, object> ResolvePermissions(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlySet<string> suppressedSkillIdentities,
        IReadOnlyList<string> codeGraphServerNames)
    {
        HashSet<string> granted = new(BaseUngovernedPermissions, StringComparer.Ordinal);
        object? taskPermissionValue = null;

        // An unresolvable profile grants nothing beyond the ungoverned permissions. Falling back
        // to "grant everything" here would turn a source error into silent widening.
        if (capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            foreach ((string capability, string[] permissions) in CapabilityPermissions)
            {
                if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                    decision == SquadPermissionDecision.Allow)
                {
                    foreach (string permission in permissions)
                    {
                        granted.Add(permission);
                    }
                }
            }

            // kw-docs reaches every read-entitled agent, the pure orchestrator included: it
            // carries docs_get_coding_standard_for_technology, which the role that assigns work needs to point its
            // delegates at the standards they are held to (ADR 0050). A suppressed shared identity
            // keeps no grant, because its canonical skill is the only projection emitted.
            if (!suppressedSkillIdentities.Contains(agent.Name) &&
                CapabilityDegradations.GrantsUniversalMcpServers(agent.CapabilityProfile, capabilityProfiles))
            {
                granted.Add(KyberWeaveMcpPermission);
            }

            // The code graph keeps the orchestrator carve-out, so its own keys are gated on its
            // own predicate. A server name comes from the toolchain roster, so a roster that
            // declares no code-graph server adds nothing rather than an entry that resolves
            // against no configured server.
            if (!suppressedSkillIdentities.Contains(agent.Name) &&
                CapabilityDegradations.GrantsOrchestratorExcludedMcpServers(
                    agent.CapabilityProfile,
                    capabilityProfiles))
            {
                foreach (string serverName in codeGraphServerNames)
                {
                    granted.Add($"{serverName}_*");
                }
            }

            if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                delegateDecision == SquadPermissionDecision.Allow)
            {
                if (agent.DelegatesTo.Count > 0)
                {
                    Dictionary<string, string> delegatesMap = new(StringComparer.Ordinal);
                    foreach (string target in agent.DelegatesTo.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        delegatesMap[target] = "allow";
                    }

                    taskPermissionValue = delegatesMap;
                }
                else
                {
                    taskPermissionValue = "allow";
                }
            }
        }

        Dictionary<string, object> ordered = new(StringComparer.Ordinal);
        foreach (string key in PermissionOrder)
        {
            if (string.Equals(key, "task", StringComparison.Ordinal))
            {
                // A withheld delegate is an explicit deny, not an absent key: absent means the
                // global default decides, and the global default is allow.
                ordered["task"] = taskPermissionValue ?? "deny";
                continue;
            }

            if (string.Equals(key, KyberWeaveMcpPermission, StringComparison.Ordinal))
            {
                // The code-graph keys sit beside the universal one: all of them are per-server MCP
                // entries rather than members of OpenCode's own closed vocabulary, and each is
                // emitted only for the agents its predicate entitles. An unentitled agent gets no
                // key at all rather than a deny, because a deny would pin a claim about a server
                // the canonical source may never declare, and an absent grant withholds already.
                foreach (string serverName in codeGraphServerNames)
                {
                    string codeGraphPermission = $"{serverName}_*";
                    if (granted.Contains(codeGraphPermission))
                    {
                        ordered[codeGraphPermission] = "allow";
                    }
                }
            }

            ordered[key] = granted.Contains(key) ? "allow" : "deny";
        }

        return ordered;
    }


    private static IEnumerable<SquadDegradationRecord> BuildDegradationRecords(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        // 'deny' needs no record: the rendered allowlist withholds the tool. Only 'ask'
        // loses meaning — OpenCode has no per-capability confirmation gate on subagents.
        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                "opencode",
                agent.Name,
                agent.Name,
                "safety-narrowed",
                agent.BodyDigest,
                $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                $"{string.Join(", ", narrowed)}. OpenCode's permissions cannot prompt " +
                "for per-capability confirmation, so these narrow to 'deny' and the " +
                "corresponding permissions are withheld.");
        }

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision != SquadPermissionDecision.Deny)
        {
            notExpressibleDetails.Add(
                "OpenCode has no native network publishing tool; tool withheld for capability 'network.publish'.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow &&
            agent.DelegatesTo.Count > 0)
        {
            notExpressibleDetails.Add(
                "OpenCode ignores task(roster) parentheses when this definition " +
                "runs as a nested subagent; the permitted delegation roster is not enforced " +
                "for nested task spawns. Roster: " +
                string.Join(", ", agent.DelegatesTo) + ".");
        }

        if (notExpressibleDetails.Count > 0)
        {
            yield return new SquadDegradationRecord(
                "opencode",
                agent.Name,
                agent.Name,
                "permission-not-expressible",
                agent.BodyDigest,
                string.Join(" ", notExpressibleDetails));
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
            "opencode",
            agent.Name,
            agent.Name,
            agent.BodyDigest,
            executeDecision,
            writeDecision,
            ["bash"],
            ["edit"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
        }
    }

    /// <summary>
    /// The server names this renderer's permission map grants from, resolved once per render.
    /// </summary>
    /// <remarks>
    /// Read from the toolchain roster through the shared derivation rather than spelled in this
    /// file, so the server is renamed in one place and this target cannot disagree with the other
    /// single-server targets about which server the widening names (ADR 0050 decision 4).
    /// </remarks>
    private sealed record McpPlan(IReadOnlyList<string> CodeGraphServerNames)
    {
        public static McpPlan Create(SquadHarnessRenderContext context) => new(
            CapabilityDegradations.CodeGraphMcpServerNames(context.Source.Toolchain.RequiredMcpTools));
    }
}

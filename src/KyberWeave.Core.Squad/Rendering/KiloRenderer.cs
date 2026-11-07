using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

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
/// <b>Kilo does have a permission lattice, and it is an approval lattice rather than a visibility
/// allow-list.</b> Agent frontmatter carries a first-class <c>permission</c> map whose values are
/// <c>ask</c>, <c>allow</c>, <c>deny</c>, or null; the deprecated <c>tools: {name: boolean}</c> form
/// is folded into it during the loader's normalization, and an unrecognised frontmatter key is
/// tolerated rather than rejected (verified against the published <c>@kilocode/cli</c> 7.8.18,
/// <c>Kilo-Org/kilocode</c> tag <c>v7.8.18</c> — the loader at <c>packages/opencode/src/config/agent.ts</c>
/// globs <c>{agent,agents}/**/*.md</c>, merges every frontmatter key into the agent config, and
/// validates the result against the agent schema). <c>allow</c> means the call runs without an ask
/// prompt; it does not hide another tool, and only a <c>deny</c> rule whose pattern is <c>*</c>
/// removes one from the model's view. The entry this renderer emits is therefore a pre-approval,
/// not isolation: it removes a prompt — and, because a headless subagent's ask fails closed, a
/// likely failure — from tools a Squad subagent is entitled to call. It restricts nothing.
/// </para>
/// <para>
/// <b>MCP tools are gated by that same map, under a namespaced key.</b> The key is
/// <c>{server}_{tool}</c>, with the server name sanitized only for characters outside
/// <c>[a-zA-Z0-9_-]</c> — so the hyphen in <c>kw-docs</c> survives, and <c>kw-docs_*</c> is
/// the server wildcard (a <c>kw_docs_*</c> spelling would parse cleanly and match nothing). An
/// agent the universal MCP predicate entitles to this server emits that wildcard with
/// <c>allow</c>, the pure orchestrator included, because the server carries
/// <c>docs_get_coding_standard_for_technology</c>, the tool that hands an agent the standards it
/// is held to (ADR 0050). An agent the predicate does not entitle emits no entry at all: for a
/// subagent, absence of <c>allow</c> is the withholding, and a <c>deny</c> on a server the operator
/// may never have configured would assert more than canonical source does — the capability lattice
/// declares no MCP capability to derive one from.
/// </para>
/// <para>
/// <b>The server half of the wiring stays the operator's.</b> An agent file cannot add an MCP
/// server: the server itself is session-level configuration (the <c>mcp</c> key in
/// <c>kilo.jsonc</c>), and Squad writes no harness settings file. A grant with no configured server
/// reaches nothing, which is why this renderer emits the permission entry and stops there rather
/// than pretending the entry supplies the connection.
/// </para>
/// <para>
/// <b>Capability permissions remain unexpressed, and that gap is recorded.</b> The
/// <c>permission</c> map is keyed by tool name — built-in tools by their own names, MCP tools as
/// <c>{server}_{tool}</c> — so the canonical capability vocabulary (<c>filesystem.read</c>,
/// <c>process.execute</c>, ...) is not a key Kilo reads. Translating the lattice onto Kilo's tool
/// keys is a separate decision this renderer does not take, so every capability a profile
/// constrains is recorded as a structured degradation under code
/// <c>permission-not-expressible</c>, deny decisions included, rather than being silently dropped
/// (preventing capability widening).
/// </para>
/// <para>
/// <b>Agent resources are projected beside their principal with a discovery-guard
/// extension.</b> Kilo's agent loader scans every config root for
/// <c>{agent,agents}/**/*.md</c> files with dotfiles and symlinks followed, and derives
/// an entry name from each match's relative path (verified against
/// <c>Kilo-Org/kilocode</c>, <c>packages/opencode/src/config/agent.ts</c> — the glob
/// inherited verbatim from OpenCode v1.18.26 — on 2026-10-19), so projecting an
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
/// <para>
/// <strong>Arbiter plugin shim (Req 6.3, 25.1):</strong> Kilo loads Bun plugins from
/// <c>.kilo/plugin/*.ts</c> with the same plugin API as OpenCode, so when the render request
/// carries an enabled <see cref="SquadArbiterWiring"/> under
/// <see cref="SquadDeploymentScope.Project"/> scope, the renderer emits the owned file
/// <c>.kilo/plugin/kyber-arbiter.ts</c> from <see cref="ArbiterPluginShim"/> for harness
/// token <c>kilo</c>. Nothing is rendered when the wiring is null or disabled, or the scope
/// is Global (Req 22.2), so a render without Arbiter is byte-identical to before the field
/// existed.
/// </para>
/// </remarks>
public sealed class KiloRenderer : SquadHarnessRenderer
{
    /// <summary>
    /// The <c>permission</c> entry naming the whole <c>kw-docs</c> MCP surface: Kilo gates MCP
    /// tools under the same permission map as built-in ones, keyed <c>{server}_{tool}</c> with the
    /// server name sanitized only for characters outside <c>[a-zA-Z0-9_-]</c>. The hyphen is
    /// load-bearing — an underscore spelling would parse and match nothing — so the key is a
    /// constant rather than assembled at the call site, and a test pins the emitted bytes.
    /// </summary>
    private const string KyberWeaveMcpPermission = $"{McpServerNames.Docs}_*";

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.Kilo,
        "Kilo",
        ".kilo",
        "agents",
        SquadModelPolicy.InheritOmitted);

    public KiloRenderer()
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

        // kw-docs reaches every read-entitled agent, the pure orchestrator included: it
        // carries docs_get_coding_standard_for_technology, and the role that assigns work is
        // the role telling its delegates which standards they are held to (ADR 0050). Kilo
        // sanitizes the server name only for characters outside [a-zA-Z0-9_-], so "kw-docs"
        // keeps its hyphen and this is the wildcard Kilo's matcher resolves for
        // kw-docs_docs_get_source_repository_and_revision. An unentitled agent emits no entry:
        // absence of "allow" withholds for a headless subagent (its ask fails closed), and a
        // "deny" here would out-assert a canonical source that declares no MCP reach.
        if (CapabilityDegradations.GrantsUniversalMcpServers(agent.CapabilityProfile, context.CapabilityProfiles))
        {
            frontmatter["permission"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [KyberWeaveMcpPermission] = "allow"
            };
        }

        context.AddDiscoveryGuardedAgent(agent, frontmatter, SquadYamlEmitter.Default, "{agent,agents}/**/*.md");
        context.AddDegradation(BuildDegradationRecord(context, agent));
    }

    /// <summary>
    /// Kilo loads Bun plugins from <c>.kilo/plugin/*.ts</c> with OpenCode's plugin API.
    /// </summary>
    private protected override void RenderArbiter(SquadHarnessRenderContext context) =>
        context.AddArbiterPluginShim(".kilo/plugin/kyber-arbiter.ts", "plugin/kyber-arbiter.ts");

    private static SquadDegradationRecord? BuildDegradationRecord(SquadHarnessRenderContext context, SquadAgent agent)
    {
        if (!context.CapabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            return null;
        }

        // Kilo's permission map is keyed by tool name (built-in tools by their own names, MCP tools
        // as {server}_{tool}), so the capability vocabulary is not a key it reads and this renderer
        // translates no capability onto one. Every configured decision — allow, ask, deny — is
        // therefore unexpressed at the harness boundary; recording the deny constraints in
        // particular ensures canonical restrictions are not silently dropped (preventing capability
        // widening). No record names MCP servers the agent was denied: the entitlement decides only
        // whether an approval is emitted, and an agent that is not entitled gets no entry rather
        // than a deny, so there is no withheld-server sentence to record.
        return context.CapabilitiesNotExpressible(
            agent,
            context.CapabilityVocabulary.Where(profile.Permissions.ContainsKey).ToList(),
            "Kilo's permission map is keyed by tool name, not by capability, so those decisions are unexpressed");
    }
}

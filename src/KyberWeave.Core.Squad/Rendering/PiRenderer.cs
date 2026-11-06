using System.Text;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into the custom-agent and skill file formats defined by
/// the third-party <c>@tintinweb/pi-subagents</c> extension for the Pi coding agent.
/// </summary>
/// <remarks>
/// <para>
/// Facts verified 2026-09-14 against the installed <c>@earendil-works/pi-coding-agent</c>
/// <b>0.84.4</b> (package.json; the Homebrew keg label 0.84.1 is stale) and
/// <c>@tintinweb/pi-subagents</c> <b>0.19.0</b> (peer dependency
/// <c>@earendil-works/pi-coding-agent &gt;=0.84.0</c>). Pi core itself ships no sub-agents
/// and no permission prompts ("No sub-agents" / "No permission popups",
/// pi-coding-agent README); the extension is what defines the Claude Code-like custom-agent
/// file format this renderer targets. A project must install the extension (or later, and
/// have it be at least 0.19.0) for the emitted <c>.pi/agents/</c> files to have any effect.
/// </para>
/// <para>
/// <b>Discovery roots and precedence.</b> Agents load lowest to highest precedence from
/// <c>$PI_CODING_AGENT_DIR/agents/*.md</c> (default <c>~/.pi/agent/agents/</c>), then
/// <c>&lt;cwd&gt;/.agents/agents/*.md</c>, then <c>&lt;cwd&gt;/.pi/agents/*.md</c> — later
/// loads overwrite earlier ones by type, and only direct <c>*.md</c> children are read (a
/// resource file must live one directory level deeper than its owning agent, never as a
/// sibling <c>*.md</c>). Skills load from global <c>~/.pi/agent/skills/</c> and
/// <c>~/.agents/skills/</c>, and from project <c>.pi/skills/</c> and <c>.agents/skills/</c>
/// (searched from the working directory up to the git root) — <b>but the project roots load
/// only once the project is trusted</b>, so an untrusted checkout shows the project's agents
/// (the agent loader has no trust check) while hiding its skills, including a lowered
/// primary agent. Skill name collisions resolve first-found-wins by root precedence rank
/// (project settings, project auto-discovered, user settings, user auto-discovered,
/// package), with a diagnostic naming the skipped path; within the project, <c>.pi/skills</c>
/// is added before <c>.agents/skills</c>, so this renderer's own output always wins a
/// same-name collision with an Antigravity-lowered skill under <c>.agents/skills/</c>.
/// </para>
/// <para>
/// <b>Accepted agent frontmatter keys</b> (custom-agents.ts / agent-types.ts): <c>name</c>,
/// <c>description</c>, <c>display_name</c>, <c>color</c>, <c>tools</c>, <c>extensions</c>,
/// <c>exclude_extensions</c>, <c>skills</c>, <c>memory</c>, <c>disallowed_tools</c>,
/// <c>isolation</c>, <c>model</c>, <c>thinking</c>, <c>max_turns</c>, <c>persist_session</c>,
/// <c>output_transcript</c>, <c>session_dir</c>, <c>allowed_subagents</c>,
/// <c>prompt_mode</c>, <c>inherit_context</c>, <c>run_in_background</c>, <c>isolated</c>,
/// <c>enabled</c>. Every other key is silently ignored, so this renderer emits only
/// <c>name</c>, <c>description</c>, <c>model</c>, <c>thinking</c> (conditionally), <c>tools</c>, <c>extensions</c>, and
/// <c>allowed_subagents</c> — the plan's approved subset — never the rest. The <c>thinking</c>
/// key is emitted only when the resolved <c>pi:</c> value carries a <c>[thinking=&lt;level&gt;]</c>
/// suffix; the level is validated against the closed domain and the key is omitted if no suffix is present.
/// A name containing <c>:</c> is skipped by the loader, so the canonical name is emitted verbatim and never
/// decorated. Skill frontmatter accepts <c>name</c> (1-64 lowercase-a-z0-9-hyphen
/// characters, no leading/trailing/doubled hyphen), a required <c>description</c> (up to
/// 1024 characters), and an optional <c>license</c>; unknown fields are ignored there too.
/// </para>
/// <para>
/// <b>The seven-tool vocabulary.</b> <c>tools</c> accepts a CSV or array drawn from
/// <c>read, bash, edit, write, grep, find, ls</c>; omitting the key grants every built-in
/// (silent widening for any canonical <c>deny</c>), and an unknown name fails loudly. This
/// renderer therefore always emits <c>tools</c> — the fixed CSV order
/// <c>read, grep, find, ls, edit, write, bash</c>, or the literal <c>none</c> when nothing is
/// granted — mirroring why <see cref="ClaudeRenderer"/> always emits its own allow-list.
/// Only <see cref="SquadPermissionDecision.Allow"/> grants a tool; <c>ask</c> and <c>deny</c>
/// both withhold, because Pi has no per-capability permission prompt and <c>tools</c> is
/// binary — an <c>ask</c> decision is therefore recorded as <c>safety-narrowed</c> rather
/// than expressed in frontmatter (owner decision Q1-A, 2026-09-14). <c>network.read</c>
/// has no expressible built-in Pi tool (R8, re-confirmed 2026-10-24: bare <c>mcp</c> is not
/// a built-in — plain <c>tools:</c> names must be built-ins, and subagents inherit the
/// <c>-builtin:mcp</c> global opt-out, so the loader refuses it under the unknown-name
/// rule); read-only web access arrives instead via the canonical agent-file overlay
/// (<c>pi-extensions</c> / <c>pi-tools</c>), which the renderer lowers onto the declared
/// <c>extensions</c> sequence and <c>ext:</c> tool entries. <c>network.publish</c>
/// still has no built-in Pi tool, so an <c>allow</c> decision for it records
/// <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// <b><c>extensions</c>.</b> <c>extensions: false</c> loads no extensions for the agent —
/// emitted on every rendered agent that carries no overlay of either kind, per the owner's
/// Q1-A decision, so a role outside the MCP entitlement reaches no CodeGraph, docs MCP, or web
/// tooling beyond the built-ins. An agent declaring the canonical <c>pi-extensions</c> overlay,
/// or receiving the derived <c>kw-docs</c> overlay described below, instead emits the
/// merged list as a YAML sequence (the exact form live-verified 2026-10-24; the CSV-scalar form
/// is unverified and is not emitted).
/// This combines with <c>allowed_subagents</c> without conflict, because nested-subagent
/// tools are injected directly rather than through a loaded extension.
/// </para>
/// <para>
/// <b>Derived <c>kw-docs</c> overlay (ADR 0050).</b> Every agent whose capability profile
/// resolves and allows <c>filesystem.read</c> receives the <c>pi-mcp-adapter</c> extension and
/// the <c>kw-docs</c> tool entries even when its canonical body declares no overlay: the
/// proxy entry <c>ext:pi-mcp-adapter/mcp</c> first, then one entry per tool
/// <c>toolchain.yml</c> declares for that server. The pure-orchestrator carve-out does not
/// apply to this server — <c>conductor</c> is entitled on the same file-read rule as every
/// other agent — because <c>kw-docs</c> carries <c>docs_get_coding_standard_for_technology</c>,
/// and the role that assigns work is the role deciding which standards apply to whom. The declared
/// <c>pi-extensions</c> and <c>pi-tools</c> remain the vehicle for everything else: the
/// <c>context7</c> pair stays a hand-authored declaration on <c>research-agent</c>, the ADR 0019
/// precedent for this overlay. The three lists merge rather than replace — built-ins, then
/// declared entries, then derived ones — keeping first occurrence, so an agent that already
/// declares the proxy tool renders it once.
/// </para>
/// <para>
/// <b>Why the hyphen becomes an underscore.</b> The server name contains a hyphen and this
/// surface does not. <c>pi-mcp-adapter</c>'s deferred/tool-search surface names a tool
/// <c>mcp__&lt;namespace&gt;__&lt;tool&gt;</c>, where the namespace maps the server name with
/// <c>-</c> to <c>_</c> before any other character is mapped, and the tool segment takes the
/// same treatment. So this server's namespace is <c>kw_docs</c> and its entries are
/// <c>ext:pi-mcp-adapter/mcp__kw_docs__docs_answer_question_from_repo_documentation</c> and its
/// seven siblings, never <c>mcp__kw-docs__*</c> — which resolves against no registered Pi tool. The
/// <c>mcp__context7__query_docs</c> precedent in the corpus does not reveal the mapping, because
/// that server name has no hyphen; the tool segment is what turns <c>query-docs</c> into
/// <c>query_docs</c>. The hyphenated spelling exists only on the adapter's <i>direct</i>-tool
/// surface (<c>kw-docs_docs_get_source_repository_and_revision</c>), and this renderer never
/// declares onto that surface: Squad's declarations target the <c>ext:</c> surface, so the
/// underscored namespace is the one to use.
/// </para>
/// <para>
/// <b>Version skew, checked.</b> The naming rule was verified against <c>pi-mcp-adapter</c>
/// <b>7.0.0</b> (published 2026-10-25, the current release) by running its published
/// <c>deferred-tool-surface.ts</c> <c>formatDeferredToolName</c> over this roster, which
/// returned the eight underscored names above. It arrived in <b>6.0.0</b>: 5.4.0, which one
/// machine here still runs, has no per-tool deferred registration at all and would name these
/// tools <c>kw-docs_docs_answer_question_from_repo_documentation</c>, so 5.x cannot be the
/// target — the corpus's checked-in <c>mcp__context7__resolve_library_id</c> entry resolves on no
/// version before 6.0.0 either. An operator still on 5.x keeps the <c>mcp</c> proxy entry working
/// and loses only the enumerable per-tool names.
/// </para>
/// <para>
/// <b>Server configuration stays the operator's.</b> Squad writes no harness settings file (the
/// standing no-settings decision). The adapter reads its servers from a project
/// <c>.mcp.json</c>, a user-global <c>~/.config/mcp/mcp.json</c>, <c>~/.agents/mcp.json</c>, or
/// Pi's own agent-directory <c>mcp.json</c>, and a project-scoped server needs a trust approval
/// on first use. This renderer declares the extension and the tool ids and configures no
/// server, so a deployment with no <c>kw-docs</c> entry renders the overlay and reaches
/// nothing through it until the operator adds one.
/// </para>
/// <para>
/// <b>Owner probe.</b> That these entries resolve in a live session — the extension registering
/// the names and a subagent calling one — is not something a renderer test can establish. It
/// remains an owner probe, and until it is run the derived wiring is recorded
/// <c>[NOT LIVE-VERIFIED]</c>. A primary agent lowers to a skill, which declares no
/// <c>tools</c> or <c>extensions</c>, so <c>conductor</c> cannot carry the grant on this target
/// and its <c>permission-not-expressible</c> record says so.
/// </para>
/// <para>
/// <b><c>allowed_subagents</c> enforcement and depth cap.</b> The key is off by default and
/// runtime-enforced: an out-of-list, unknown, or disabled subagent type is rejected with no
/// fallback, so this renderer emits it — the canonical <c>delegates-to</c> roster, CSV in
/// canonical order — only when the capability profile allows <c>delegate</c> and the roster
/// is non-empty; an allowed-but-empty roster would need the literal <c>all</c>, which reaches
/// <c>general-purpose</c> (every tool) and is therefore recorded as
/// <c>permission-not-expressible</c> rather than emitted. <c>maxSubagentDepth</c> defaults to
/// 2 (main session 0, subagent 1, nested child 2); this renderer never writes
/// <c>.pi/subagents.json</c>; onboarding recommends <c>fallbackSubagent: none</c> so an
/// unresolvable <c>Agent</c> call fails rather than silently running with every tool.
/// </para>
/// <para>
/// <strong>Arbiter extension (Req 6.2, 22.2, 25.1):</strong> When the render request carries an
/// enabled <see cref="SquadArbiterWiring"/> under <see cref="SquadDeploymentScope.Project"/>
/// scope, the renderer emits the owned file <c>.pi/extensions/kyber-arbiter.ts</c> from
/// <see cref="RenderArbiterExtension(int, SquadDeploymentScope)"/> for harness token <c>pi</c>. Nothing is rendered
/// when the wiring is null or disabled, or the scope is Global (Req 22.2), so a render
/// without Arbiter is byte-identical to before the field existed.
/// </para>
/// <para>
/// <b>Primary-agent lowering.</b> Pi core has no primary-agent selection primitive —
/// <c>.pi/SYSTEM.md</c> and <c>--system-prompt</c> replace the whole session prompt, not a
/// per-agent one — so a canonical agent with <see cref="SquadInvocation.Primary"/> cannot
/// render at <c>.pi/agents/&lt;name&gt;.md</c> the way the four other native targets do.
/// Its fallback profile decides the outcome: <c>no-primary-agent: skill</c> renders it as a
/// top-level <c>.pi/skills/&lt;name&gt;/SKILL.md</c> (running it at depth 0, where nested
/// subagent delegation stays available to its own roster, unlike depth 1 if it were forced
/// into <c>.pi/agents/</c>), recording <c>role-skill-fallback</c> and, because a top-level
/// skill enforces none of the capability lattice, <c>permission-not-expressible</c> for every
/// vocabulary capability; <c>no-primary-agent: omit</c> emits nothing and records
/// <c>omitted</c>. Unlike <see cref="AntigravityRenderer"/>'s role-skill fallback, Pi has
/// separate agent and skill namespaces, so there is no role-prefixed escape hatch: if the
/// lowered identity is already occupied by a canonical skill, rendering fails closed with
/// <see cref="SquadRenderValidationException"/> naming it, rather than emitting a
/// <c>role-</c>-prefixed duplicate the way a genuinely primitive-less fallback target would.
/// </para>
/// </remarks>
public sealed class PiRenderer : SquadHarnessRenderer
{
    private const string ArbiterExtensionFileRelativePath = ".pi/extensions/kyber-arbiter.ts";

    /// <summary>The MCP extension every agent carrying a derived overlay loads.</summary>
    private const string McpAdapterExtension = "pi-mcp-adapter";

    /// <summary>
    /// The adapter's proxy tool: it reaches any configured MCP server by name, so it is the one
    /// entry that keeps working whichever version registered the per-tool names.
    /// </summary>
    private const string McpProxyToolEntry = "ext:pi-mcp-adapter/mcp";

    /// <summary>
    /// The MCP server ADR 0050 widened to every read-entitled agent, the pure orchestrator
    /// included, because it carries <c>docs_get_coding_standard_for_technology</c>.
    /// </summary>
    private const string KyberWeaveMcpServer = McpServerNames.Docs;

    /// <summary>
    /// Lowers the semantic capability vocabulary onto Pi's built-in tool names, verified
    /// against the pi-subagents 0.19.0 README ("Tool &amp; extension scoping") on 2026-09-14.
    /// <c>network.read</c> is absent deliberately: bare <c>mcp</c> is not a built-in Pi tool
    /// (re-confirmed 2026-10-24), so it still has no expressible lowering — web access arrives
    /// via the canonical <c>pi-extensions</c> / <c>pi-tools</c> overlay instead.
    /// <c>network.publish</c> is absent deliberately: it still has no built-in Pi tool.
    /// <c>delegate</c> is handled separately because an allowed, non-empty
    /// roster emits <c>allowed_subagents</c>, not a tool.
    /// </summary>
    /// <remarks>
    /// Fixed <c>tools</c> CSV emission order, so a rendered agent file is byte-stable
    /// regardless of how the profile's permissions enumerate.
    /// </remarks>
    private static readonly SquadToolMap Tools = new(
        [
            ("filesystem.read", ["read"]),
            ("filesystem.search", ["grep", "find", "ls"]),
            ("filesystem.write", ["edit", "write"]),
            ("process.execute", ["bash"])
        ],
        ["read", "grep", "find", "ls", "edit", "write", "bash"]);

    private static readonly HashSet<string> AllowedThinkingLevels = new(StringComparer.Ordinal)
    {
        "off", "minimal", "low", "medium", "high", "max"
    };

    private static readonly SquadYamlEmitter Yaml = new(new SerializerBuilder()
        .WithTypeConverter(new PiExtensionsFlowSequenceConverter())
        .Build());

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.Pi,
        "Pi",
        ".pi",
        "agents",
        SquadModelPolicy.InheritOmitted,
        SquadLineBreaks.LineFeedOnly);

    public PiRenderer()
        : base(HarnessSpec)
    {
    }

    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        // Derived once: the roster and the adapter's naming rule are target-wide, so a
        // per-agent re-derivation could only produce the same list at greater cost.
        IReadOnlyList<string> kyberWeaveOverlayTools = context.GetOrCreateState(static c =>
            ResolveKyberWeaveOverlayTools(c.Source.Toolchain.RequiredMcpTools));

        if (agent.Invocation == SquadInvocation.Subagent)
        {
            context.AddPrincipal(RenderSubagentAgent(context, agent, kyberWeaveOverlayTools), agent.Resources);
            context.Degradations.AddRange(BuildSubagentDegradations(agent, context.CapabilityProfiles));
            return;
        }

        // R3: a lowered identity a canonical skill already holds fails closed (ADR 0019).
        context.AddLoweredPrimaryAgent(
            agent,
            () =>
                $"Cannot lower primary agent '{agent.Name}' to a Pi skill: a canonical " +
                $"skill named '{agent.Name}' already occupies that identity. Pi has " +
                "separate agent and skill namespaces with no role-prefixed fallback " +
                "mechanism, so this is a fail-closed condition rather than a naming " +
                "collision this renderer can resolve on its own.",
            () => context.RenderSkillDocument(agent.Name, agent.Description, agent.InstructionBody, Yaml),
            (fallbackProfile, reused) => BuildPrimaryAgentDegradations(
                agent,
                fallbackProfile,
                context.CapabilityProfiles,
                context.CapabilityVocabulary,
                kyberWeaveOverlayTools,
                reused));
    }

    private protected override void RenderCanonicalSkills(SquadHarnessRenderContext context) =>
        context.EmitCanonicalSkills(skill =>
            context.RenderSkillDocument(skill.Name, skill.Description, skill.InstructionBody, Yaml));

    /// <summary>The owned extension shim, only for an enabled wiring.</summary>
    private protected override void RenderArbiter(SquadHarnessRenderContext context)
    {
        if (context.EnabledArbiter is not { } arbiter)
        {
            return;
        }

        string relativePath = context.Scope == SquadDeploymentScope.Global
            ? "extensions/kyber-arbiter.ts"
            : ArbiterExtensionFileRelativePath;

        context.Files.Add(context.CreateFile(
            relativePath,
            RenderArbiterExtension(arbiter.HookTimeoutSeconds, context.Scope)));
    }

    private static SquadDeploymentFile RenderSubagentAgent(
        SquadHarnessRenderContext context,
        SquadAgent agent,
        IReadOnlyList<string> mcpOverlayTools)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = context.SingleLine(agent.Description)
        };

        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles = context.CapabilityProfiles;
        string? resolvedModel = context.ResolveModel(agent);
        (string? model, string? thinkingLevel) = resolvedModel is null
            ? (null, null)
            : ParseThinkingSuffix(resolvedModel);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        if (thinkingLevel is not null)
        {
            frontmatter["thinking"] = thinkingLevel;
        }

        // Two sources, one merged surface: the entitlement derives the kw-docs overlay for
        // any agent allowed to read the filesystem (ADR 0050), and the agent's own declaration
        // carries everything else. A roster with no kw-docs entry, or a profile outside the
        // entitlement, derives nothing — so an unmatched grant can never load an extension whose
        // only purpose is that server.
        bool carriesMcpOverlay = mcpOverlayTools.Count > 0 &&
            CapabilityDegradations.GrantsUniversalMcpServers(agent.CapabilityProfile, capabilityProfiles);

        // Always emit tools: omitting the key grants every built-in (widening). The
        // capability-derived CSV comes first in the fixed tool order; the declared pi-tools
        // overlay entries follow in declared canonical order, then the derived ones.
        IReadOnlyList<string> tools = MergeTools(
            Tools.Resolve(agent.CapabilityProfile, capabilityProfiles),
            agent.PiTools,
            carriesMcpOverlay ? mcpOverlayTools : []);
        frontmatter["tools"] = tools.Count == 0 ? "none" : string.Join(", ", tools);

        // Every agent without an overlay of either kind gets `false` (owner decision Q1-A,
        // 2026-09-14); an overlay agent emits the merged extension ids as a YAML sequence.
        IReadOnlyList<string> extensions = MergeExtensions(agent.PiExtensions, carriesMcpOverlay);
        frontmatter["extensions"] = extensions.Count == 0
            ? false
            : new PiExtensionsFlowSequence(extensions);

        if (IsDelegateAllowed(agent, capabilityProfiles) && agent.DelegatesTo.Count > 0)
        {
            frontmatter["allowed_subagents"] = string.Join(", ", agent.DelegatesTo);
        }

        return context.CreateFile(
            $"{context.AgentsDirectory}/{agent.Name}.md",
            Yaml.Compose(frontmatter, agent.InstructionBody));
    }

    /// <summary>
    /// Renders the Pi project extension that bridges Pi tool calls to the
    /// <c>kyber-weave-arbiter hook</c> binary through the task 4.3 plugin envelope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pi extensions default-export <c>function (pi) { … }</c> and load only in a trusted
    /// project. The shim handles the <c>Agent</c> tool from
    /// <c>@tintinweb/pi-subagents</c>: <c>pi.on("tool_call", …)</c> sends the
    /// <c>before</c> envelope and <c>pi.on("tool_result", …)</c> sends the
    /// <c>after</c> envelope, both shaped
    /// <c>{schema, harness, phase, tool, call-id, session, cwd, args, result}</c> under
    /// schema <c>kyber-arbiter.plugin-event/v1</c>, and both read back
    /// <c>{decision, reason, args}</c>.
    /// </para>
    /// <para>
    /// The shim spawns by argv with <c>node:child_process</c> <c>spawnSync</c> and no
    /// shell — <c>pi.exec</c> takes no stdin, so Bun-style process APIs cannot carry the
    /// envelope — writing the envelope on stdin via <c>input</c> with
    /// <c>encoding: "utf8"</c> and <c>timeout</c> from
    /// <see cref="SquadArbiterWiring.HookTimeoutSeconds"/> (spawnSync takes
    /// milliseconds). A <c>block</c> decision before the call returns
    /// <c>{block: true, reason}</c>; a returned <c>args</c> object is assigned field by
    /// field onto <c>event.input</c>. After the call a <c>block</c> returns
    /// <c>{content: [...event.content, {type: "text", text: reason}]}</c>. A spawn error
    /// or a non-zero status blocks, so the call fails closed rather than passing through.
    /// </para>
    /// </remarks>
    private static string RenderArbiterExtension(int timeoutSeconds, SquadDeploymentScope scope)
    {
        long timeoutMs = Math.Clamp((long)timeoutSeconds * 1000, 0, int.MaxValue);
        string scopeSuffix = scope == SquadDeploymentScope.Global ? ", \"--scope\", \"global\"" : string.Empty;
        string commandLine = scope == SquadDeploymentScope.Global
            ? ArbiterHookWiring.HookCommandLine("pi", scope)
            : string.Empty;
        string globalCommandLineStatement = scope == SquadDeploymentScope.Global
            ? $"const commandLine = {System.Text.Json.JsonSerializer.Serialize(commandLine)};\n"
            : string.Empty;

        return $$"""
            import { spawnSync } from "node:child_process";

            // Keys that mutate an object's linkage rather than adding a property to it.
            const POLLUTING_ARG_KEYS = new Set(["__proto__", "constructor", "prototype"]);

            type ArbiterDecision = {
              decision: "allow" | "block";
              reason?: string;
              args?: Record<string, unknown>;
            };

            // JSON.parse accepts null, a bare string, an array and an object with no
            // decision field, and the extension believed whatever came back: a document
            // whose decision is not "block" is read as an allow. Only a plain object whose
            // decision is exactly "allow" or "block" is a decision at all; anything else
            // blocks, because a dispatch the host never approved must not pass.
            function asDecision(parsed: unknown): ArbiterDecision {
              if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
                return { decision: "block", reason: "kyber-arbiter: malformed arbiter decision" };
              }
              const candidate = parsed as { decision?: unknown; reason?: unknown; args?: unknown };
              if (candidate.decision !== "allow" && candidate.decision !== "block") {
                return { decision: "block", reason: "kyber-arbiter: malformed arbiter decision" };
              }
              const decision: ArbiterDecision = { decision: candidate.decision };
              if (typeof candidate.reason === "string") {
                decision.reason = candidate.reason;
              }
              if (typeof candidate.args === "object" && candidate.args !== null && !Array.isArray(candidate.args)) {
                decision.args = candidate.args as Record<string, unknown>;
              }
              return decision;
            }

            {{globalCommandLineStatement}}function runArbiterHook(envelope: unknown): ArbiterDecision {
              try {
                const result = spawnSync("kyber-weave-arbiter", ["hook", "--harness", "pi"{{scopeSuffix}}], {
                  input: JSON.stringify(envelope),
                  encoding: "utf8",
                  timeout: {{timeoutMs}},
                });
                if (result.error) {
                  return { decision: "block", reason: "kyber-arbiter: failed to spawn kyber-weave-arbiter: " + String(result.error) };
                }
                if (result.status !== 0) {
                  return { decision: "block", reason: "kyber-arbiter: hook exited with status " + String(result.status) };
                }
                const out = String(result.stdout ?? "");
                if (out.trim() === "") {
                  return { decision: "allow" };
                }
                return asDecision(JSON.parse(out));
              } catch (err) {
                return { decision: "block", reason: "kyber-arbiter: failed to run arbiter hook: " + String(err) };
              }
            }

            export default function (pi: any) {
              pi.on("tool_call", async (event: any) => {
                if (event.toolName !== "Agent") {
                  return;
                }
                const envelope = {
                  schema: "kyber-arbiter.plugin-event/v1",
                  harness: "pi",
                  phase: "before",
                  tool: event.toolName,
                  "call-id": event.toolCallId,
                  session: event.sessionId ?? event.session ?? "",
                  cwd: process.cwd(),
                  args: event.input,
                };
                const decision = runArbiterHook(envelope);
                if (decision.decision === "block") {
                  const reason = decision.reason ?? "blocked by kyber-arbiter";
                  return { block: true, reason };
                }
                if (decision.args !== undefined) {
                  for (const key of Object.keys(decision.args)) {
                    // These three keys run an inherited setter rather than creating an own
                    // property: assigning to `__proto__` rebinds the target object's
                    // prototype, so a decision could hand the host's tool input an object
                    // it then inherits properties from. Skip them; a decision has no
                    // legitimate reason to name them.
                    if (POLLUTING_ARG_KEYS.has(key)) {
                      continue;
                    }
                    event.input[key] = decision.args[key];
                  }
                }
              });
              pi.on("tool_result", async (event: any) => {
                if (event.toolName !== "Agent") {
                  return;
                }
                const envelope = {
                  schema: "kyber-arbiter.plugin-event/v1",
                  harness: "pi",
                  phase: "after",
                  tool: event.toolName,
                  "call-id": event.toolCallId,
                  session: event.sessionId ?? event.session ?? "",
                  cwd: process.cwd(),
                  args: event.input,
                  result: {
                    content: event.content,
                    isError: event.isError,
                  },
                };
                const decision = runArbiterHook(envelope);
                if (decision.decision === "block") {
                  const reason = decision.reason ?? "blocked by kyber-arbiter";
                  // A call that produced no output carries `content: undefined`, and
                  // spreading that threw before the block could be built.
                  return { content: [...(event.content ?? []), { type: "text", text: reason }] };
                }
              });
            }
            """;
    }

    /// <summary>
    /// Parses and validates a thinking level suffix from a model string. A model value like
    /// <c>zai/glm-5.3[thinking=high]</c> splits into bare model id <c>zai/glm-5.3</c> and level
    /// <c>high</c>. The level is validated against the closed six-value domain
    /// <c>off | minimal | low | medium | high | max</c> with <see cref="StringComparison.Ordinal"/>
    /// (case-sensitive); an invalid level throws <see cref="SquadRenderValidationException"/>.
    /// Returns a tuple of (bareModelId, thinkingLevel) where thinkingLevel is null if no suffix.
    /// </summary>
    private static (string BareModel, string? ThinkingLevel) ParseThinkingSuffix(string? modelValue)
    {
        if (string.IsNullOrEmpty(modelValue))
        {
            return (modelValue ?? string.Empty, null);
        }

        const string suffixPrefix = "[thinking=";
        int suffixStartIdx = modelValue.LastIndexOf(suffixPrefix, StringComparison.Ordinal);
        if (suffixStartIdx < 0)
        {
            // No suffix present
            return (modelValue, null);
        }

        string bareModel = modelValue[..suffixStartIdx];
        int levelStartIdx = suffixStartIdx + suffixPrefix.Length;
        int suffixEndIdx = modelValue.IndexOf(']', levelStartIdx);
        if (suffixEndIdx < 0)
        {
            throw new SquadRenderValidationException(
                $"Malformed thinking suffix in model value '{modelValue}': missing closing ']'.");
        }

        if (suffixEndIdx != modelValue.Length - 1)
        {
            throw new SquadRenderValidationException(
                $"Malformed thinking suffix in model value '{modelValue}': the suffix must be the last element of the value.");
        }

        if (bareModel.Length == 0)
        {
            throw new SquadRenderValidationException(
                $"Malformed model value '{modelValue}': the model id before the thinking suffix is empty.");
        }

        string level = modelValue[levelStartIdx..suffixEndIdx];

        // Validate level against closed domain
        if (!AllowedThinkingLevels.Contains(level))
        {
            throw new SquadRenderValidationException(
                $"Invalid thinking level '{level}' in model value. Allowed values are: off, minimal, low, medium, high, max.");
        }

        return (bareModel, level);
    }

    /// <summary>
    /// The overlay entries that reach <see cref="KyberWeaveMcpServer"/> through
    /// <c>pi-mcp-adapter</c>, in emission order: the proxy tool, then one entry per tool
    /// <paramref name="requiredMcpTools"/> declares for that server, in ordinal order.
    /// </summary>
    /// <remarks>
    /// A roster without the server yields no entries, which is what keeps an unmatched grant
    /// from loading an extension for a server nobody declared. The tool segment is spelled the
    /// way the adapter's deferred surface registers it — see this type's remarks on why the
    /// server name's hyphen becomes an underscore here and nowhere else.
    /// </remarks>
    /// <param name="requiredMcpTools">The toolchain roster, keyed by MCP server name.</param>
    private static IReadOnlyList<string> ResolveKyberWeaveOverlayTools(
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredMcpTools)
    {
        if (!requiredMcpTools.TryGetValue(KyberWeaveMcpServer, out IReadOnlyList<string>? tools))
        {
            return [];
        }

        string namespacePrefix = $"ext:{McpAdapterExtension}/mcp__{McpServerNamespace(KyberWeaveMcpServer)}__";

        return
        [
            McpProxyToolEntry,
            .. tools
                .Order(StringComparer.Ordinal)
                .Select(tool => namespacePrefix + McpToolName(tool))
        ];
    }

    /// <summary>
    /// The namespace the adapter gives a server: hyphens become underscores before any other
    /// character is mapped, which is why <c>kw-docs</c> is reached as <c>kw_docs</c>.
    /// </summary>
    /// <remarks>
    /// Only the hyphen is mapped, because the one server this renderer derives an overlay for is
    /// the only name it is ever handed and a hyphen is the only character that name has outside
    /// <c>[A-Za-z0-9_]</c>. The adapter's own rule also code-point-encodes anything else; a
    /// second derived server with such a character would have to bring that rule with it rather
    /// than assume this one covers it.
    /// </remarks>
    private static string McpServerNamespace(string serverName)
    {
        return serverName.Replace('-', '_');
    }

    /// <summary>
    /// The adapter's tool segment: every character outside <c>[A-Za-z0-9_]</c> becomes
    /// <c>_</c>, which is what spells <c>query-docs</c> as <c>query_docs</c>.
    /// </summary>
    private static string McpToolName(string toolName)
    {
        return string.Concat(toolName.Select(
            character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_'));
    }

    /// <summary>
    /// Appends the declared and derived overlay entries to the capability-derived built-ins,
    /// keeping the first occurrence of each so an agent that already declares an entry the
    /// derivation would add renders it once.
    /// </summary>
    /// <param name="capabilityTools">The built-ins the capability profile grants, in fixed order.</param>
    /// <param name="declaredTools">The agent's own <c>pi-tools</c>, in declared order.</param>
    /// <param name="derivedTools">The renderer-derived entries, in emission order.</param>
    private static IReadOnlyList<string> MergeTools(
        IReadOnlyList<string> capabilityTools,
        IReadOnlyList<string> declaredTools,
        IReadOnlyList<string> derivedTools)
    {
        if (declaredTools.Count == 0 && derivedTools.Count == 0)
        {
            return capabilityTools;
        }

        List<string> merged = [.. capabilityTools];
        HashSet<string> seen = new(capabilityTools, StringComparer.Ordinal);

        foreach (string tool in declaredTools.Concat(derivedTools))
        {
            if (seen.Add(tool))
            {
                merged.Add(tool);
            }
        }

        return merged;
    }

    /// <summary>
    /// Appends the adapter to the declared extension ids when a derived overlay needs it,
    /// leaving an agent that already declares it — or that receives no derived overlay —
    /// exactly as the canonical body declared it.
    /// </summary>
    /// <param name="declaredExtensions">The agent's own <c>pi-extensions</c>, in declared order.</param>
    /// <param name="deriveOverlay">Whether the renderer derived an overlay for this agent.</param>
    private static IReadOnlyList<string> MergeExtensions(
        IReadOnlyList<string> declaredExtensions,
        bool deriveOverlay)
    {
        if (!deriveOverlay || declaredExtensions.Contains(McpAdapterExtension, StringComparer.Ordinal))
        {
            return declaredExtensions;
        }

        return [.. declaredExtensions, McpAdapterExtension];
    }

    private static bool IsDelegateAllowed(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        return capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile) &&
               profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision decision) &&
               decision == SquadPermissionDecision.Allow;
    }

    private static IEnumerable<SquadDegradationRecord> BuildSubagentDegradations(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        // 'deny' needs no record: the rendered tools list withholds it. Only 'ask' loses
        // meaning — Pi has no per-capability permission prompt (R9).
        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                "pi",
                agent.Name,
                agent.Name,
                "safety-narrowed",
                agent.BodyDigest,
                $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                $"{string.Join(", ", narrowed)}. Pi has no per-capability permission prompt, " +
                "and the 'tools' key is binary, so these narrow to withheld: the " +
                "corresponding tools are absent from the agent's 'tools' list.");
        }

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.read", out SquadPermissionDecision readDecision) &&
            readDecision == SquadPermissionDecision.Allow)
        {
            notExpressibleDetails.Add(
                "Capability 'network.read' is allowed but no built-in Pi tool exists to express it " +
                "(bare 'mcp' is not a built-in; web access arrives via the canonical 'pi-extensions' / " +
                "'pi-tools' agent-file overlay, not the renderer).");
        }

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision == SquadPermissionDecision.Allow)
        {
            notExpressibleDetails.Add(
                "Capability 'network.publish' is allowed but no built-in Pi tool exists to express it.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow &&
            agent.DelegatesTo.Count == 0)
        {
            notExpressibleDetails.Add(
                "Capability 'delegate' is allowed but the canonical delegates-to roster is " +
                "empty; expressing 'all' would require 'allowed_subagents' to reach every " +
                "registered agent type, including 'general-purpose', which no empty roster " +
                "authorizes.");
        }

        if (notExpressibleDetails.Count > 0)
        {
            yield return new SquadDegradationRecord(
                "pi",
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
            "pi",
            agent.Name,
            agent.Name,
            agent.BodyDigest,
            executeDecision,
            writeDecision,
            ["bash"],
            ["edit", "write"]);

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
        IReadOnlyList<string> mcpOverlayTools,
        bool reusedCanonicalSkill = false)
    {
        string projectionText = reusedCanonicalSkill
            ? "this agent reuses the same-name canonical skill as its entry point, because the " +
              "canonical skill already occupies that identity; no separate lowered skill is emitted"
            : "this agent renders as a top-level Pi skill instead of a '.pi/agents/' file";

        yield return new SquadDegradationRecord(
            "pi",
            agent.Name,
            agent.Name,
            "role-skill-fallback",
            agent.BodyDigest,
            "Pi core has no primary-agent selection primitive: '.pi/SYSTEM.md' and " +
            "'--system-prompt' replace the whole session prompt, not a per-agent one. " +
            $"Fallback profile '{agent.Fallback}' declares no-primary-agent: " +
            $"{fallbackProfile.NoPrimaryAgent}, so " + projectionText + ".");

        string rosterText = agent.DelegatesTo.Count > 0
            ? string.Join(", ", agent.DelegatesTo)
            : "(none declared)";

        // An entitled primary agent loses the derived overlay on this target, and a loss the
        // receipt does not name is indistinguishable from a grant that was never decided. Only
        // the entitled case is stated, so the record never claims a grant the profile lacks.
        bool losesMcpOverlay = mcpOverlayTools.Count > 0 &&
            CapabilityDegradations.GrantsUniversalMcpServers(agent.CapabilityProfile, capabilityProfiles);

        yield return new SquadDegradationRecord(
            "pi",
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            "Capability decisions " +
            $"({CapabilityDegradations.DescribeCapabilityDecisions(agent, capabilityProfiles, capabilityVocabulary)}) " +
            "are not enforced: a top-level Pi skill runs under the harness default tool " +
            "set, not the canonical capability lattice, and its delegates-to roster " +
            $"({rosterText}) is instruction-only, not a runtime-enforced " +
            "'allowed_subagents' list." +
            (losesMcpOverlay
                ? " The '" + KyberWeaveMcpServer + "' MCP overlay a rendered agent file carries " +
                  "cannot be expressed here: a Pi skill declares no 'tools' or 'extensions', so " +
                  "this entry point reaches that server only if the session it runs in already " +
                  "has it configured."
                : string.Empty));
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="PiExtensionsFlowSequenceConverter"/>.
    /// </summary>
    private sealed class PiExtensionsFlowSequence(IEnumerable<string> extensions) : List<string>(extensions);

    /// <summary>
    /// Serializes the Pi agent <c>extensions</c> list as an inline YAML flow sequence
    /// (e.g. <c>extensions: [pi-mcp-adapter]</c>, the exact form live-verified 2026-10-24).
    /// Mirrors <c>ClaudeRenderer.ClaudeToolsFlowSequenceConverter</c>.
    /// </summary>
    private sealed class PiExtensionsFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            return type == typeof(PiExtensionsFlowSequence);
        }

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            throw new NotSupportedException("Deserialization of PiExtensionsFlowSequence is not supported.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not PiExtensionsFlowSequence extensions)
            {
                return;
            }

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
            foreach (string extension in extensions)
            {
                emitter.Emit(new Scalar(
                    AnchorName.Empty,
                    TagName.Empty,
                    extension,
                    ScalarStyle.Plain,
                    true,
                    true));
            }

            emitter.Emit(new SequenceEnd());
        }
    }
}

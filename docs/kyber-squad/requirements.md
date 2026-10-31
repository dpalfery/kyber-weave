---
id: squad/requirements
title: Kyber-Squad requirements and degradation contract
doc-type: requirements
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-31
status: current
decided-by:
  - adr/0019-pi-native-subagents-and-primary-lowering
  - adr/0022-antigravity-native-agents
  - adr/0028-devin-target-scoped-authoring-capability-profiles
  - adr/0037-squad-state-in-user-home
---

# Kyber-Squad requirements and degradation contract

This document defines the formal requirement specifications (**KS-001** through **KS-008**) and the structured degradation contract governing Kyber-Squad.

---

## Requirements Specifications

| ID | Requirement Specification |
|---|---|
| **KS-001** | **Canonical Source Governance**: Maintain exactly 24 canonical agent instruction bodies and 28 canonical skill identities under `products/kyber-squad/`. The skill tree retains 88 supplemental resources, for 116 files total, and agents own 10 progressive-disclosure references; every owner's local references form a validated resource closure, and every retained resource carries a reviewed disposition in the [skill-resource dispositions audit](skill-resource-dispositions.md). Generated role-skill projections and target-rendered `.github` trees do not alter the canonical product inventory. |
| **KS-002** | **Deterministic Resolution & Permission Lattice**: Resolve canonical identity, invocation mode, model profiles, capabilities, permissions, delegation hierarchies, fallbacks, aliases, and instruction body digests deterministically. Permission translation adheres to the lattice `deny < ask < allow`. Unsupported `ask` permissions narrow to `deny`, and unenforceable `ask` or `deny` constraints cause representation omission rather than permission broadening. A target-scoped internal capability profile (`copilot-capability-profile`, `devin-capability-profile`) may validate exact target tool membership but must not replace or widen the shared capability profile or metadata; a profile marked `target: <name>` is rejected when assigned as an agent's shared `capability-profile`; a `<target>-capability-profile` naming a profile without that marker is rejected; and a primary agent naming `devin-capability-profile` is rejected because Devin lowers primaries to skills with no tool allow-list ([ADR 0028](../adr/0028-devin-target-scoped-authoring-capability-profiles.md)). |
| **KS-003** | **Deterministic Target Resolution**: For install, resolve deployment targets from explicit CLI flags, saved repository configuration, then strong filesystem markers. For update, a non-empty explicit `--target` list is the complete desired deployment target set and takes precedence over the existing receipt; when omitted, reuse the receipt roster. Update never re-detects filesystem markers. Uninstall uses the existing receipt roster. The `all` keyword expands strictly to the approved 13-target roster (`codex`, `cursor`, `claude`, `copilot`, `opencode`, `kilo`, `antigravity`, `warp`, `factory`, `pi`, `zcode`, `devin`, `commandcode`). |
| **KS-004** | **Transactional Lifecycle & State Governance**: Execute install, update, and uninstall operations via an isolated render plan with preflight validation, exact-match adoption (`--adopt`), managed-edit preservation, exclusive cross-process mutex leasing (`kyber-weave-squad-<root-key>`), leaf-level no-overwrite claim/publish execution, compare-and-restore rollback, and lock/receipt state applied last. Lock and receipt state is kept under the user home (`~/.kyber-weave/squad/`), never in the repository ([ADR 0037](../adr/0037-squad-state-in-user-home.md)). |
| **KS-005** | **Version Lockstep**: Enforce exact version equality across the CLI, Squad release asset, and MCP server. Verify all release assets against published SHA-256 checksums without installing external dependencies as side effects. |
| **KS-006** | **Dual Distribution Packaging**: Provide `squad pack` to build an APM distribution zip containing all agents with their owned resources, all skills with their resources, and MCP configurations, plus an adjunct Agent Plugins v1 artifact exposing the complete recursive portable skill tree and MCP surfaces only — never agents or agent-owned resources. Every rendered role embeds its canonical instruction digest. |
| **KS-007** | **Release Pipeline Publishing**: Publish versioned `kyber-squad-X.Y.Z.zip` and `kyber-squad-plugin-X.Y.Z.zip` artifacts in GitHub Releases, validated against the pinned APM release. |
| **KS-008** | **Documentation & Plan Closeout**: Maintain canonical architecture, onboarding, requirements, configuration, and distribution documentation, keeping the governed corpus at zero validation findings. Every plan and specification declares its documentation scale at authoring time (`documentation-impact` with `impact-components` plus a `Design harvest` table keyed by target component and doc type, owner-confirmed at approve-and-execute); every completed change receives a `docs-dev` documentation-impact review (`DOCS_REVIEW` rows, run record, outcome carried in the PR description); and plan/spec-backed work completes closeout — durable decisions and the declared `Design harvest` migrated into canonical documentation with a closeout mapping (`MAPPED:` / `IMPACT:` / `DOCS_REVIEW:`), artifact marked `Status: Archived` — before the objective is reported complete or delivered. |

---

## Conductor execution circuit-breaker

This is product behavior of the deployed `conductor`, `csharp-dev`, `test-dev`, and
`github-devops` agents, not a new `KS-*` id. `KS-001` through `KS-008` stay as numbered.
Mechanics live in [architecture §9](architecture.md#9-conductor-execution-circuit-breaker).

| Decision | Requirement |
|---|---|
| **Q1 — Two-level caps** | A worker may make at most 3 incremental test-fix-verify attempts against the same failing fixture or cluster in one implementation or rework dispatch. The conductor may dispatch rework at most 2 times for that cluster in the run, including follow-ups to a retained worker. Cluster identity is the first-observed test ID recorded on the execution artifact. The dispatch tally increments only on rework dispatch and never resets on resumption, replacement, or compaction. |
| **Q2 — Oscillation** | A first one-way regression (A causes B to fail) consumes one worker iteration. `THRASH_OSCILLATION_DETECTED` trips only on A→B→A or a repeating failure signature. |
| **Q3 — Invariant contradiction** | Workers must not satisfy contradictory invariants. A fixture that asserts obsolete details conflicting with the approved design trips `INVARIANT_CONTRADICTION`. |
| **Q4 — JEV checkpoints** | Before and after each fix, workers check blast radius, oscillation, invariant consistency, and the 3-iteration cap. The closed trigger set is `ITERATION_CAP_EXCEEDED`, `THRASH_OSCILLATION_DETECTED`, `INVARIANT_CONTRADICTION`, `BLAST_RADIUS_EXCEEDED`. |
| **Q5 — Escalation** | A trip emits `STATUS: ESCALATION`. The conductor records `ESCALATION: circuit-breaker`, halts rework for that task, and leaves non-dependent work running. In supervised mode the run cannot complete while that finding is unresolved, and the breaker escalates to the user. In autonomous mode it is not a run stop: an unresolved breaker is listed in the draft pull request and the final report as an open finding, delivery continues, and the pull request is ready only if required CI passes ([ADR 0045](../adr/0045-conductor-operator-channel-opt-in.md)). `architect` mediates at queue drain. |

No ADR: the decisions constrain instruction bodies, not the render or transaction engine.

Implementation ownership remains with the original task session through task-gate rework
and verification wherever the harness supports continuation. There is no reviewer session;
a failing or missing `TASK_GATE:` line returns to the original worker.
Self-contained packets and recovery briefs remain mandatory for unsupported or lost
sessions and for context that has become unreliable. The conductor records actual
session handles or the reason they are unavailable; unrelated tasks do not share a
task session. See [task session continuity](architecture.md#task-session-continuity).

---

## Conductor execution and worker invariants

This is product behavior of the deployed `conductor`, `csharp-dev`, and `test-dev` agents,
not a new `KS-*` id. `KS-001` through `KS-008` stay as numbered. Mechanics live in
[architecture §9](architecture.md#9-conductor-execution-circuit-breaker) and
[onboarding](onboarding.md#task-execution-runbook-inlined-packets-fast-testing-and-completion-digests).

| Invariant | Requirement |
|---|---|
| **Build-once, test-many iteration** | Workers build the test project once before iteration (`dotnet build KyberWeave.sln -c Release`) and use `--no-build` or direct assembly runner invocations (`dotnet <dll> -class ... -reporter quiet`) during inner-loop RED/GREEN cycles. Running the full test suite during inner iteration is forbidden; full-suite execution is reserved for task exit gates (`task-gate`) and final council review. |
| **Zero-stash RED proofs** | Capturing honest RED proof evidence must never use `git stash` or `git stash pop` on dirty working trees. RED proofs are captured by running newly authored tests against a clean base checkout, a throwaway working tree copy, or an isolated `git worktree` before production implementation begins. |
| **Strict 6-field completion digest** | Subagents conclude dispatches with the standardized 6-field digest schema (`STATUS`, `FILES`, `TESTS`, `COMMIT`, `REMOTE_HEAD`, `TASK_GATE`). Conductor enforces this exact schema and rejects digests when any required field is missing, when `STATUS: FAIL` is reported, or when `TASK_GATE:` is missing or `FAIL`. The only exemption is a valid escalation (`STATUS: ESCALATION` with a valid `CIRCUIT_BREAKER_TRIGGER`), defined in [the rule below this table](#conductor-execution-and-worker-invariants). |
| **Adversarial RED/GREEN role separation** | The two-agent adversarial boundary between `test-dev` (RED) and the implementation specialist (GREEN) is strictly preserved; collapsing RED and GREEN into a single worker dispatch is forbidden. Small adjacent task bundling is permitted strictly for same-role micro-tasks, and an independent verification pass (`task-gate` deterministic exit check and final council review) is mandatory on the integrated working tree. |

A valid escalation is `STATUS: ESCALATION` with exactly one `CIRCUIT_BREAKER_TRIGGER:` token from the closed set (`ITERATION_CAP_EXCEEDED`, `THRASH_OSCILLATION_DETECTED`, `INVARIANT_CONTRADICTION`, `BLAST_RADIUS_EXCEEDED`) and its payload lines (`FAILURE_CLUSTER`, `CONTRADICTORY_INVARIANTS`, `BLAST_RADIUS`, `RECOMMENDED_ACTION`); only a valid escalation is exempt from the `FILES`, `TESTS`, `COMMIT`, `REMOTE_HEAD` and `TASK_GATE` rules. A `STATUS: ESCALATION` without a valid `CIRCUIT_BREAKER_TRIGGER` is not an escalation: it is checked and rejected like any completion digest. A valid escalation wins the routing (halt rework, record `ESCALATION: circuit-breaker`, architect mediation) but never completes the task or clears its exit gate; a `TASK_GATE: FAIL` it carries is recorded in the finding with its gate ids, and the task stays open until a later dispatch returns a fresh passing `TASK_GATE:` line against the current tree.
When a worker returns a valid escalation, the conductor accepts the escalation payload without rejecting it for missing completion fields or missing `TASK_GATE` and routes it directly to architect mediation without rejection.

---

## User-facing response format

This is product behavior of the deployed `conductor` and `bug-crusher` agents, not a new `KS-*`
id. `KS-001` through `KS-008` stay as numbered. The response shape is owned by the shipped default
`products/kyber-squad/response-guidance.default.md`; `squad install` and `squad update` copy the
effective text of the user's home file into a `<response-preferences>` block in each deployed
entry file of `conductor` and `bug-crusher`. No agent loads a skill for it. This section states the
requirement those entry files implement. Mechanics live in
[response preferences](architecture.md#response-preferences-injected-at-deploy).

| Decision | Requirement |
|---|---|
| **R1 — One shape, in order** | Every user-facing response from a primary conversational squad agent states, in this order: a title line naming the work; a bottom line of at most 3 sentences, written for an experienced reader with no prior involvement in the work and assuming no context; and the open decisions whenever a decision is open, one decision where possible, with a recommendation attached. The order is part of the requirement, not a preference. |
| **R2 — The format is never named** | A response never labels, names, mentions, or refers to the format it follows: no tags, no structural headers, no meta-commentary about how the answer is arranged. Bullets carry issues, options, and status, and a status statement uses the same shape as a question. |
| **R3 — Who is bound and when** | The primary conversational agents `conductor` and `bug-crusher` carry the effective response preferences in their own deployed entry file, inside a `<response-preferences>` block that `squad install` and `squad update` fill at deploy time (and `squad pack` fills with the shipped default). The agent reads the block as part of its instructions on every turn; no skill is loaded and no load step precedes a reply. `product-owner` is exempt: it is headless, and its output is a persisted artifact and a structured digest for the conductor rather than a conversational reply. |
| **R4 — Digests stay machine-consumed** | The conductor relays every specialist digest it surfaces to the human in the response-format shape, and the digest schemas themselves are unchanged. The format governs the presentation of a digest, never the fields it carries: every status marker, id, path, count, gap, open question, and evidence value reaches the human exactly as the specialist returned it, and nothing is dropped, merged, or paraphrased. |
| **R5 — One edit point** | The user-home file `~/.kyber-weave/response-guidance.md` is the single place the operator edits response preferences. A global `squad install` creates it with the shipped default when it is absent (never in a dry run or project scope). `squad update` never overwrites it once it has been edited; it warns "Kept the edited response guidance". When the file is unreadable or blank, the shipped default text is used. The text is pasted into the entry files verbatim: there is no size cap and no style-only guard line. That is an accepted risk, not an oversight: the operator owns the text, and it can carry any instruction the operator writes. |

The skill is bound by name, not by a link. The body resource-closure rule governs local path
links — a link resolves only to a file inside its owner's own directory — while a skill invoked
by name is reached by name, so one skill can carry the standard for every agent.

---

## Intake assessment

This is product behavior of the deployed `conductor` agent, not a new `KS-*` id. `KS-001` through
`KS-008` stay as numbered.

The intake assessment is the routing question the conductor asks when the user has not chosen an
artifact kind. It is default-off and opt-in per repository.

- **Default-off**: absent or false in `.kyber-weave/kyber-weave.yml`, the assessment is skipped
  and the path continues to the plan choice. A repository that never set the flag keeps the
  behavior it always had.
- **Opt-in through the Config Reg**: `ontology: intake: true` publishes the
  `<intake-assessment>` mode property into the repository root `AGENTS.md`, with the value
  `enabled`. The conductor resolves that property: present → run the assessment for a request
  whose intent is unstated; absent → skip the assessment and take the plan path directly.
- **Routing is never silent**: when the assessment is off and the conductor takes the plan path,
  it says so in one line and tells the user they can ask for a specification instead.
- **Defect-first precedence is unchanged**: a concrete failure that has not been diagnosed
  outranks the artifact-choice question, and the assessment never runs in place of a diagnosis.

The published property is a mode entry, not a path entry. `docs validate` does not resolve its
value on disk (`KW-CONFIG-REG-001` is exempt for it) but still checks that the rendered block
matches configuration (`KW-CONFIG-REG-002`). See
[mode properties](../docgraph/architecture.md#mode-properties-are-not-paths) and
[onboarding](onboarding.md#intake-assessment-opt-in-per-repository).

---

## Degradation Contract

Harnesses differ in their native capabilities (e.g. support for primary agents, subagent spawning, interactive confirmation prompts, and tool filtering). When a target harness cannot natively execute a canonical capability, Kyber-Squad degrades safely according to explicit rules.

The product currently has eleven agent/skill intersections. Two — `product-owner` and `bug-crusher`
— are **primary shared identities**: root agents whose body is a thin wrapper that loads the
same-name canonical skill, which carries the role ([ADR 0036](../adr/0036-primary-agent-canonical-skill-entry-point.md)).
Their canonical skill is the entry point and is emitted on every target; a target that renders a
primary through an additional entry-point form (Claude, Factory) does not duplicate it, and a target
that lowers a primary (Cursor, Pi, ZCode, Devin, Warp) reuses it. The remaining nine are distinct-body
collisions: fallback targets (Warp) preserve each of those skills and emit the matching agent as
`role-<name>`. `conductor` has an unoccupied skill identity and on Warp lowers to a same-name
role skill.

### Degradation Taxonomy

Every non-native translation emits a structured degradation record in `squad.receipt.json` using one of these codes:

| Code | Meaning | Example |
|---|---|---|
| `role-skill-fallback` | An agent role was projected to a role-skill or an alternative entry point because the target lacks a native agent primitive or primary agent role, or renders a primary agent through an additional entry-point form. | `architect` lowered to skill `architect` on Warp (role-skill fallback); `conductor` lowered to a skill on Pi (native skill entry point); `conductor` rendered as both a subagent and an entry-point skill on Claude (native dual entry point); `conductor` lowered to a slash command on ZCode (command entry point); `conductor` lowered to a skill on Devin (native skill entry point); `conductor` rendered as both a droid and an entry-point skill on Factory (no session-parent selector, [ADR 0034](../adr/0034-factory-droid-entry-point-skill.md)); `product-owner` and `bug-crusher` reuse their canonical skill as the entry point on Claude, Factory, Cursor, Pi, ZCode, Devin, and Warp ([ADR 0036](../adr/0036-primary-agent-canonical-skill-entry-point.md)). |
| `safety-narrowed` | An interactive confirmation requirement (`ask`) was narrowed to `deny` because the target cannot prompt the user. | A capability requiring `ask` narrowed to `deny` on non-interactive harnesses. |
| `omitted` | An agent or skill was omitted because a required security or execution constraint cannot be enforced by the target. | A role with unenforceable `deny` constraints omitted to prevent unauthorized execution. |
| `workspace-binding-required` | An MCP server configuration in an Agent Plugins package requires host-specific repository path bindings. | Client loads portable skills but requires manual MCP workspace binding. |
| `resource-links-rewritten` | An agent's or skill's resources were projected under a different directory and authored links were rewritten to preserve resolution. | ZCode projects an agent's resource closure under `.zcode/skills/<owner>/` instead of beside the agent file because `.zcode/agents/` and `.zcode/commands/` are scanned recursively, so resources beside a principal would register as phantom agents or commands. |
| `permission-not-expressible` | A non-deny capability decision cannot be expressed in the target's native permission model without inventing an unverified mapping. | Factory records this for non-deny `network.publish` and `delegate` (no documented tool / `Task` withheld from subagents) and for `mcpServers: []` so parent MCP is not inherited. Claude records this for the unenforced entry-point skill and the nested-roster limitation. |
| `operator-channel-not-rendered` | The operator channel was enabled (`KYBER_SQUAD_OPERATOR_CHANNEL=true`) for a target or scope that cannot carry the conductor overlay, so that target's conductor is unchanged. Only Copilot CLI at global scope carries it ([ADR 0045](../adr/0045-conductor-operator-channel-opt-in.md)). | Claude project scope records this on the conductor when the gate is on. |
| `operator-channel-server-name-missing` | Warning, Copilot CLI global scope only. The operator channel gate is on (`KYBER_SQUAD_OPERATOR_CHANNEL=true`) but `KYBER_SQUAD_OPERATOR_CHANNEL_SERVER` is unset or empty, so the conductor renders without the channel ([ADR 0045](../adr/0045-conductor-operator-channel-opt-in.md)). Other targets and scopes get only `operator-channel-not-rendered`. | Install prints the warning and the conductor is unchanged. |
| `operator-channel-server-name-invalid` | Warning, Copilot CLI global scope only. `KYBER_SQUAD_OPERATOR_CHANNEL_SERVER` contains a character outside `[A-Za-z0-9._-]` or is longer than 64 characters. The conductor renders without the channel and the raw value is never written into a rendered file ([ADR 0045](../adr/0045-conductor-operator-channel-opt-in.md)). | Install prints the warning, with the name rejected and not written. |
| `operator-channel-server-absent` | Warning, Copilot CLI global scope only. The gate is on and the server name is valid, but `$COPILOT_HOME/mcp-config.json` does not declare that name under `mcpServers`. The conductor renders without the channel. | Install prints the warning and the conductor is unchanged. |
| `capability-not-isolable` | A capability grant encompasses an unenforceable security boundary: when `process.execute: allow` and `filesystem.write` is `ask` or `deny` on a target whose shell tool can write files through redirection, the withheld write-tool names are unreachable but the underlying write capability remains accessible. The degradation record's Details names the target, the granted shell tool(s), and the withheld write-tool names. See [plan D11](../archive/plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md#3-approved-decisions-owner-dpalfery-2026-09-21) for complete tool mappings. | Claude: `Bash`/`PowerShell` vs. `Edit`/`Write`/`NotebookEdit`; Pi: `bash` vs. `edit`/`write`; ZCode: `Bash` vs. `Edit`/`Write`; Devin: `exec` vs. `edit`/`write`/`apply_patch`/`notebook_edit`; Factory: `Execute` vs. `Create`/`Edit`/`ApplyPatch`; OpenCode: `bash` vs. `edit`; Antigravity: `run_command` vs. `write_to_file`/`replace_file_content`/`multi_replace_file_content`. On each target, the withheld write tools do not appear in the rendered agent's tool allow-list (the `tools:` frontmatter key, or `allowed-tools:` on Devin), while the degradation record's Details identifies both the granted shell tool(s) and the withheld write-tool names. |

---

## Target Capability and Degradation Matrix

| Target Harness | Target Projection | Renderer Coverage | Subagent Delegation | Lowering Policy | Permission Model |
|---|---|---|---|---|---|
| **Codex** | Native Markdown | Implemented and registered | Supported | Not lowered | Native execution |
| **Cursor** | Native `.cursor/agents` | Implemented and registered | Supported | Not lowered | Native execution |
| **Claude** | Native `.claude/agents` + an entry-point skill per primary agent at `.claude/skills/<name>/SKILL.md` | Implemented and registered | Supported; roster enforced only under `claude --agent` | Not lowered; primary agents additionally exposed as `/<name>` (`role-skill-fallback`). `product-owner` and `bug-crusher` reuse their canonical skill as the entry point instead of a derived one | Explicit tools allow-list; `safety-narrowed` on ask; `capability-not-isolable` for shell-implies-write; `permission-not-expressible` for `network.publish`, the nested roster (ignored in nested subagents), the unenforced entry-point skill (runs under session permissions, can auto-load from its description), and the MCP withholding which applies to subagent only |
| **GitHub Copilot** | Native instructions/agents | Implemented and registered | Supported | Not lowered | Native execution |
| **OpenCode** | Native `.opencode/agents` | Implemented and registered | Supported | Not lowered | Native execution; every documented permission key is pinned explicitly, because an omitted key inherits OpenCode's default-allow rather than being withheld |
| **Kilo** | Native `.kilo/agents` | Implemented and registered | Supported | Not lowered | Native execution |
| **Antigravity** | Native `.agents/agents` (directory per agent) | Implemented and registered | Supported (`enable_subagent_tools: true`, `invoke_subagent`) | Not lowered (native conductor with `mainAgent: true`) | Native execution via tool allowlist (`tools:`) and execution switches (`enable_write_tools`); safety-narrowed; capability-not-isolable for shell-implies-write; permission-not-expressible for unenforceable delegates-to roster |
| **Pi** | Native `.pi/agents` + lowered primary agents | Implemented and registered | Supported (via `@tintinweb/pi-subagents` extension) | Lowered (primary agents; the two root wrappers reuse their canonical skill) | Native execution + safety-narrowed; seven-tool vocabulary `read, grep, find, ls, edit, write, bash` — `network.read` has no expressible built-in Pi tool (bare `mcp` in `tools:` is a loader `tools-error`) and still records `permission-not-expressible`; read-only MCP access arrives via the canonical per-target overlay (`pi-extensions`/`pi-tools`) on `research-agent`, which renders `extensions: [pi-mcp-adapter]` plus the verified `ext:` tool entries; `network.publish` still has no built-in tool and still records `permission-not-expressible`, as does the lowered primary |
| **Warp** | Role skills | Implemented and registered | Single-agent context | Lowered (`role-*` on collision) | Harness default; permission-not-expressible for non-deny decisions |
| **Factory Droids** | Native `.factory/droids` + an entry-point skill per primary agent at `.factory/skills/<name>/SKILL.md` ([ADR 0034](../adr/0034-factory-droid-entry-point-skill.md)) | Implemented and registered | Supported | Not lowered; primary agents additionally exposed as an entry-point skill (`role-skill-fallback`). `product-owner` and `bug-crusher` reuse their canonical skill | Explicit tools array (allow-only documented IDs); safety-narrowed on ask; permission-not-expressible for unmapped `network.publish`/`delegate`, `mcpServers: []`, and the unenforced entry-point skill (runs under session permissions: the droid's tools array and `mcpServers: []` do not apply to it) |
| **ZCode** | Native `.zcode/agents` + primary agents lowered to `.zcode/commands` | Implemented and registered | Supported (project and global scope) | Lowered (primary agents to a slash command; the two root wrappers reuse their canonical skill) | Native execution + safety-narrowed; always a non-empty tool list because `tools: []` is ZCode's inherit-everything signal; permission-not-expressible for `network.publish`, the unenforceable `delegate` roster, and MCP withheld from the pure orchestrator. MCP is granted to every other agent by enumerated tool name from `toolchain.yml`, and `squad doctor` fails a ZCode install that does not declare those servers |
| **Devin** | Native `.devin/agents` (directory per agent) + primary agents lowered to `.devin/skills` | Implemented and registered | Main session only: subagents cannot delegate, because Devin has no enforceable roster (project and global scope) | Lowered (primary agents to a skill; the two root wrappers reuse their canonical skill) | Explicit `allowed-tools` allow-list, never empty; safety-narrowed on ask, except where an agent names a `devin-capability-profile` that grants the capability outright ([ADR 0028](../adr/0028-devin-target-scoped-authoring-capability-profiles.md): `architect`; `product-owner` held the same envelope while it was a subagent, but it is now a primary shared identity that Devin lowers to its canonical skill, so the envelope no longer applies to it and the initialise-before-edit sentence lives in the skill's `spec-authoring` reference instead; the conductor pre-creates the file only as fallback); capability-not-isolable for shell-implies-write; permission-not-expressible for `network.publish`, the withheld `delegate` roster, and the lowered conductor's unenforced decisions. MCP is granted by enumerated tool name from `toolchain.yml`. Skills carry no `allowed-tools` or `permissions`: on a Devin skill `allowed-tools` and `permissions.allow` pre-approve, and whether `permissions.deny` reaches the subagents a skill dispatches is undocumented. The lowered primary agents carry `triggers: [user]`, since Devin Cloud sees skills but loads no custom subagents |
| **Command Code** | Native `.commandcode/agents` + an entry-point skill per primary agent at `.commandcode/skills/<name>/SKILL.md`; the global root is `~/.commandcode` | Implemented and registered | Not supported: a subagent never receives the `agent` or `agent_output` tools, so `delegate` is not expressible | Not lowered; primary agents additionally exposed as an entry-point skill (`role-skill-fallback`), invoked as `/<name>`. The two root wrappers reuse their canonical skill | Explicit `tools` allow-list of snake_case tool names, always written as a flow sequence so an empty grant is the visible `tools: []` (an omitted `tools` already grants nothing); only `allow` grants, `ask` narrows to deny and records `safety-narrowed`; `capability-not-isolable` for shell-implies-write; `permission-not-expressible` for `network.publish` (no built-in tool), `delegate`, parent MCP (MCP tools reach a subagent only when named in `tools`, and Squad names none), and the unenforced entry-point skill (runs under the session's permissions: the agent's `tools` allow-list does not apply to it) |

The thirteen rows are the declared target roster. All thirteen targets (`copilot`, `cursor`, `claude`, `codex`,
`antigravity`, `opencode`, `kilo`, `pi`, `factory`, `warp`, `zcode`, `devin`, and `commandcode`) have implemented and registered renderers.

---

## Non-Broadening Guarantee

Kyber-Squad enforces a strict non-broadening guarantee across all translations:

> **No capability permission may be escalated from `deny` or `ask` to `allow` during target rendering or role-skill lowering.**

If a target harness cannot guarantee the containment or authorization boundaries specified in an agent's capability profile, the engine will narrow the permission or omit the component entirely.

One explicit, opt-in exception: the operator channel overlay ([ADR 0045](../adr/0045-conductor-operator-channel-opt-in.md)) grants the conductor one MCP tool entry, `<name>/*`, for the server the operator names. It renders only on Copilot CLI at global scope, only when the gate is on and the server is declared in that CLI's MCP config. Every other render keeps the conductor without MCP, and the overlay escalates nothing else.

## Render and knowledge-retention requirement

`code-review-loop` and `kilo-review-loop` are authored in Kyber-Squad. Every imported
canonical raw `SKILL.md` except the five explicitly evolved skills (`bug-crusher`, `code-review`,
`product-owner`, `create-pull-request`, and `pr-review-fix-comments`) matches the
canonical bytes, as does every non-evolved agent body. The retired `create-pull-request-github`
skill is folded into `create-pull-request`, and the three retired `-v3` agent identities survive
only as folded provenance in their canonical migration reports. Renderers project every file an
owner's Markdown links reach beside its principal output, so a fresh Copilot render emits 149
files with no dangling link; every skill resource reaches this render except
`skills/setup-dev-environment/agents/openai.yaml`, which stays packaged-only Codex skill-UI
metadata. The tracked root `.github/`
self-deployment predates resource delivery and is refreshed only by a release. Both recursive
package formats retain all supplemental resources. Every retained resource has a reviewed
disposition in the [skill-resource dispositions audit](skill-resource-dispositions.md):
non-policy content stays in its skill directory as its durable home, portable policy lives in the
`products/kyber-squad/standards/` templates, and nothing was deleted.

`products/kyber-squad/` is canonical and package authority. The repository root
`.github/agents/` and `.github/skills/` are an intentional stale self-deployment outside this
synchronization. The repository's own `.kyber-weave/squad.lock.yml` and
`.kyber-weave/squad.receipt.json` are legacy in-repo state from before
[ADR 0037](../adr/0037-squad-state-in-user-home.md): Squad no longer writes lock or receipt files
into the repository, and these two files are retained for rollback. They are not current state. All four
paths remain untouched until a human refreshes them after a fresh release candidate.

---

## Related

- [Kyber-Squad architecture](architecture.md) — technical design, transaction engine, and conductor execution circuit-breaker
- [Kyber-Squad onboarding guide](onboarding.md) — command usage and lifecycle workflows
- [The documentation ontology](../documentation-ontology.md) — documentation standards

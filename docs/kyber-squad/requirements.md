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
  - adr/0050-universal-kyber-weave-mcp-grant
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
| **KS-006** | **Dual Distribution Packaging**: Provide `squad pack` to build an APM distribution zip containing all agents with their owned resources, all skills with their resources, and MCP configurations, plus an adjunct Agent Plugins v1 artifact exposing the complete recursive portable skill tree and MCP surfaces only — never agents or agent-owned resources. The skill tree is the 28 canonical Squad skills plus every skill in `.apm/skills/` (the six Kyber-Weave skills), so each archive carries 34 skills. A companion skill whose name clashes with a Squad skill or agent, or that is a symbolic link, fails the pack closed and writes no archive. Every rendered role embeds its canonical instruction digest. |
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
| **Build-once, test-many iteration** | Workers build the test project once before iteration (`dotnet build KyberWeave.sln -c Release`) and use `--no-build` or direct assembly runner invocations (`dotnet <dll> -class ... -reporter quiet`) during inner-loop RED/GREEN cycles. Running the full test suite during inner iteration is forbidden; full-suite execution is reserved for the final council review, which runs it once on the integrated head. A worker's task end runs only the test classes its task touched. |
| **Zero-stash RED proofs** | Capturing honest RED proof evidence must never use `git stash` or `git stash pop` on dirty working trees. RED proofs are captured by running newly authored tests against a clean base checkout, a throwaway working tree copy, or an isolated `git worktree` before production implementation begins. |
| **Strict 6-field completion digest** | Subagents conclude dispatches with the standardized 6-field digest schema (`STATUS`, `FILES`, `TESTS`, `COMMIT`, `REMOTE_HEAD`, `TASK_GATE`). Conductor enforces this exact schema and rejects digests when any required field is missing, when `STATUS: FAIL` is reported, or when `TASK_GATE:` is missing or `FAIL`. The only exemption is a valid escalation (`STATUS: ESCALATION` with a valid `CIRCUIT_BREAKER_TRIGGER`), defined in [the rule below this table](#conductor-execution-and-worker-invariants). |
| **Adversarial RED/GREEN role separation** | The two-agent adversarial boundary between `test-dev` (RED) and the implementation specialist (GREEN) is strictly preserved; collapsing RED and GREEN into a single worker dispatch is forbidden. Small adjacent task bundling is permitted strictly for same-role micro-tasks, and an independent verification pass (`task-gate` deterministic exit check and final council review) is mandatory on the integrated working tree. |

A valid escalation is `STATUS: ESCALATION` with exactly one `CIRCUIT_BREAKER_TRIGGER:` token from the closed set (`ITERATION_CAP_EXCEEDED`, `THRASH_OSCILLATION_DETECTED`, `INVARIANT_CONTRADICTION`, `BLAST_RADIUS_EXCEEDED`) and its required payload lines (`FAILURE_CLUSTER` and `RECOMMENDED_ACTION` always, plus `CONTRADICTORY_INVARIANTS` for `INVARIANT_CONTRADICTION` and `BLAST_RADIUS` for `BLAST_RADIUS_EXCEEDED`); only a valid escalation is exempt from the `FILES`, `TESTS`, `COMMIT`, `REMOTE_HEAD` and `TASK_GATE` rules. A `STATUS: ESCALATION` without a valid `CIRCUIT_BREAKER_TRIGGER` is not an escalation: it is checked and rejected like any completion digest. A valid escalation wins the routing (halt rework, record `ESCALATION: circuit-breaker`, architect mediation) but never completes the task or clears its exit gate; a `TASK_GATE: FAIL` it carries is recorded in the finding with its gate ids, and the task stays open until the conductor, once architect mediation has resolved the cluster, dispatches a follow-up worker that returns a fresh passing `TASK_GATE:` line against the current tree; the halt bars dispatch only until that mediation completes.
When a worker returns a valid escalation, the conductor accepts the escalation payload without rejecting it for missing completion fields or missing `TASK_GATE` and routes it directly to architect mediation without rejection.

During the phased rollout only `csharp-dev` and `test-dev` must return the full six-field block. Every other developer worker may return the standard completion report its own agent body specifies, plus a `TASK_GATE:` line; the conductor's requirement for that line overrides the agent body, and the `FILES`, `TESTS`, `COMMIT` and `REMOTE_HEAD` fields are not required of it. The rejection of a missing or `FAIL` `TASK_GATE:` applies to every developer worker. Lines of a worker's explanatory message body that are not digest fields are never counted or rejected as a seventh field are exactly these shapes: the `Discovery: codegraph N, docs N, direct reads N` header; `COVERAGE_GAPS:` followed by free text; and `Artifact:` or `Baseline:` followed by one repository-relative or absolute path. A `report=<path>` inside the `TASK_GATE:` line is part of that field, not a separate line.

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
| `permission-not-expressible` | A non-deny capability decision cannot be expressed in the target's native permission model without inventing an unverified mapping. | Factory records this for non-deny `network.publish` and `delegate` (no documented tool / `Task` withheld from subagents), for an **empty** `mcpServers` whitelist, and for the unenforced entry-point skill a primary agent also renders. The MCP **withholding** sentence is `mcpServers: [] excludes every MCP server so parent MCP was not inherited.`, and it is emitted only when the rendered whitelist is empty: a droid carrying a non-empty whitelist is a non-degraded outcome and its record makes no withholding claim, so the two outcomes are distinguishable in both the frontmatter value and the record. The record itself still appears whenever any of its other conditions applies — including on a whitelisted primary agent, whose entry-point caveat names the value it does not govern (`mcpServers: [kyber-weave]`). **The withholding is now narrowed to the `codegraph` and `context7` servers on every target that withholds anything from the orchestrator** ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)): `kyber-weave` is granted to every agent whose capability profile resolves and allows `filesystem.read`, `conductor` included on the targets that render a conductor file — on Devin and Pi the conductor is a lowered skill with no grant surface, so the outcome there is structural rather than a withholding — so a granted orchestrator is a non-degraded outcome for that server and only its code-graph and third-party-documentation reach is withheld. Command Code makes the same sentence conditional on the derived grant being empty rather than on Squad naming no tool at all. Claude records this for the unenforced entry-point skill and the nested-roster limitation. |
| `operator-channel-not-rendered` | The operator channel was enabled (`KYBER_SQUAD_OPERATOR_CHANNEL=true`) for a target or scope that cannot carry the conductor overlay, so that target's conductor is unchanged. Only Copilot CLI at global scope carries it ([ADR 0045](../adr/0045-conductor-operator-channel-opt-in.md)). | Claude project scope records this on the conductor when the gate is on. |
| `operator-channel-server-name-missing` | Warning, Copilot CLI global scope only. The operator channel gate is on (`KYBER_SQUAD_OPERATOR_CHANNEL=true`) but `KYBER_SQUAD_OPERATOR_CHANNEL_SERVER` is unset or empty, so the conductor renders without the channel ([ADR 0045](../adr/0045-conductor-operator-channel-opt-in.md)). Other targets and scopes get only `operator-channel-not-rendered`. | Install prints the warning and the conductor is unchanged. |
| `operator-channel-server-name-invalid` | Warning, Copilot CLI global scope only. `KYBER_SQUAD_OPERATOR_CHANNEL_SERVER` contains a character outside `[A-Za-z0-9._-]` or is longer than 64 characters. The conductor renders without the channel and the raw value is never written into a rendered file ([ADR 0045](../adr/0045-conductor-operator-channel-opt-in.md)). | Install prints the warning, with the name rejected and not written. |
| `operator-channel-server-absent` | Warning, Copilot CLI global scope only. The gate is on and the server name is valid, but `$COPILOT_HOME/mcp-config.json` does not declare that name under `mcpServers`. The conductor renders without the channel. | Install prints the warning and the conductor is unchanged. |
| `capability-not-isolable` | A capability grant encompasses an unenforceable security boundary: when `process.execute: allow` and `filesystem.write` is `ask` or `deny` on a target whose shell tool can write files through redirection, the withheld write-tool names are unreachable but the underlying write capability remains accessible. The degradation record's Details names the target, the granted shell tool(s), and the withheld write-tool names. See [plan D11](../archive/plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md#3-approved-decisions-owner-dpalfery-2026-09-21) for complete tool mappings. | Claude: `Bash`/`PowerShell` vs. `Edit`/`Write`/`NotebookEdit`; Pi: `bash` vs. `edit`/`write`; ZCode: `Bash` vs. `Edit`/`Write`; Devin: `exec` vs. `edit`/`write`/`apply_patch`/`notebook_edit`; Factory: `Execute` vs. `Create`/`Edit`/`ApplyPatch`; OpenCode: `bash` vs. `edit`; Antigravity: `run_command` vs. `write_to_file`/`replace_file_content`/`multi_replace_file_content`. On each target, the withheld write tools do not appear in the rendered agent's tool allow-list (the `tools:` frontmatter key, or `allowed-tools:` on Devin), while the degradation record's Details identifies both the granted shell tool(s) and the withheld write-tool names. |

---

## Target Capability and Degradation Matrix

| Target Harness | Target Projection | Renderer Coverage | Subagent Delegation | Lowering Policy | Permission Model |
|---|---|---|---|---|---|
| **Codex** | Native Markdown | Implemented and registered | Supported | Not lowered | Native execution. MCP reach is **session-inherited**, not per agent: a custom agent inherits the parent's MCP servers and a custom agent file cannot override those settings (developers.openai.com/codex/subagents, read 2026-10-31), so no per-agent grant exists to render and `conductor` reaches `kyber-weave` as soon as the operator wires the server |
| **Cursor** | Native `.cursor/agents` | Implemented and registered | Supported | Not lowered | Native execution. MCP reach is **session-inherited**, not per agent: subagents inherit every tool from the parent including MCP tools, and there is no `tools` allow-list key (cursor.com/docs/subagents.md, read 2026-10-31), so no per-agent grant exists to render and `conductor` reaches `kyber-weave` as soon as the operator wires the server |
| **Claude** | Native `.claude/agents` + an entry-point skill per primary agent at `.claude/skills/<name>/SKILL.md` | Implemented and registered | Supported; roster enforced only under `claude --agent` | Not lowered; primary agents additionally exposed as `/<name>` (`role-skill-fallback`). `product-owner` and `bug-crusher` reuse their canonical skill as the entry point instead of a derived one | Explicit tools allow-list; `safety-narrowed` on ask; `capability-not-isolable` for shell-implies-write; `permission-not-expressible` for `network.publish`, the nested roster (ignored in nested subagents), the unenforced entry-point skill (runs under session permissions, can auto-load from its description), and the MCP withholding which applies to subagent only. MCP is granted by tool-level wildcard under the shared `filesystem.read` entitlement ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)): `mcp__kyber-weave__*` to every entitled agent — `conductor` included — and `mcp__codegraph__*` and `mcp__context7__*` only to an agent whose capability profile id is not `orchestrator`, so a subagent `conductor` carries the kyber-weave wildcard and neither of the other two |
| **GitHub Copilot** | Native instructions/agents | Implemented and registered | Supported | Not lowered | Native execution. MCP is granted from the agent's **own** `copilot-tools` frontmatter entry `kyber-weave/*`, which is canonical source rather than renderer output: no renderer injects it, and `conductor` carries that one entry on the same `filesystem.read` entitlement every other target applies ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)) |
| **OpenCode** | Native `.opencode/agents` | Implemented and registered | Supported | Not lowered | Native execution; every documented permission key is pinned explicitly, because an omitted key inherits OpenCode's default-allow rather than being withheld. MCP is a server-scoped permission key: `kyber-weave_*` reaches every agent whose capability profile allows `filesystem.read`, `conductor` included, and no `codegraph`/`context7` key is written for the orchestrator |
| **Kilo** | Native `.kilo/agents` | Implemented and registered | Supported | Not lowered | Native execution. Kilo's agent frontmatter carries a `permission` map whose values are `ask`, `allow`, `deny`, or null, and MCP tools are gated by that same map under the namespaced key `{server}_{tool}`, with the server name sanitized only for characters outside `[a-zA-Z0-9_-]` — so the hyphen survives and the server wildcard is `kyber-weave_*` ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)). That map is an **approval** lattice, not a visibility allow-list: `allow` runs a call without the ask prompt, and only a `deny` whose pattern is `*` removes a tool from the model. `KiloRenderer` emits `permission: {kyber-weave_*: allow}` for every agent whose capability profile allows `filesystem.read`, `conductor` included, because a headless subagent's ask fails closed and the grant is what keeps the tool usable. An agent outside that entitlement emits no `permission` key at all: absence of `allow` is the withholding, and a `deny` would assert a decision the capability lattice cannot derive. The map is keyed by tool name, so the capability vocabulary is not a key Kilo reads — every configured capability decision still records `permission-not-expressible`, deny decisions included. The server itself is session-level configuration (`kilo.jsonc`'s `mcp` key) and stays the operator's. Verified against `@kilocode/cli` 7.8.18 (`Kilo-Org/kilocode` tag `v7.8.18`) |
| **Antigravity** | Native `.agents/agents` (directory per agent) | Implemented and registered | Supported (`enable_subagent_tools: true`, `invoke_subagent`) | Not lowered (native conductor with `mainAgent: true`) | Native execution via tool allowlist (`tools:`) and execution switches (`enable_write_tools`); safety-narrowed; capability-not-isolable for shell-implies-write; permission-not-expressible for unenforceable delegates-to roster. MCP is a per-agent `mcpServers` **array** in the agent frontmatter — one `{name, command, args}` entry, with `command`/`args` read from the squad's own `mcp.json` (today `kyber-weave-mcp --repo-root .`) rather than hardcoded — emitted for every agent whose capability profile allows `filesystem.read`, `conductor` included ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)). A squad that declares no such server, or declares it malformed, yields no entry: the fail-closed answer. An agent outside the entitlement records the withheld-server sentence naming the one server, its profile, and the capability the entitlement rests on. MCP tool names are never added to `tools:` — Antigravity resolves that list against its executor registry and fails fast on an unmapped name (`unknown component: tool "..." not found in registry`, surfaced as `AGY_ERROR` on stderr while the process still exits 0, so the failure is silent to a caller reading only the exit code) — and no `enable_mcp_tools` key is emitted, because a frontmatter-declared server is not gated by it. Validated live against `agy` 1.4.12 under the [ADR 0022](../adr/0022-antigravity-native-agents.md) discipline: the `{name, command, args}` entry was called successfully, while the single-key map spelling listed and ran but was never invoked, so it is not emitted. The workspace `.agents/mcp_config.json` remains the operator's to write |
| **Pi** | Native `.pi/agents` + lowered primary agents | Implemented and registered | Supported (via `@tintinweb/pi-subagents` extension) | Lowered (primary agents; the two root wrappers reuse their canonical skill) | Native execution + safety-narrowed; seven-tool vocabulary `read, grep, find, ls, edit, write, bash` — `network.read` has no expressible built-in Pi tool (bare `mcp` in `tools:` is a loader `tools-error`) and still records `permission-not-expressible`; read-only MCP access arrives via the per-target overlay (`pi-extensions`/`pi-tools`), which the renderer now derives for `kyber-weave` on every entitled **subagent**: `extensions: [pi-mcp-adapter]`, the proxy entry `ext:pi-mcp-adapter/mcp`, then one `ext:pi-mcp-adapter/mcp__kyber_weave__<tool>` entry per roster tool, merged and deduplicated with the agent's own declared entries so `research-agent` keeps its hand-authored `context7` pair ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)). The tool namespace maps the server name's hyphen to an underscore — `mcp__kyber_weave__`, never `mcp__kyber-weave__` — which the deferred-tool surface requires `pi-mcp-adapter` 6.0.0 or later to register; 7.0.0 is the supported target, and an operator on 5.x keeps the `mcp` proxy entry while losing only the enumerable per-tool names. A primary agent lowers to `.pi/skills/<name>/SKILL.md`, whose frontmatter carries no `tools` or `extensions`, so the grant is structurally inexpressible for `conductor` on that form and its degradation record names the server and the reason. The adapter's servers are the operator's configuration (project `.mcp.json`, approved on first use in the project), and the in-session tool listing remains an owner probe; `network.publish` still has no built-in tool and still records `permission-not-expressible`, as does the lowered primary |
| **Warp** | Role skills | Implemented and registered | Single-agent context | Lowered (`role-*` on collision) | Harness default; permission-not-expressible for non-deny decisions. MCP reach is **session-inherited** and not per agent: Squad's Warp surface is role skills, which carry no tool scoping (docs.warp.dev/agents/capabilities/mcp, read 2026-10-31), so no per-agent grant exists to render and `conductor`'s lowered skill reaches `kyber-weave` through the session the operator configures |
| **Factory Droids** | Native `.factory/droids` + an entry-point skill per primary agent at `.factory/skills/<name>/SKILL.md` ([ADR 0034](../adr/0034-factory-droid-entry-point-skill.md)) | Implemented and registered | Supported | Not lowered; primary agents additionally exposed as an entry-point skill (`role-skill-fallback`). `product-owner` and `bug-crusher` reuse their canonical skill | Explicit tools array (allow-only documented IDs); safety-narrowed on ask; permission-not-expressible for unmapped `network.publish`/`delegate`, an empty `mcpServers` whitelist, and the unenforced entry-point skill (runs under session permissions: the droid's tools array and `mcpServers` value do not apply to it). MCP is a per-droid **server** whitelist rather than a tool allow-list: `mcpServers` names the servers the squad itself declares (today exactly `kyber-weave`) for any droid whose capability profile allows `filesystem.read`, `conductor` included under the shared entitlement predicate ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)), and is `[]` for a droid whose profile grants no read entitlement or for a squad that declares no servers. The key is never omitted, because omitting it inherits the parent session's servers rather than excluding them. A non-empty whitelist is a non-degraded outcome and records no MCP **withholding** sentence; a whitelisted primary agent's record still exists for the entry-point caveat, which names the rendered value it does not govern (`mcpServers: [kyber-weave]`). The app-level `.factory/mcp.json` / `~/.factory/mcp.json` that launches those servers is the operator's, not Squad's |
| **ZCode** | Native `.zcode/agents` + primary agents lowered to `.zcode/commands` | Implemented and registered | Supported (project and global scope) | Lowered (primary agents to a slash command; the two root wrappers reuse their canonical skill) | Native execution + safety-narrowed; always a non-empty tool list because `tools: []` is ZCode's inherit-everything signal; permission-not-expressible for `network.publish`, the unenforceable `delegate` roster, and MCP withheld from the pure orchestrator for the `codegraph` and `context7` servers only. MCP is granted by enumerated tool name from `toolchain.yml` to every agent whose capability profile allows `filesystem.read`, `conductor` included — it receives the eight `kyber-weave` names and neither the codegraph nor the context7 names ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)). ZCode expands no wildcard, so the eight names are enumerated literally, and `validateSubagentMcpRequirements` makes them a hard connected-server requirement for every agent including `conductor`: `squad doctor` fails a ZCode install that does not declare the `kyber-weave` server |
| **Devin** | Native `.devin/agents` (directory per agent) + primary agents lowered to `.devin/skills` | Implemented and registered | Main session only: subagents cannot delegate, because Devin has no enforceable roster (project and global scope) | Lowered (primary agents to a skill; the two root wrappers reuse their canonical skill) | Explicit `allowed-tools` allow-list, never empty; safety-narrowed on ask, except where an agent names a `devin-capability-profile` that grants the capability outright ([ADR 0028](../adr/0028-devin-target-scoped-authoring-capability-profiles.md): `architect`; `product-owner` held the same envelope while it was a subagent, but it is now a primary shared identity that Devin lowers to its canonical skill, so the envelope no longer applies to it and the initialise-before-edit sentence lives in the skill's `spec-authoring` reference instead; the conductor pre-creates the file only as fallback); capability-not-isolable for shell-implies-write; permission-not-expressible for `network.publish`, the withheld `delegate` roster, and the lowered conductor's unenforced decisions. MCP is granted by enumerated tool name from `toolchain.yml`, on the **subagent path only**: every subagent whose capability profile allows `filesystem.read` gains the qualified `mcp__kyber-weave__<tool>` names in its `allowed-tools`, while the `codegraph` and `context7` names stay withheld from the orchestrator ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)). The `conductor` is granted nothing here and that is structural rather than narrow: Devin lowers a primary agent to a skill, a Devin skill carries no rendered `allowed-tools` (on a skill the key pre-approves rather than restricts, which is why the renderer emits none), so the lowered conductor has no governed grant surface and reaches `kyber-weave` only through whatever its session already has. The parity guard pins `devin` and `pi` as the two targets with no conductor grant surface. Skills carry no `allowed-tools` or `permissions`: on a Devin skill `allowed-tools` and `permissions.allow` pre-approve, and whether `permissions.deny` reaches the subagents a skill dispatches is undocumented. The lowered primary agents carry `triggers: [user]`, since Devin Cloud sees skills but loads no custom subagents |
| **Command Code** | Native `.commandcode/agents` + an entry-point skill per primary agent at `.commandcode/skills/<name>/SKILL.md`; the global root is `~/.commandcode` | Implemented and registered | Not supported: a subagent never receives the `agent` or `agent_output` tools, so `delegate` is not expressible | Not lowered; primary agents additionally exposed as an entry-point skill (`role-skill-fallback`), invoked as `/<name>`. The two root wrappers reuse their canonical skill | Explicit `tools` allow-list of snake_case tool names, always written as a flow sequence so an empty grant is the visible `tools: []` (an omitted `tools` already grants nothing); only `allow` grants, `ask` narrows to deny and records `safety-narrowed`; `capability-not-isolable` for shell-implies-write; `permission-not-expressible` for `network.publish` (no built-in tool), `delegate`, parent MCP **when the derived grant is empty** (MCP tools reach a subagent only when named in `tools`, so an agent granted nothing names nothing), and the unenforced entry-point skill (runs under the session's permissions: the agent's `tools` allow-list does not apply to it). MCP is granted by enumerated `mcp__<server>__<tool>` name from `toolchain.yml` to every agent whose capability profile allows `filesystem.read`, `conductor` included, which receives the eight `kyber-weave` names and no `codegraph`/`context7` name; the granted case records no withholding claim ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)) |

The thirteen rows are the declared target roster. All thirteen targets (`copilot`, `cursor`, `claude`, `codex`,
`antigravity`, `opencode`, `kilo`, `pi`, `factory`, `warp`, `zcode`, `devin`, and `commandcode`) have implemented and registered renderers.

---

## Non-Broadening Guarantee

Kyber-Squad enforces a strict non-broadening guarantee across all translations:

> **No capability permission may be escalated from `deny` or `ask` to `allow` during target rendering or role-skill lowering.**

If a target harness cannot guarantee the containment or authorization boundaries specified in an agent's capability profile, the engine will narrow the permission or omit the component entirely.

One explicit, opt-in exception: the operator channel overlay ([ADR 0045](../adr/0045-conductor-operator-channel-opt-in.md)) grants the conductor one MCP tool entry, `<name>/*`, for the server the operator names. It renders only on Copilot CLI at global scope, only when the gate is on and the server is declared in that CLI's MCP config. The overlay escalates nothing else.

The `kyber-weave` grant is not a second exception to this guarantee but a recorded widening of one
profile's reach: it is a read-only server, no write, execution, or network capability is involved,
and [ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md) records it in those terms because
`conductor`'s profile denies `filesystem.search` and the documentation tools reach text that
decision withholds. Every capability decision in the lattice stands unchanged.

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

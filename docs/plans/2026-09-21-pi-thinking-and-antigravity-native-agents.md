---
id: plans/2026-09-21-pi-thinking-and-antigravity-native-agents
title: Emit Pi thinking levels and reclassify Antigravity as a native per-agent target
doc-type: plan
status: current
owner: dpalfery
last-reviewed: 2026-09-21
component: KyberSquad
development-mode: test-first
---

# Emit Pi thinking levels and reclassify Antigravity as a native per-agent target

**Status:** Ready
**Date:** 2026-09-21
**Goal:** (Track A) Make `PiRenderer` emit a `thinking:` reasoning-effort key by splitting an
optional `[thinking=<level>]` suffix off the `pi:` `models.yml` value. (Track C) Reclassify
`AntigravityRenderer` from fallback role-skill lowering to a native per-agent renderer —
`.agents/agents/<name>/agent.md` (a directory-per-agent shape no existing target uses) plus
unchanged `.agents/skills/<name>/SKILL.md` — with a capability→tool mapping validated live
against the installed `agy` 1.2.7 before any Test contract pins it.

---

## 1. Problem / Motivation

This plan closes the two open tasks recorded in
[the Black Hawk Hotel handover](../todo/black-hawk-hotel-todo.md) (Task B needed no work and is
not part of this plan):

- **Task A.** `PiRenderer` emits no reasoning-effort key today. Pi's `pi-subagents` 0.19.0
  accepts one (`thinking`), and `PiRenderer.cs:45` already lists `thinking` among the frontmatter
  keys the extension accepts — the renderer simply never emits it
  (`PiRenderer.cs:48-50`: "this renderer emits only `name`, `description`, `model`, `tools`,
  `extensions`, and `allowed_subagents`… never the rest"). A prior half-finished edit to
  `models.yml` was reverted deliberately because landing the `pi:` suffix without the parser
  change would have sent a literal `zai/glm-5.3[thinking=high]` to Pi as a model id
  (`PiRenderer.ResolvePiModel` returns the harness string verbatim, `PiRenderer.cs:346-361`).

- **Task C.** `AntigravityRenderer` is registered as a **fallback** renderer
  (`SquadRendererRegistry.cs:164-166` excludes `SquadTarget.Antigravity` from the native list):
  every canonical role lowers to a skill under `.agents/skills/`, `role-`-prefixed on a name
  collision (`AntigravityRenderer.cs:147-190`). Antigravity has had a native per-agent primitive
  since before this was noticed — [the verified spec](../todo/antigravity-native-agents.md)
  confirms `agy agent` / `agy --agent <name>` / one `agent.md` per agent directory at
  `~/.gemini/config/agents/<name>/agent.md` (global) and `<workspace>/.agents/agents/<name>/agent.md`
  (project). The conductor suffers most today, because a delegated Antigravity session cannot
  load a Squad role as an agent — only as a skill to be told to follow.

Both tracks touch disjoint files except one line each in `products/kyber-squad/profiles/models.yml`
(Track A edits each profile's `pi:` value; Track C adds an `antigravity:` value to the same
profile blocks) — see the dependency note in §10.

---

## 2. Source of truth

Every current-behavior claim below was read from source in this worktree
(`/Users/dave/git/claude-worktree/kyber-weave/zcode-harness-subagents-e2fff4`) on 2026-09-21,
cited by file:line. Facts about Pi and ZCode already verified by the conductor and restated in
the operation brief were re-confirmed independently:

- `SquadRenderValidationException` — `src/KyberWeave.Core/Squad/Rendering/SquadRenderModels.cs:60`
  (`public sealed class SquadRenderValidationException : InvalidOperationException`). Confirmed.
- `PiRenderer.cs:45` — the accepted-keys list reads
  `…<c>isolation</c>, <c>model</c>, <c>thinking</c>, <c>max_turns</c>,…`. Confirmed; `thinking`
  is literally on that line.
- `PiRenderer.cs:48-50` — "this renderer emits only `name`, `description`, `model`, `tools`,
  `extensions`, and `allowed_subagents` — the plan's approved subset — never the rest." This is
  the sentence Task A must correct (`thinking` moves into the emitted set when present).
- `PiRenderer.ResolvePiModel` — `PiRenderer.cs:346-361`. Reads the `pi` harness override from
  the model profile, returns it verbatim unless it (or the neutral `default`) is literally
  `"inherit"` (`StringComparison.Ordinal`), in which case the key is omitted. No suffix parsing
  exists today.
- `products/kyber-squad/profiles/models.yml` — read in full. The `pi:` line for each profile is
  at line 11 (`deep-planning: zai/glm-5.3`), line 21 (`fast: opencode/muse-spark-1.3-contributor-free`),
  line 31 (`general: opencode/muse-spark-1.3-contributor-free`), line 38 (`orchestration: inherit`),
  line 48 (`reviewer: opencode-go/kimi-k2.7-code`). These are exactly the five lines D2 (below)
  names, confirmed against the file, not assumed from the todo. **The file has no `antigravity:`
  key anywhere** — confirmed by reading all 50 lines.
- `products/kyber-squad/schemas/model-profiles.schema.json:23` — the schema **already** declares
  an optional `antigravity` string property (added ahead of any renderer using it, alongside
  `warp`, `factory`, `pi`, `zcode`). No schema change is needed for Track C; only populating the
  key in `models.yml` is new work.
- `AntigravityRenderer.cs` (full file read). `RenderAsync` (lines 96-226) renders every canonical
  skill unconditionally, then for every agent: reuses the canonical skill when the identity is
  both a profile-declared shared identity (`ResolveSharedIdentities`, lines 42-50, reading
  `fallbacks.yml`'s `role-skill` profile) **and** an occupied skill name (lines 149-164); emits a
  `role-<name>` skill when only occupied (lines 165-178); otherwise emits `<name>` directly
  (lines 179-190). `BuildPermissionDegradation` (lines 291-328) records `permission-not-expressible`
  for any non-`deny` capability decision, because "Antigravity skills cannot express capability
  permissions" (line 324-327) — that sentence becomes false once native `agent.md` frontmatter
  can express `enable_write_tools` / `tools`.
- `SquadRendererRegistry.cs:164-166` — the native-target set is
  `Codex, Cursor, Claude, Copilot, OpenCode, Kilo, Factory, Pi, ZCode`. Antigravity and Warp are
  the two remaining fallback targets (confirmed by absence).
- `SquadRendererRegistry.AgentOutputPath` (lines 370-391) and `SkillOutputPath` (lines 437-454) —
  Antigravity's current agent-output branch (line 384-385) returns a **skill** path via
  `ResolveFallbackOutputIdentity`; there is no directory-per-agent branch anywhere in this file
  today, unlike Pi's and ZCode's `Invocation`-dependent branches (`ResolvePiAgentOutputPath`,
  lines 393-409; `ResolveZCodeAgentOutputPath`, lines 422-435), which Track C's native branch
  must join.
- `SquadDeploymentPlan.IdentityFromRelativePath` — `src/KyberWeave.Core/Squad/Deployment/SquadDeploymentPlan.cs:531-546`.
  Handles `SKILL.md` (parent-directory name) and `<name>.agent.md` (suffix strip, Copilot's
  shape) but has **no branch for a bare `agent.md` inside a `<name>/` directory** — the shape
  Track C introduces. Doctor's `CollectUnmanagedCollisions` (same file, lines 484-522) calls this
  helper, so an unhandled `agent.md` would report a wrong collision identity.
- `SquadGlobalRoots.cs:95` — `SquadTarget.Antigravity => ResolveWithOverride(null, Path.Combine(".gemini", "config"))`.
  The root itself (`~/.gemini/config`, no override) is unaffected by Track C; only the class
  remarks (lines 22, 40) calling Antigravity "no agent primitive" are now wrong and must be
  corrected.
- `products/kyber-squad/profiles/capabilities.yml` (read in full). Two profiles are concrete
  instances of D4's XOR case: `investigator` (line 64-72: `process.execute: allow`,
  `filesystem.write: deny`) and `documentation` (line 55-63: `filesystem.write: allow`,
  `process.execute: deny`). `worker` and `publishing-worker` hold both; `architect`,
  `orchestrator`, `product-planning`, `read-only` hold neither at `allow`.
- **Existing-deployment question (defect 6), answered from code, not assumed.**
  `SquadDeploymentPlan.CreateUpdate` (`SquadDeploymentPlan.cs:258-400`) already diffs a receipt
  against a fresh render: any previously-owned file whose `(Target, RelativePath)` identity is
  **not** in the newly rendered set (lines 356-386) is deleted if its on-disk bytes still match
  the receipt-recorded SHA-256 (lines 372-383, planning `SquadFileMutation.Delete`), or retained
  untouched if it was locally edited (line 385). This is exactly the shape-change case: once
  `AntigravityRenderer` stops emitting `.agents/skills/<name>/SKILL.md` for lowered agents and
  starts emitting `.agents/agents/<name>/agent.md`, the old skill paths simply stop appearing in
  `renderedIdentities` and are cleaned up automatically on the next `squad update --target
  antigravity` (or `--global`) — no new deployment code is needed. This is pinned by a
  regression test (C3, §9) rather than left as an unverified claim.
- `docs/kyber-squad/architecture.md:65-77` (target coverage table), `:124-137` (role-skill
  lowering section 3), `:279-299` (renderer registration list and rendering table),
  `:371-375` (coverage summary); `docs/kyber-squad/requirements.md:49`, `:65-74`;
  `docs/kyber-squad/onboarding.md:63,71,77-78,118-125,171` — every location that currently
  documents Antigravity as "fallback" / "role-skill lowering" / "single-agent context", read and
  line-cited for Track C's docs task.
- `docs/adr/README.md` — ADR 0019 (Pi: fallback→native reclassification) and ADR 0021 (ZCode:
  command lowering, a first-of-its-kind primitive). Kilo and Factory (straightforward native
  agent+skill additions, no reclassification or novel primitive) have **no** ADR. This is the
  precedent behind D6 (§3).
- `tests/KyberWeave.Tests/SquadSharedIdentityProjectionTests.cs:16` —
  `AntigravityFallbackReusesSharedIdentityAsExactlyOneUnprefixedSkill` names the fallback
  behavior directly in its test name; it must be revisited once Antigravity is native.
- `tests/KyberWeave.Tests/PiSquadLifecycleTests.cs:60-79` —
  `InstallAsync_DryRun_AntigravityAndPi_ProduceDisjointPathsSummingToTheIndividualCounts` only
  asserts disjoint paths and summed counts; both hold regardless of Antigravity's internal
  shape, so this test needs re-running, not editing — confirmed by reading its body.
- `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs:62-77` — the `ResolveRenderer`
  doc comment states "Antigravity and Warp are fallback role-skill lowering"; needs correcting.

---

## 3. Approved decisions (owner, dpalfery, 2026-09-21)

- **D1 (Combined plan, parallel tracks).** One plan covers Task A (Pi) and Task C (Antigravity).
  The two tracks' file scopes are disjoint except `products/kyber-squad/profiles/models.yml`,
  where they edit different keys in the same profile blocks — sequenced, not parallelized, for
  that one file (§10).

- **D2 (Pi thinking levels).** `models.yml` `pi:` values change to:

  | Profile | Line (confirmed) | Current `pi:` | New `pi:` |
  |---|---|---|---|
  | `deep-planning` | 11 | `zai/glm-5.3` | `zai/glm-5.3[thinking=high]` |
  | `reviewer` | 48 | `opencode-go/kimi-k2.7-code` | `opencode-go/kimi-k2.7-code[thinking=high]` |
  | `general` | 31 | `opencode/muse-spark-1.3-contributor-free` | `opencode/muse-spark-1.3-contributor-free[thinking=medium]` |
  | `fast` | 21 | `opencode/muse-spark-1.3-contributor-free` | `opencode/muse-spark-1.3-contributor-free[thinking=low]` |
  | `orchestration` | 38 | `inherit` | `inherit` (unchanged — the conductor lowers to a Pi skill, which carries no frontmatter model) |

  `PiRenderer.ResolvePiModel` splits an optional trailing `[thinking=<level>]` off the resolved
  `pi:` value, returning the bare model id and the level separately. The level is validated
  against the closed six-value domain `off | minimal | low | medium | high | max` and **fails
  closed** (`SquadRenderValidationException`) on anything else. `thinking:` is emitted only when
  a level is present. Matching is **case-sensitive** (`StringComparison.Ordinal`) — decided, not
  assumed: no source (the `pi-subagents` snippet in the todo, nor any file read for this plan)
  states or implies case-insensitive parsing, and every other closed-domain comparison in
  `PiRenderer.cs` (`ResolvePiModel`'s `"inherit"` check at line 357, `ResolvePrefixedDirectory`'s
  `.pi/` check at line 149, `DescribeDecision`) already uses Ordinal. A value with no `[thinking=…]`
  suffix at all still emits `model` and omits `thinking`, matching today's behavior for every
  profile whose `pi:` value carries no suffix. `PiRenderer.cs:45-50`'s remarks are updated: line 45
  already lists `thinking` as accepted; lines 48-50's "never the rest" sentence is corrected to
  name `thinking` as conditionally emitted.

- **D3 (Live validation gates the Antigravity Test contract).** The capability→tool mapping in
  [the verified spec](../todo/antigravity-native-agents.md) ("The tool vocabulary" table) is
  **inferred from 21 hand-authored files, not read from a published schema** — the spec says so
  explicitly. No Antigravity contract-test task in this plan may pin that mapping until it is
  validated live against the installed `agy` 1.2.7 (`/Users/dave/.local/bin/agy`). Task C0 (§9)
  is the explicit first task on the Antigravity track, owned by a specialist with shell access,
  and records evidence in `docs/todo/antigravity-capability-verification-evidence.md` (a new,
  plan-owned evidence document — not an edit to the two todos this plan supersedes, since those
  move to Superseded status independently of this plan's execution timeline; see §11). **If C0's
  live results contradict the inferred mapping**, the affected rows of C1's Test contract (§8)
  reopen for conductor re-approval and this plan returns to Draft before C1 proceeds to author
  tests against the contradicted mapping — this is not a hypothetical, it is the literal
  test-first-contract.md rule ("changing an approved Test contract … is a scope change").

- **D4 (`filesystem.write` XOR `process.execute` narrowing).** When a role's capability profile
  grants exactly one of `filesystem.write` / `process.execute` at `allow` (the other at `ask` or
  `deny`), the renderer emits `enable_write_tools: true` (the switch that unlocks *both* tool
  families per the spec's Answer 2 — there is no separate switch) but narrows the `tools` list to
  **exactly** the granted capability's tools, omitting the other capability's tools entirely so
  the model has no path to them regardless of what the switch nominally unlocks. Concrete cases,
  confirmed against `capabilities.yml`:
  - `investigator` (`process.execute: allow`, `filesystem.write: deny`) →
    `enable_write_tools: true`, `tools` includes `run_command` and excludes
    `write_to_file` / `replace_file_content` / `multi_replace_file_content`.
  - `documentation` (`filesystem.write: allow`, `process.execute: deny`) →
    `enable_write_tools: true`, `tools` includes `write_to_file` / `replace_file_content` /
    `multi_replace_file_content` and excludes `run_command`.

  When **both** are `allow` (`worker`, `publishing-worker`): `enable_write_tools: true`, `tools`
  includes all four. When **neither** is `allow` (`architect`, `orchestrator`,
  `product-planning`, `read-only` — `architect`'s and `product-planning`'s `filesystem.write:
  ask` narrows to withheld, recording `safety-narrowed`, matching the established
  ask-both-withhold pattern from `PiRenderer`/`ZCodeRenderer`): `enable_write_tools: false`,
  neither tool family present. Pinned in C1's contract tests (§8) against `investigator` and
  `documentation` by name.

- **D5 (Conductor renders as a native agent).** Approved as recommended (dpalfery, 2026-09-21).
  The primary-invocation `conductor` agent renders as a genuine native Antigravity agent at
  `.agents/agents/conductor/agent.md` with `mainAgent: true` and `enable_subagent_tools: true` —
  it does **not** lower to a skill the way it does on Pi and ZCode. Task C0 (§9) still
  re-confirms live that a non-`mainAgent` custom agent is genuinely selectable via
  `agy --agent <name>`; if C0's live results contradict that premise, the plan falls back to
  skill-lowering and C1's Test contract (§8) reopens per D3.

- **D6 (A dedicated ADR is required).** Approved as recommended (dpalfery, 2026-09-21). C4 (§9)
  is unconditional: a new ADR records the fallback→native reclassification, the
  directory-per-agent shape, and the `enable_write_tools` XOR narrowing rule, following ADR
  0019/0020's precedent and structure.

- **D7 (Defer the camelCase optional fields).** Approved as recommended (dpalfery, 2026-09-21).
  `hidden`, `inheritMcp`, and `commandExecutionPolicy` are deferred; none is emitted in this
  first pass.

- **D8 (Exclude `FLASH_LITE`).** Approved as recommended (dpalfery, 2026-09-21). Only the three
  confirmed tier spellings (`inherit`, `flash`, `pro`) are ever emitted; the fourth tier is not
  emitted under any spelling.

- **D9 (`antigravity:` model-profile values).** Approved exactly as proposed (dpalfery,
  2026-09-21):

  | Profile | `antigravity:` tier | `reasoning_effort` |
  |---|---|---|
  | `deep-planning` | `pro` | `high` |
  | `reviewer` | `pro` | `high` |
  | `general` | `flash` | `medium` |
  | `fast` | `flash` | `low` |
  | `orchestration` | `flash` | `minimal` |

- **D10 (`capability-not-isolable` degradation for the `process.execute`-allow /
  `filesystem.write`-withheld case).** Approved as recommended (dpalfery, 2026-09-21), reopened
  and resolved under the D3 gate after C0 ran. C0's evidence
  (`docs/todo/antigravity-capability-verification-evidence.md` §5, Summary) proved that
  `filesystem.write: deny`/`ask` narrows the *named* write tools but not the underlying write
  capability when `process.execute: allow` also holds, because a granted shell can write files
  via redirection. Applies to any role where `process.execute: allow` and `filesystem.write` is
  `ask` or `deny` — concretely `investigator` (`task-reviewer`) and `reviewer`
  (`code-reviewer`/its council), per `capabilities.yml` lines 64-72 and 114-137. Resolution:
  withhold the named write tools by name (unchanged) **and** emit a new structured degradation
  record, code `capability-not-isolable`, naming the granted shell tool and the withheld write
  tools it can still reach through redirection. A new row for this code is added to
  `docs/kyber-squad/requirements.md`'s Degradation Taxonomy (owned by task DS2, §9).

- **D11 (Cross-target scope: fix every renderer sharing the shell-implies-write shape, now).**
  Approved (dpalfery, 2026-09-21) — **not** the architect's original recommendation, which was to
  scope this plan to Antigravity only and defer the rest to a follow-up todo. The owner directed
  an immediate cross-target fix instead. Grounded in code, read for this revision:
  `ClaudeRenderer.CapabilityTools` (`src/KyberWeave.Core/Squad/Rendering/ClaudeRenderer.cs:73-79`,
  `Bash`/`PowerShell` vs. `Edit`/`Write`/`NotebookEdit`), `PiRenderer.CapabilityTools`
  (`PiRenderer.cs:121-127`, `bash` vs. `edit`/`write`), `ZCodeRenderer.CapabilityTools`
  (`ZCodeRenderer.cs:202-210`, `Bash` vs. `Edit`/`Write`), `FactoryRenderer.CapabilityTools`
  (`FactoryRenderer.cs:59-66`, `Execute` vs. `Create`/`Edit`/`ApplyPatch`),
  `OpenCodeRenderer.CapabilityPermissions` (`OpenCodeRenderer.cs:68-75`, `bash` vs. `edit`) — all
  five share Antigravity's exact shape: a single named shell-class tool held distinct from named
  write tools, both derived from the same capability lattice. Three renderers were checked and
  found **not** affected, with no new task: `CodexRenderer` and `KiloRenderer` have no
  frontmatter tool allow-list at all — every non-`deny` capability decision is already recorded
  as `permission-not-expressible` (`CodexRenderer.cs` remarks; `KiloRenderer.cs` remarks), so
  there is no separate write/execute grant to conflate. `CursorRenderer` bundles both capabilities
  behind one `readonly` boolean (`CursorRenderer.cs:55`,
  `ReadOnlyEnforcedCapabilities = ["filesystem.write", "process.execute"]`) and already records
  "Canonical denies not enforced without readonly" via its existing `permission-not-expressible`
  record (`CursorRenderer.cs:260-265`) — the same gap, already covered by an existing mechanism.
  `CopilotRenderer` is architecturally different and excluded: its `tools` frontmatter comes from
  `agent.CopilotTools`, a hand-authored per-agent list (`CopilotRenderer.cs:160`), not a
  lattice-derived `CapabilityTools` mapping — there is no per-role narrowing rule for this plan to
  patch; noted in C4's ADR as an awareness point, not a task. `WarpRenderer`'s fallback skills are
  instruction-only, exactly like Antigravity's superseded fallback shape, and already record
  `permission-not-expressible` for any non-`deny` decision — unaffected, per this plan's existing
  Warp exclusion (§6).
  Implementation: one shared helper (task DS1/DS2, §9) computes the `capability-not-isolable`
  record given a target token, the granted shell tool name(s), and the withheld write tool
  name(s), because the condition and record shape are identical across all six affected
  renderers; each renderer then gets its own RED/GREEN task pair (DCL, DPI, DZC, DFA, DOC, and
  Antigravity's existing C1/C2) wiring its `CapabilityTools`-derived shell/write tool names into
  the shared helper — a shared computation, not six independent reimplementations, but still one
  test-contract row per renderer so each harness's exact tool names are pinned individually.
  `PiRenderer.cs` is mid-edit by Track A's A2 (uncommitted, under review); DPI1/DPI2 are
  sequenced after A2 to avoid a concurrent-edit race on that file (§9, §10).

- **D12 (The five INCONCLUSIVE tool-mapping rows, and delegate separately).** Two decisions,
  both dpalfery, 2026-09-21:
  - **General rows — Approved as recommended.** `grep_search`, `replace_file_content`,
    `multi_replace_file_content`, `search_web`, `read_url_content` are withheld from every
    Antigravity profile this pass (not emitted in `tools:` regardless of the owning capability's
    decision), recorded as a known limitation. A follow-up todo,
    `docs/todo/shell-implies-write-live-verification-other-targets.md` (created by task DS2, §9),
    also records that the new `capability-not-isolable` degradation on Claude/Pi/ZCode/Factory/
    OpenCode (D11) is grounded in structural code inspection — a named shell tool distinct from
    named write tools — rather than the live redirection proof C0 achieved for `agy`; a live
    check on each harness would strengthen it but is not required to record a degradation, since
    recording errs toward caution rather than the false confidence D3 guards against.
  - **`delegate` — decided separately, not per the general-rows default.** Emit delegation now,
    on static evidence (21 hand-authored backup-corpus agents), not live-exercised. Every agent
    whose canonical `delegates-to` roster is non-empty
    (`products/kyber-squad/schemas/agent.schema.json:51`, `SquadAgent.DelegatesTo`) gets
    `invoke_subagent` in `tools:` and `enable_subagent_tools: true`; `orchestrator`/`conductor`
    also get `manage_subagents`. An agent with an empty roster gets neither. Because no observed
    Antigravity key restricts *which* agents may be invoked, the renderer also records a
    degradation that the delegates-to roster is unenforceable — reusing the existing
    `permission-not-expressible` code `ClaudeRenderer` already uses for the same gap
    (`ClaudeRenderer.cs:400-409`, "Claude Code ignores Agent(roster) parentheses… the permitted
    delegation roster is not enforced for nested Task/Agent spawns"), not a new code.

### Approval (owner dpalfery, 2026-09-21)

**Approved: "Approve and execute"** with one execution constraint amendment to C0:
- C0 runs its live agy session in a **throwaway scratch workspace** using PROJECT-scope agent discovery (`<scratch-workspace>/.agents/agents/<role>/agent.md`, per the verified spec's project path).
- C0 must **NOT** write, copy, or modify anything under `~/.gemini/config/` (the owner's live global Antigravity config). Reading the existing backup corpus at `~/.gemini/config/agents.bak.20260918/` is allowed.
- Every `agy` invocation that starts a model session passes an explicit `--model` for Gemini Flash (to be verified with `agy models` before C0 runs).

This amendment is recorded in the C0 task description (§9) and its Test contract row (§8) below.

### Approval (owner dpalfery, 2026-09-22) — Revision 2

**Approved: "Approve and execute"** with scope: decisions D10-D12 (§3), the reopened C1/C2 rows (§8), and Track D (DS1-DS2, DCL1-DCL2, DZC1-DZC2, DFA1-DFA2, DOC1-DOC2, DPI1-DPI2) in §8/§9/§10, at MAX_CONCURRENCY 2. No amendments.

---

## 4. Decision ledger

No open decisions. The five questions raised in the initial round (`AG-CONDUCTOR`, `AG-ADR`,
`AG-CAMELCASE`, `AG-TIER`, `AG-MODELVALUES`) were all answered by the owner (dpalfery,
2026-09-21) exactly as recommended/proposed and are recorded as D5-D9 in §3 above.

D3's live-validation gate fired on 2026-09-21: C0 ran and its evidence contradicted D4's
`investigator`-shaped narrowing claim (see §3 D4's reopened-and-resolved note, and D10-D12).
Three questions this raised (`capability-not-isolable` recording, cross-target scope, and the
INCONCLUSIVE rows plus delegate) were answered by the owner on 2026-09-21 and are recorded as
D10-D12 in §3 above — D11 explicitly against the architect's recommendation (Antigravity-only);
the owner directed an immediate cross-target fix instead, which is now Track D (§8, §9). The
ledger is empty again; the new Track D tasks (DS1-DS2, DCL1-DCL2, DPI1-DPI2, DZC1-DZC2,
DFA1-DFA2, DOC1-DOC2) and C1/C2's revised scope were approved 2026-09-22 for execution.

---

## 5. Investigation findings

Summarized inline in §2 with file:line citations; not repeated here. Two design points worth
naming explicitly because they are not obvious from the spec alone:

- **The "Native Both" pattern already exists and Track C simply joins it.** `docs/kyber-squad/architecture.md`'s
  role-skill-lowering flowchart (§3) already has a "Native Both: Emit Native Agent + Canonical
  Skill (Different Namespaces)" branch for the seven distinct-body collisions (`csharp-dev`,
  `dal-dev`, `github-devops`, `maui-dev`, `product-owner`, `python-dev`, `test-dev`) on every
  native target. Once Antigravity is native, this branch applies to it too: an agent and a
  same-named canonical skill simply render to different paths (`.agents/agents/<name>/agent.md`
  vs `.agents/skills/<name>/SKILL.md`) with no `role-` prefix needed. This **eliminates**
  `AntigravityRenderer`'s current `ResolveOccupiedIdentities`/`ResolveSharedIdentities`/
  `loweredAgents` collision machinery (`AntigravityRenderer.cs:42-69,147-223`) — the native
  renderer is structurally simpler than the fallback one it replaces, matching Kilo's/ZCode's
  unconditional `foreach (agent) render; foreach (skill not shared-identity) render` shape.
- **`enable_mcp_tools`** inherits the parent's already-configured MCP servers (verified spec,
  Answer 2, citing a changelog fix) — it is not a per-server grant the way ZCode's MCP tool names
  are (ADR 0021, decision 5). This plan does **not** attempt a ZCode-style fully-qualified MCP
  roster for Antigravity; `enable_mcp_tools` is emitted as a plain boolean gated on whether the
  role's capability profile grants any of the two Squad capabilities MCP servers exist to serve
  (`network.read` for context7/docs lookups) — exact gating is part of C0's evidence-gathering
  scope, since the todo does not resolve it and no test may pin an unverified rule (D3).
- **The shell-implies-write property is not Antigravity-specific.** C0 (§9) live-verified it
  against `agy`, but the same shape — a named shell-class tool held distinct from named write
  tools, both derived from `CapabilityTools`-style capability→tool mappings — already exists in
  `ClaudeRenderer`, `PiRenderer`, `ZCodeRenderer`, `FactoryRenderer`, and `OpenCodeRenderer`
  (D11, §3). `CodexRenderer`, `KiloRenderer`, `CursorRenderer`, and the `WarpRenderer` fallback
  path are structurally different and already record the same class of gap through an existing
  mechanism; `CopilotRenderer` uses hand-authored per-agent tools rather than a lattice-derived
  mapping and is out of scope for an automated fix.

---

## 6. Scope

**In (Track A):** `PiRenderer.ResolvePiModel` suffix-splitting and validation;
`products/kyber-squad/profiles/models.yml` `pi:` value updates (D2); `PiRenderer.cs` remarks
correction; `PiRendererContractTests.cs` coverage for the split, the six-value domain, the
fail-closed path, and the no-suffix case.

**In (Track C):** live capability-mapping verification against `agy` 1.2.7; `AntigravityRenderer`
rewritten to native per-agent + per-skill emission; `SquadRendererRegistry`'s `isNative` set,
`AgentOutputPath`, and native-validation branches; `SquadDeploymentPlan.IdentityFromRelativePath`'s
new `agent.md` branch; `SquadGlobalRoots` remarks correction; `products/kyber-squad/profiles/models.yml`
`antigravity:` column (values per D9); contract, shared-identity, and lifecycle
test updates; documentation alignment across architecture/requirements/onboarding/renderer-coverage;
an ADR (D6, expanded per D11 to also record the cross-target `capability-not-isolable` finding);
the declared gate suite; a `docs-dev` closeout.

**In (Track D, added 2026-09-21 per D11):** a shared `capability-not-isolable` degradation
helper; wiring it into `ClaudeRenderer`, `PiRenderer`, `ZCodeRenderer`, `FactoryRenderer`, and
`OpenCodeRenderer` (in addition to `AntigravityRenderer` via C1/C2); a new Degradation Taxonomy
row in `docs/kyber-squad/requirements.md`; a follow-up todo recording that the five renderers'
records are structurally, not live, evidenced; contract-test coverage per renderer (§8, §9).

**Out:** Actually running `squad update --global --target antigravity` against the owner's live
1017-file global receipt (a live-state operation the owner performs post-merge, not a plan task —
§11); ZCode plugin packaging (unrelated, already tracked in
[zcode-plugin-packaging.md](../todo/zcode-plugin-packaging.md)); any change to Warp, which remains
fallback role-skill lowering and is unaffected by this plan; archiving
[the two source todos](../todo/black-hawk-hotel-todo.md) — per the intake contract, the
conductor's docs-dev promotion step (run immediately after this Draft save) records this plan as
their successor and marks them Superseded; they archive only after this plan reaches Ready, and
that archival is not a task in this plan's task list (defect 5); changing `CodexRenderer`,
`KiloRenderer`, `CursorRenderer`, or `CopilotRenderer` — checked under D11 and found not to need
a task (§3 D11, §5).

---

## 7. Development mode

`development-mode: test-first` (default; not overridden). Every implementation task's Test
contract (§8) is decision-complete: all five questions raised in the initial round (D5-D9, §3)
were answered by the owner on 2026-09-21.

---

## 8. Test contract

### Track A — Pi thinking

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| A1/A2 | `tests/KyberWeave.Tests/PiRendererContractTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~PiRendererContractTests"` | `ResolvePiModel` splits `[thinking=<level>]`; the six-value domain (`off/minimal/low/medium/high/max`) is accepted case-sensitively; any other value throws `SquadRenderValidationException`; `thinking:` is emitted only when a level is present; a `pi:` value with no suffix still emits `model` and omits `thinking`; the five `models.yml` profiles from D2 resolve to their documented split values. | A1 authors these assertions against the current (unmodified) `ResolvePiModel`/`models.yml` and records the failing run — every new assertion fails because no suffix syntax exists yet. | A2 lands the parser change and the `models.yml` edits in the same task (defect-2 rule: never a state where `models.yml` carries a suffix the renderer passes through verbatim) and the same test file, unmodified from A1, passes. |

### Track C — Antigravity native agents

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| C0 | *(no test — investigation task)* | `agy --version`, `agy agent`, a live `agy --agent <non-mainAgent-role>` session in throwaway scratch workspace (PROJECT-scope), plus `strings -a` over the `agy` binary; every `agy` call with `--model Gemini-Flash` (exact id from `agy models`); no writes to `~/.gemini/config/` | Marked explicitly as the plan's one no-test task (test-first-contract.md: "silence is invalid" — this states it). Read-only verification replaces a test: C0 confirms or contradicts (1) the seven-capability tool mapping, (2) the `model`/`reasoning_effort` enum domains, (3) `enable_mcp_tools`'s inheritance behavior, (4) whether a non-`mainAgent` custom agent is genuinely selectable via `--agent <name>` (D5's live check), (5) the `FLASH_LITE` spelling if discoverable. | n/a | Evidence recorded in `docs/todo/antigravity-capability-verification-evidence.md`, dated and versioned against `agy 1.2.7`. Any contradiction of the inferred mapping is flagged inline and triggers the D3 reopen-to-Draft clause before C1 proceeds. |
| C1 | `tests/KyberWeave.Tests/AntigravityRendererContractTests.cs`, `tests/KyberWeave.Tests/SquadSharedIdentityProjectionTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~AntigravityRendererContractTests\|FullyQualifiedName~SquadSharedIdentityProjectionTests"` | Native dual emission (agent.md + SKILL.md, no `role-` prefix, no lowering) for every canonical agent/skill including the seven distinct-body-collision names; directory-per-agent shape at both scopes; frontmatter key set and value domains per C0's confirmed mapping; D4's XOR narrowing pinned against `documentation` by name at the capability level (`process.execute` genuinely absent, per C0); against `investigator` and `reviewer` by name at the tool-name level (`write_to_file`/`replace_file_content`/`multi_replace_file_content` absent from `tools:`) plus a `capability-not-isolable` degradation record naming the residual `filesystem.write` reachability through `run_command` (D10, via the shared helper from DS2); `grep_search`/`replace_file_content`/`multi_replace_file_content`/`search_web`/`read_url_content` withheld from every profile this pass pending live validation (D12); delegation emitted per D12's delegate rule (`invoke_subagent` + `enable_subagent_tools: true` for any non-empty `delegates-to` roster, plus `manage_subagents` for `orchestrator`/`conductor`, plus a reused `permission-not-expressible` record for the unenforceable roster) rather than withheld; `conductor`'s `mainAgent: true` per D5; `SquadRendererRegistry`'s `isNative` set includes Antigravity and its native-validation branch (no `role-` files, single projection) applies to it; `SquadDeploymentPlan.IdentityFromRelativePath` resolves a bare `agent.md` to its parent directory name. | C1 authors these assertions against the current fallback `AntigravityRenderer` and records the failing run (every native-shape assertion fails against today's `.agents/skills/role-*` output). | C2 makes this same file's assertions pass without weakening them. |
| C3 | New test in `tests/KyberWeave.Tests/SquadDeploymentPlanTests.cs` (or the nearest existing lifecycle-state test file — confirm exact file during the task) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~ReceiptDiff"` (name TBD by test-dev to match the file's existing convention) | A receipt built from Antigravity's *old* fallback output (`.agents/skills/<name>/SKILL.md` for a lowered agent), fed through `SquadDeploymentPlan.CreateUpdate` against the *new* native render (`.agents/agents/<name>/agent.md`), plans a `Delete` mutation for the orphaned skill path when its on-disk bytes still match the receipt digest, and retains it untouched when locally edited. | *(No RED/GREEN pairing — this is a regression-pinning characterization test for behavior that already exists per `SquadDeploymentPlan.cs:356-386`, cited in §2. It is expected to pass on first run, immediately, with no production-code change.)* | Passes immediately against unmodified `SquadDeploymentPlan.cs`, proving defect 6's premise (existing behavior already handles the shape change) rather than leaving it asserted but unverified. |

> **C1/C2 revised (2026-09-21, D3 gate resolved; approved 2026-09-22).** C0's evidence contradicted D4's
> `investigator`-shaped claim; D10-D12 (§3) resolve it. C1 now authors the `capability-not-isolable`
> assertion (against `investigator` and `reviewer`), the D12 tool-exclusion assertions, and the
> D12-delegate assertions, instead of the original unconditional "no path to" claim. Approved 2026-09-22
> for execution.

### Track D — cross-target `capability-not-isolable` degradation (D11, added 2026-09-21)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| DS1 | New `tests/KyberWeave.Tests/CapabilityDegradationsTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~CapabilityDegradationsTests"` | A shared helper, given a target token, the granted shell tool name(s), and the withheld write tool name(s), returns a `SquadDegradationRecord` with `Code: "capability-not-isolable"` naming both, when `process.execute: allow` and `filesystem.write` is `ask`/`deny`; returns `null` when `filesystem.write: allow` or `process.execute` is not `allow`. | Authored against a helper that does not exist yet; fails to compile/resolve, recorded as the RED evidence. | n/a (see DS2). |
| DS2 | *(implementation task; DS1 is its Test contract)* | *(same command as DS1)* | Same as DS1. | n/a | DS1 passes; `docs/kyber-squad/requirements.md` gains a `capability-not-isolable` Degradation Taxonomy row, worded generically (not Antigravity-specific); `docs/todo/shell-implies-write-live-verification-other-targets.md` is created. |
| DCL1/DCL2 | `tests/KyberWeave.Tests/ClaudeRendererContractTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~ClaudeRendererContractTests"` | `investigator`- and `reviewer`-profile agents get a `capability-not-isolable` record naming `Bash`/`PowerShell` and the withheld `Edit`/`Write`/`NotebookEdit`; `documentation`/`worker`/`architect` etc. do not. | DCL1 authors the assertion against unmodified `ClaudeRenderer` and records the failing run. | DCL2 wires `ClaudeRenderer`'s degradation builder to the shared helper; DCL1 passes unmodified. |
| DZC1/DZC2 | `tests/KyberWeave.Tests/ZCodeRendererContractTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~ZCodeRendererContractTests"` | Same shape as Claude, naming `Bash` and the withheld `Edit`/`Write`. | Same pattern as DCL1. | Same pattern as DCL2. |
| DFA1/DFA2 | `tests/KyberWeave.Tests/FactoryRendererContractTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~FactoryRendererContractTests"` | Same shape, naming `Execute` and the withheld `Create`/`Edit`/`ApplyPatch`. | Same pattern as DCL1. | Same pattern as DCL2. |
| DOC1/DOC2 | `tests/KyberWeave.Tests/OpenCodeRendererContractTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~OpenCodeRendererContractTests"` | Same shape, naming `bash` and the withheld `edit`. | Same pattern as DCL1. | Same pattern as DCL2. |
| DPI1/DPI2 | `tests/KyberWeave.Tests/PiRendererContractTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~PiRendererContractTests"` | Same shape, naming `bash` and the withheld `edit`/`write`. Sequenced after A2 (§9, §10): `PiRenderer.cs` is mid-edit, uncommitted, under review. | Same pattern as DCL1, authored once A2 has landed. | Same pattern as DCL2. |

---

## 9. Task list

| # | Track | Phase | Owner skill | Depends on |
|---|---|---|---|---|
| A1 | A | RED | `test-dev` | — |
| A2 | A | GREEN | `csharp-dev` | A1 |
| C0 | C | Investigation | `csharp-dev` (shell access) | — |
| DS1 | D | RED | `test-dev` | — |
| DS2 | D | GREEN | `csharp-dev` | DS1 |
| C1 | C | RED | `test-dev` | C0, DS1 |
| C2 | C | GREEN | `csharp-dev` | C1, A2, DS2 |
| C3 | C | Regression test | `test-dev` | C2 |
| C4 | C | ADR | `app-docs-standard`, `architecture-decision-record` | C2, DS2 |
| C5 | C | Docs alignment | `app-docs-standard` | C2, C4 |
| DCL1 | D | RED | `test-dev` | DS2 |
| DCL2 | D | GREEN | `csharp-dev` | DCL1 |
| DZC1 | D | RED | `test-dev` | DS2 |
| DZC2 | D | GREEN | `csharp-dev` | DZC1 |
| DFA1 | D | RED | `test-dev` | DS2 |
| DFA2 | D | GREEN | `csharp-dev` | DFA1 |
| DOC1 | D | RED | `test-dev` | DS2 |
| DOC2 | D | GREEN | `csharp-dev` | DOC1 |
| DPI1 | D | RED | `test-dev` | DS2, A2 |
| DPI2 | D | GREEN | `csharp-dev` | DPI1 |
| G1 | all | Gate suite | `csharp-dev`, `test-dev` | A2, C3, C5, DCL2, DZC2, DFA2, DOC2, DPI2 |
| G2 | all | Closeout | `app-docs-standard` | G1 |

### A1 — Author failing Pi thinking-level contract tests
**Files:** `tests/KyberWeave.Tests/PiRendererContractTests.cs`.
**Objective:** Add assertions (see §8 Track A row) against unmodified `ResolvePiModel`/`models.yml`;
record the failing run.
**Acceptance:** Every new assertion fails for the correct reason (no suffix syntax parsed today);
no existing test in the file is weakened or deleted.

### A2 — Implement the Pi thinking-level parser and land `models.yml` together
**Files:** `src/KyberWeave.Core/Squad/Rendering/PiRenderer.cs` (`ResolvePiModel` and its caller
`RenderSubagentAgent`, lines 275-361), `products/kyber-squad/profiles/models.yml` (five `pi:`
values per D2), `PiRenderer.cs:42-50` remarks.
**Objective:** Split `[thinking=<level>]`, validate against the six-value domain
(`StringComparison.Ordinal`), fail closed via `SquadRenderValidationException` on an invalid
level, emit `thinking:` only when present. Both halves land in this one task (defect 2).
**Acceptance:** A1's test file passes unmodified; `dotnet build KyberWeave.sln -c Release --no-restore`
is warning-clean (`TreatWarningsAsErrors`).

### C0 — Live-verify the Antigravity capability mapping (D3 gate)
**Files:** none in `src/`; produces `docs/todo/antigravity-capability-verification-evidence.md`
(new).
**Objective:** Run `agy --version` (confirm 1.2.7 still installed), `agy agent`/`agy agents`, and
a live `agy --agent <role>` session in a throwaway scratch workspace (PROJECT-scope discovery at
`<scratch>/.agents/agents/<role>/agent.md`) for at least one non-`mainAgent` custom agent copied
from the backup corpus at `~/.gemini/config/agents.bak.20260918/`, to confirm or contradict: the
seven-capability tool mapping table (verified spec, "The tool vocabulary"); the `model`/`reasoning_effort`
enum domains; `enable_mcp_tools`'s inheritance behavior; D5's selectability question; the `FLASH_LITE`
spelling if discoverable via `strings -a ~/.local/bin/agy`. Every `agy` invocation must pass an explicit
`--model` for Gemini Flash. Do not write, copy, or modify anything under `~/.gemini/config/`.
**Acceptance:** The evidence file exists, is dated, cites the exact commands run and their output,
and explicitly states agreement or contradiction for each of the five items above. A contradiction
triggers the D3 clause (C1's affected rows reopen; plan returns to Draft) rather than being
silently absorbed into C1.

### DS1 — Author failing shared-helper tests for `capability-not-isolable` (D10, D11)
**Files:** new `tests/KyberWeave.Tests/CapabilityDegradationsTests.cs`.
**Objective:** Author the assertions in §8's Track D DS1 row against a helper that does not yet
exist. Record the failing (non-compiling / unresolved) run.
**Acceptance:** The test file exists and fails for the correct reason (no such helper).

### DS2 — Implement the shared `capability-not-isolable` helper (D10, D11)
**Files:** new `src/KyberWeave.Core/Squad/Rendering/CapabilityDegradations.cs`,
`docs/kyber-squad/requirements.md` (new Degradation Taxonomy row, worded generically — not
Antigravity-specific, since six renderers use it), new
`docs/todo/shell-implies-write-live-verification-other-targets.md` (records that the
Claude/Pi/ZCode/Factory/OpenCode records are structurally, not live, evidenced; D12).
**Objective:** Implement the helper: given a target token, the granted shell tool name(s), and
the withheld write tool name(s), return a `SquadDegradationRecord` (`Code:
"capability-not-isolable"`) when `process.execute: allow` and `filesystem.write` is `ask`/`deny`;
`null` otherwise.
**Acceptance:** DS1 passes unmodified; `docs validate .` passes on the new taxonomy row.

### C1 — Author failing Antigravity native-renderer contract tests
**Files:** `tests/KyberWeave.Tests/AntigravityRendererContractTests.cs`,
`tests/KyberWeave.Tests/SquadSharedIdentityProjectionTests.cs`, and (verify only, no edit expected
per §2) `tests/KyberWeave.Tests/PiSquadLifecycleTests.cs:60-79`.
**Objective:** Author the assertions in §8's C1 row against C0's confirmed mapping and this
plan's owner-approved decisions (D5, D7, D8, D9, D10, D11, D12 — §3); rewrite
`AntigravityFallbackReusesSharedIdentityAsExactlyOneUnprefixedSkill` to reflect native shared-identity
suppression (a shared identity still skips its redundant skill projection, but no `role-` prefixing
survives for any *other* collision, per the "Native Both" pattern in §5) — rename it if the old
name is now misleading. Use DS1's shared-helper contract for the `capability-not-isolable`
assertion's exact shape (Code, and the tool names it names) rather than inventing a
parallel wording. Record the failing run against the unmodified fallback renderer.
**Acceptance:** Every native-shape assertion fails for the correct reason; `PiSquadLifecycleTests.cs`
is confirmed still passing (disjoint-path invariant unaffected) or its confirmation is recorded as
a no-op verification, not silently skipped.

### C2 — Implement the native Antigravity renderer
**Files:** `src/KyberWeave.Core/Squad/Rendering/AntigravityRenderer.cs` (rewrite to the "Native
Both" pattern — drop `ResolveOccupiedIdentities`/`ResolveSharedIdentities`'s collision-lowering
role, keep shared-identity skill suppression, add native agent.md emission with D4's XOR
narrowing (as revised by D10) and D5's `mainAgent`/`enable_subagent_tools` handling, generalized
per D12-delegate to every non-empty `delegates-to` roster), `SquadRendererRegistry.cs`
(`isNative` list add `SquadTarget.Antigravity` at line 164-166; `AgentOutputPath`'s Antigravity
branch at lines 384-385 changes from a skill path to
`$".agents/agents/{agent.Name}/agent.md"`), `SquadDeploymentPlan.cs`'s `IdentityFromRelativePath`
(lines 531-546, new `agent.md`-parent-directory branch), `SquadGlobalRoots.cs` remarks (lines 22,
40 — correct "no agent primitive"), `products/kyber-squad/profiles/models.yml` (`antigravity:`
column per D9), `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs:62-77`
doc comment.
**Objective:** Make C1's test file pass without weakening any assertion. Call DS2's shared
`capability-not-isolable` helper for the `investigator`/`reviewer` XOR case rather than a
bespoke Antigravity-only implementation.
**Acceptance:** C1 passes; `dotnet build` and `dotnet format … --verify-no-changes` are clean.
**Dependency note:** depends on A2 in addition to C1, because both tasks edit
`products/kyber-squad/profiles/models.yml` (different keys, same profile blocks) — sequencing
avoids a concurrent-write race on one file; it is not a data dependency. Also depends on DS2 for
the shared helper it calls.

### C3 — Pin the receipt-diff shape-change behavior (defect 6)
**Files:** new test per §8's C3 row.
**Objective:** Prove `SquadDeploymentPlan.CreateUpdate` already retires the old
`.agents/skills/<name>/SKILL.md` path when the render moves to
`.agents/agents/<name>/agent.md`, with no production-code change.
**Acceptance:** Passes immediately; if it does not, that is new information contradicting §2's
citation and the plan returns to Draft to address it rather than patching the test to fit.

### C4 — ADR
**Files:** new `docs/adr/00NN-antigravity-native-agents.md` (next available number after 0020),
`docs/adr/README.md` inventory row.
**Objective:** Record the fallback→native reclassification, the directory-per-agent shape, the
`enable_write_tools` XOR narrowing rule (as revised by D10), the live-deployment migration
consequence, and — per D11 — the cross-target `capability-not-isolable` finding: that
`process.execute`-grants-a-shell already exists on Claude/Pi/ZCode/Factory/OpenCode, why
Codex/Kilo/Cursor/Warp were found unaffected, and why Copilot is excluded (§3 D11, §5); following
ADR 0019/0021's structure.
**Acceptance:** `docs validate .` passes; the ADR's `Related` section links this plan and the two
source todos.
**Dependency note:** depends on DS2 in addition to C2, since it documents the shared helper DS2
implements.

### C5 — Documentation alignment
**Files:** `docs/kyber-squad/architecture.md` (§3 lines 124-137, rendering table lines 279-299,
coverage summary lines 371-375), `docs/kyber-squad/requirements.md` (lines 49, 65-74),
`docs/kyber-squad/onboarding.md` (lines 63, 71, 77-78, 118-125, and the global-root table row at
line 171 — add the `agents/<name>/agent.md` column entry), `docs/todo/kyber-squad-renderer-coverage.md`
(lines 15, 26, 40 — move Antigravity from "Fallback (role-skill lowering)" to "Native").
**Objective:** Every location cited in §2 that currently documents Antigravity as fallback is
corrected to native, with C4's ADR linked where applicable.
**Acceptance:** `docs validate .` and `docs drift .` both pass.

### DCL1/DCL2 — Claude: wire the shared `capability-not-isolable` helper (D11)
**Files:** `tests/KyberWeave.Tests/ClaudeRendererContractTests.cs` (DCL1);
`src/KyberWeave.Core/Squad/Rendering/ClaudeRenderer.cs` (DCL2, its degradation builder calling
DS2's helper with `Bash`/`PowerShell` as the granted shell tools and `Edit`/`Write`/`NotebookEdit`
as the write tools).
**Objective:** Per §8's DCL1/DCL2 row: RED authors the assertion against unmodified
`ClaudeRenderer`; GREEN wires the helper and makes it pass.
**Acceptance:** DCL1 fails for the correct reason before DCL2; passes unmodified after.

### DZC1/DZC2 — ZCode: wire the shared `capability-not-isolable` helper (D11)
**Files:** `tests/KyberWeave.Tests/ZCodeRendererContractTests.cs` (DZC1);
`src/KyberWeave.Core/Squad/Rendering/ZCodeRenderer.cs` (DZC2, `Bash` vs. `Edit`/`Write`).
**Objective/Acceptance:** Same pattern as DCL1/DCL2, for ZCode's tool names.

### DFA1/DFA2 — Factory: wire the shared `capability-not-isolable` helper (D11)
**Files:** `tests/KyberWeave.Tests/FactoryRendererContractTests.cs` (DFA1);
`src/KyberWeave.Core/Squad/Rendering/FactoryRenderer.cs` (DFA2, `Execute` vs.
`Create`/`Edit`/`ApplyPatch`).
**Objective/Acceptance:** Same pattern as DCL1/DCL2, for Factory's tool names.

### DOC1/DOC2 — OpenCode: wire the shared `capability-not-isolable` helper (D11)
**Files:** `tests/KyberWeave.Tests/OpenCodeRendererContractTests.cs` (DOC1);
`src/KyberWeave.Core/Squad/Rendering/OpenCodeRenderer.cs` (DOC2, `bash` vs. `edit`).
**Objective/Acceptance:** Same pattern as DCL1/DCL2, for OpenCode's permission names.

### DPI1/DPI2 — Pi: wire the shared `capability-not-isolable` helper (D11)
**Files:** `tests/KyberWeave.Tests/PiRendererContractTests.cs` (DPI1);
`src/KyberWeave.Core/Squad/Rendering/PiRenderer.cs` (DPI2, `bash` vs. `edit`/`write`).
**Objective/Acceptance:** Same pattern as DCL1/DCL2, for Pi's tool names.
**Sequencing:** `PiRenderer.cs` is mid-edit by A2 (uncommitted, under review). DPI1/DPI2 do not
start until A2 has landed, to avoid a concurrent-edit race on the same file (§10).

### G1 — Declared gate suite
**Command:** `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`.
**Acceptance:** Zero findings blocking merge; `artifacts/gates.json` attached to the run record.

### G2 — Closeout
**Owner skill:** `app-docs-standard`.
**Objective:** Harvest durable facts from this plan into canonical docs (already done in C5;
this task verifies nothing was missed), sync `docs/plans/README.md`'s Active/Archived tables,
and archive this plan per the standard lifecycle. Archiving the two source todos
(`black-hawk-hotel-todo.md`, `antigravity-native-agents.md`) and the C0 evidence document is the
conductor's post-Ready promotion step, not this task (defect 5) — G2 confirms that step ran, it
does not perform it.

---

## 10. Dependency graph and `MAX_CONCURRENCY`

```text
A1 ──► A2 ──────────────────────────────────┐
                                             ├──► C2 ──► C3 ──┐
C0 ─────────────────────► C1 ───────────────┘         ┌──► C4 ──► C5 ──► G1 ──► G2
DS1 ──► DS2 ──┬─────────► C1                           │
              ├──► DCL1 ──► DCL2 ───────────────────────┤
              ├──► DZC1 ──► DZC2 ───────────────────────┤
              ├──► DFA1 ──► DFA2 ───────────────────────┤
              ├──► DOC1 ──► DOC2 ───────────────────────┤
              └──► DPI1 ──► DPI2 ───────────────────────┘
A2 ──────────────────────► DPI1 (sequencing only, not a data dependency)
```

Level 1: `{A1, C0, DS1}` (3). Level 2: `{A2, DS2}` (2). Level 3: `{C1, DCL1, DZC1, DFA1, DOC1,
DPI1}` (6 — DPI1 additionally waits on A2, already satisfied at this level). Level 4: `{C2, DCL2,
DZC2, DFA2, DOC2, DPI2}` (6). Level 5: `{C3, C4}` (2). Level 6: `{C5}` (1). Level 7: `{G1}` (1).
Level 8: `{G2}` (1).

**`MAX_CONCURRENCY: 2`** (unchanged). Levels 3-4 now exceed the cap in width; the scheduler
queues the excess under the same limit rather than running them in parallel — this plan does not
raise the cap, since Track D's five renderer pairs are independent, low-risk, mechanical edits
that do not need to run concurrently to stay on schedule.

---

## 11. Risks and out-of-scope boundaries

- **Live global deployment migration.** The owner's machine carries a 1017-file, nine-target
  global receipt including Antigravity (`black-hawk-hotel-todo.md`, "The machine state this work
  left behind"). Per §2's defect-6 citation, `squad update --global --target antigravity` (or a
  full `--global` update) after this ships will automatically retire the old
  `.agents/skills/role-*`/`.agents/skills/<name>` agent-lowered files and write the new
  `.agents/agents/<name>/agent.md` files, with no manual file surgery — this is existing,
  citation-backed, now regression-tested (C3) behavior, not a new risk this plan introduces.
  Running that update is the owner's live-state action after merge; it is out of scope as a plan
  task (§6).
- **D3's contradiction path fired (2026-09-21), not hypothetically.** C0 ran; its evidence
  confirmed D5's selectability premise but contradicted D4's `investigator`-shaped narrowing
  claim: `process.execute: allow` retains file-write capability through the shell even when
  `filesystem.write` tools are withheld by name. D10-D12 (§3) resolve it, and the owner directed
  a cross-target fix (D11) rather than the architect's recommended Antigravity-only scope,
  adding Track D (§8, §9) to this revision. This plan stays Ready; the reopened C1/C2 rows and
  the new Track D tasks were approved 2026-09-22 for execution.

---

## 12. Verification

- The declared gate suite (G1, §9).
- `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build` (full suite,
  not just the filtered subsets used per-task).
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- squad install --target
  antigravity --dry-run` reports the new directory-per-agent shape.
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and
  `… docs drift .` at zero findings (required after every docs/ edit per `AGENTS.md`).

---

## 13. Execution status and handover (as of 2026-09-22)

Run state recorded by the conductor so another agent can resume cold. Decisions live in §3/§4;
this section holds only what has been executed, what is owed, and how the run was operated.

**Where the work is.** Worktree `/Users/dave/git/claude-worktree/kyber-weave/zcode-harness-subagents-e2fff4`,
branch `claude/zcode-harness-subagents-e2fff4`, on top of `1dca55b1`. **Nothing from this run is
committed or pushed.** Uncommitted: this plan (new); `docs/plans/README.md`; `docs/todo/README.md`;
`docs/todo/black-hawk-hotel-todo.md` and `docs/todo/antigravity-native-agents.md` (both now
`status: superseded`, pointing at this plan); `docs/todo/antigravity-capability-verification-evidence.md`
(new, C0); `products/kyber-squad/profiles/models.yml`; `src/KyberWeave.Core/Squad/Rendering/PiRenderer.cs`;
`tests/KyberWeave.Tests/PiRendererContractTests.cs`; `tests/KyberWeave.Tests/SquadSourceTests.cs`.
Per the execution contract, commit and push wait for `code-reviewer` to return `APPROVE`.

**Task status.**

| Task | Status | Evidence and notes |
|---|---|---|
| A1 | Done — `task-reviewer` pass 1 PASS | 18 new cases in `PiRendererContractTests.cs`. RED run: 15 failed for the intended reason (suffix passed through verbatim, no `thinking` key, no exception), 3 passed (behavior that already held), all 13 pre-existing tests passed. Reviewer note for the end-of-run council: two near-identical fixture helpers (`PiModelOverrideFixture` / `PiThinkingSuffixFixture` each carry `ReplaceOrInsertPiValue` and `CopyDirectory`). |
| A2 | Done — `task-reviewer` pass 1 PASS (2026-09-22), no fixes. Track A is complete apart from the end-of-run council | `PiRenderer.cs` (+108/−5): trailing `[thinking=<level>]` split, Ordinal six-level validation, fail-closed `SquadRenderValidationException` naming the level, `thinking:` only when present, remarks updated. `models.yml`: the four D2 `pi:` suffixes. Two pre-existing tests that pinned pre-D2 behavior were aligned to D2 exactly, not loosened: `SquadSourceTests.ModelsYmlDeclaresExactHarnessValuesPerProfilePerPlan` (four `pi` rows) and `PiRendererContractTests.RenderAsync_Pi_EachSubagentAgentHasCanonicalFrontmatterAndBody` (key order with a conditional `thinking`). Current tree: `dotnet build KyberWeave.sln -c Release` 0 warnings; full suite 1995 passed, 0 failed; `dotnet format KyberWeave.sln whitespace --verify-no-changes` and `style --verify-no-changes --severity warn`, scoped with `--include` to `PiRenderer.cs`, `SquadSourceTests.cs` and `PiRendererContractTests.cs`, both exit 0 (2026-09-22). |
| C0 | Done — `task-reviewer` pass 3 PASS | [Evidence](../todo/antigravity-capability-verification-evidence.md). Two earlier attempts failed review (model narration taken as proof, no load proof, sessions run in `--mode plan` outside the workspace). The plan's named owner (`csharp-dev`) then declined the task as outside its role; the owner reassigned the final attempt to a general-purpose agent. Its findings produced D10-D12. |
| DS1 | Done — `task-reviewer` pass 2 PASS (2026-09-22) | `tests/KyberWeave.Tests/CapabilityDegradationsTests.cs` (new): 20 cases across 7 methods pinning `BuildCapabilityNotIsolable(targetToken, canonicalIdentity, outputIdentity, instructionDigest, executeDecision, writeDecision, grantedShellTools, withheldWriteTools)`. Pass 1 FAILed: the first signature carried no identities or digest, so no valid per-agent `SquadDegradationRecord` could be built; the rework added them with exact-value assertions and made the no-granted-shell case return null per D10. RED was the accepted compile failure (CS0103 only). |
| DS2 | Done — `task-reviewer` pass 2 PASS (2026-09-22) | Code: `src/KyberWeave.Core/Squad/Rendering/CapabilityDegradations.cs` (new) — all 20 DS1 cases pass, build 0 warnings, both verify-only format gates exit 0, full suite 2015 passed / 0 failed. Docs: the `capability-not-isolable` Degradation Taxonomy row in `docs/kyber-squad/requirements.md` and the new `docs/todo/shell-implies-write-live-verification-other-targets.md` with its index row; `docs validate .` and `docs drift .` at 0 findings. Pass 1 FAILed on the taxonomy row (incomplete tool mappings, an unsupported "listed in the receipt" claim); a wrong deep-link anchor was fixed too. Note for the council: the code worker's digest claimed a green full suite its own log contradicted (see the artifacts-path rule below). |
| C1 | In progress (2026-09-22) | Authoring the failing native-Antigravity contract tests in `AntigravityRendererContractTests.cs` and `SquadSharedIdentityProjectionTests.cs`. |
| C2-C5, DCL/DZC/DFA/DOC/DPI pairs, G1, G2 | Not started | Approved 2026-09-22. |

**Gate passed 2026-09-22.** Revision 2 — D10-D12 (§3), the reopened C1/C2 rows (§8), and Track D (§8, §9, §10) — was approved for execution 2026-09-22 (recorded in §3's Approval subsection).

**Next actions, in order.**
1. `task-reviewer` pass 1 on A2 — done 2026-09-22, PASS with no fixes.
2. Present the revision-2 approve-and-execute gate — done 2026-09-22, approved.
3. Run the ready queue per §10 at `MAX_CONCURRENCY: 2`. DS1, DS2 and A2 have passed review, so
   the remaining eligible work is C1 (in progress) → C2, then the five Track D pairs (DCL, DZC,
   DFA, DOC, DPI), then C3-C5. Sequence RED/GREEN pairs one at a time — see the compile-state
   rule below.
4. With the queue and findings empty: `code-reviewer` once over the whole run, then G2 closeout
   (`docs-dev`). Archive the two superseded todos only after C5 has harvested
   `antigravity-native-agents.md`, which remains Task C's contract until then. Commit and push
   only after `APPROVE`.

**Operating notes for the next conductor, learned in this run.**
- In Claude Code the conductor has to be the main thread: subagents cannot spawn subagents, so
  the `conductor` agent's `Agent(...)` roster only applies under `claude --agent conductor`.
- `architect` and `product-owner` render with no Write, Edit, or Bash
  ([ask-narrowing todo](../todo/claude-renderer-ask-narrowing.md)). The pattern used throughout:
  the architect returns full content or exact OLD/NEW pairs, `docs-dev` applies them, and the
  conductor byte-compares the result against an independently computed copy with `cmp`, then runs
  `docs validate .` and `docs drift .`. The byte-compare caught two silent `docs-dev`
  mis-applications in this run; keep it.
- Subagents can start in a different worktree of this repository. Every dispatch must give
  absolute paths into this worktree — one reviewer reported the C0 evidence file missing because
  it looked in the wrong tree.
- Build isolation, corrected 2026-09-22. A worker `dotnet build` or a FILTERED `dotnet test` may
  pass `--artifacts-path` to a scratch directory (never a directory inside the worktree;
  concurrent workers otherwise share `bin/` and `obj/`). A FULL-suite `dotnet test` must NOT:
  `KyberWeaveTestPaths.LocateToolRoot` walks up from the test output directory to find
  `KyberWeave.sln`, so a scratch output path fails 306 tests with
  `TypeInitializationException … Could not locate KyberWeave.sln`. Those failures are
  environmental, not regressions — the same tree run from the default output passed 2015/0. A
  worker reported that broken run as a green suite, so check a digest's counts against its own log.
- Compile-state rule: a RED task leaves the test project uncompilable until its GREEN lands, so
  run one RED/GREEN pair at a time. Use the second concurrency slot for work that does not build
  (documentation, review, evidence) rather than a second RED task, or two REDs' failures become
  indistinguishable.
- Forbid workers from reverting, checking out, stashing, or resetting. A `test-dev` worker
  "re-established a baseline" by reverting `PiRendererContractTests.cs` and deleted A1's
  uncommitted, reviewed tests; they were reconstructed from A1's recorded edits. Do not run a
  whole-file `cleanupcode` on files with pre-existing code either — one run rewrote 246
  pre-existing lines and was discarded; use the verify-only CI format gates.
- `agy` probing rules (§3 Approval, still binding): nothing written under `~/.gemini/config/`; a
  throwaway project-scope workspace; `--model gemini-3.8-flash-low`; `-p='<prompt>'`;
  `--mode accept-edits --add-dir <workspace>` (never `--mode plan`, which ran in agy's default cwd
  and resolved a same-named global skill); `timeout 180` per call. agy's per-session SQLite
  records at `~/.gemini/antigravity-cli/conversations/<id>.db` (`steps` table) are the ground truth
  for which tool was actually called; a model's self-reported tool list is not.
- Model tiering: haiku by default. Sonnet was used for plan drafting and revision and for C0's
  final attempt, each after a demonstrated haiku failure on that task.
- Earlier run logs sit in session scratch paths under `/private/tmp/`, which are not durable.
  Re-run evidence rather than relying on them.
- Cross-stream audit (2026-09-22). PR #96 (`claude/kyberdash-context-surfaces-spec-d5775e`, the
  KyberDash stream) does not duplicate this plan, but it crossed it in two ways. It carries a
  stale 2026-09-18 copy of `docs/todo/antigravity-native-agents.md` (an add/add conflict with this
  branch's copy, which must win), and it added its own ADR 0020
  (`0020-kyberdash-one-time-fork.md`) beside this branch's ZCode record, then numbered 0020 too. A
  note covering both, plus a diverged kyberdash remote, was handed to the session working on PR #96.
  **Resolved 2026-09-22:** PR #96 merged first (`f7054414`), so `origin/main` owns ADR 0020. This
  branch merged `origin/main` and renumbered its ZCode record to
  [ADR 0021](../adr/0021-zcode-command-lowering-and-resource-relocation.md), updating every
  reference; the Antigravity todo resolved to this branch's superseded 2026-09-21 version, as
  intended. C4's ADR takes the next free number after 0021.

---

## Related

- [Black Hawk Hotel handover](../todo/black-hawk-hotel-todo.md) — the source todo (superseded by
  this plan once the conductor's promotion step runs)
- [Antigravity native agents — verified spec](../todo/antigravity-native-agents.md) — the Task C
  contract (superseded by this plan once the conductor's promotion step runs)
- [ADR 0019](../adr/0019-pi-native-subagents-and-primary-lowering.md) — the fallback→native
  reclassification precedent (Pi)
- [ADR 0021](../adr/0021-zcode-command-lowering-and-resource-relocation.md) — the first-of-its-kind
  rendering-mechanism precedent (ZCode); also the source of the models.yml schema's pre-existing
  `antigravity` key
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — §3 role-skill lowering, §8 rendering
- [Renderer coverage](../todo/kyber-squad-renderer-coverage.md)
- [Antigravity capability verification evidence](../todo/antigravity-capability-verification-evidence.md) — C0's evidence; the source of D10-D12
- [Shell-implies-write live verification for other targets](../todo/shell-implies-write-live-verification-other-targets.md) — created by DS2 (D11, D12)

---
id: plans/2026-09-26-canon-fixture-consolidation
title: Consolidate the canon suites' CanonicalRecord fixture helpers
doc-type: plan
status: current
owner: dpalfery
last-reviewed: 2026-09-26
component: KyberDash
development-mode: test-first
---

# Consolidate the canon suites' CanonicalRecord fixture helpers

**Status:** Ready
**Approved:** 2026-09-26 — the user's directive relayed by the conductor ("fix issue 129 … use a
plan, dont ask me any questions, run autonomously. push the pr, once the CI pipelines run
successfully, ask code rabbit ai to do a code review, fix any comments and ask for a rereview
repeat loop till code rabbit approves the pr") is an explicit, unconditional approval of the
approve-and-execute gate in advance, and delegates every technical decision to this plan. No
separate conductor gate is pending; execution may start immediately.
**Date:** 2026-09-26
**Development mode:** test-first (default; the user did not opt out)
**Provenance:** Fix for [GitHub issue #129](https://github.com/dpalfery/kyber-weave/issues/129)
"Canon test suites duplicate their CanonicalRecord fixture helpers" (OPEN, `enhancement`,
`javascript`, no comments). The issue was migrated from a `docs/todo/` seed by main's `#135`
(nine open todos moved to GitHub issues `#125`–`#133`); the issue is the durable record, and
relative links to the former todo file do not survive that migration.
**Goal:** One shared `CanonicalRecord` fixture module for `dash/src/canon/findings.test.ts` and
`dash/src/canon/sessions.test.ts`, so duplicate clusters `dup-7222c0d2` and `dup-3bfb5437`
disappear from the regenerated duplicates report, with zero change to any test assertion —
delivered through a PR that closes #129 and reaches CodeRabbit approval.

---

## 1. Problem / Motivation

The end-of-run review council over the compaction-hazard context-window plan accepted, for that
change only, that the new `findings.test.ts` copied the store fixture helpers of
`sessions.test.ts`. That acceptance is not endorsement in general: the two suites now carry
byte-identical `tokens` and `turn` builders, so a `CanonicalRecord` shape change must be applied
in every copy and the copies can drift. No behaviour is wrong today — this is test-fixture
hygiene, exactly as scoped by issue #129.

The duplicates gate tracks the copies as two clusters spanning exactly the two files:

- `dup-7222c0d2` — `tokens`, 8 normalized lines: `findings.test.ts:19-27` and
  `sessions.test.ts:87-95`.
- `dup-3bfb5437` — `turn`, 18 normalized lines: `findings.test.ts:29-49` and
  `sessions.test.ts:97-117`.

## 2. Investigation findings

| Fact | Evidence |
|---|---|
| Issue #129 is OPEN, enhancement + javascript, zero comments, and matches this work | `gh issue view 129 --repo dpalfery/kyber-weave` 2026-09-26 |
| The two helper pairs are byte-identical (same bodies, same signatures, same defaults) | `dash/src/canon/findings.test.ts:19-49` vs `dash/src/canon/sessions.test.ts:87-117` |
| Both clusters span exactly those two files; no other file is a member | `artifacts/duplicates.json` read 2026-09-26: `dup-7222c0d2`, `dup-3bfb5437`, 56 clusters at HEAD `9a2eea7`, `minimumLines: 4` (`.kyber-weave/kyber-weave.yml` `review.duplicates`). Amended 2026-09-26: the planning-time read reported 58 against a CodeGraph index predating the `#117` test-suite rewrites; the T2 post-fix regeneration established the true pre-fix count at HEAD is 56 |
| `measurability.test.ts` `turn` is a different builder over `ContextTurn` (from `../analysis/context.js`), different signature (single `overrides` arg), member of neither cluster | `dash/src/canon/measurability.test.ts:90` |
| Other canon suites carry single-copy local `CanonicalRecord` builders with suite-specific shapes, none clustered with the two reported helpers | `store.test.ts:43`, `runs.test.ts:38`, `outcome.test.ts:46`, `retention.test.ts:34`, `split-identity.test.ts:20,32`, `types.test.ts:27`, `log-ingest.test.ts:17` |
| The dash suite's shared-fixture convention is a per-suite `fixtures/` directory, and a TS helper module there is established precedent | `dash/src/canon/fixtures/asad-session-shape.json`; `dash/src/refresh/fixtures/integration-harness.ts` (TS module, imported by `refresh.integration.test.ts`) |
| `__fixtures__/` (double underscore) is a different convention: adapter JSON payloads, excluded from coverage by vitest config | `dash/src/canon/adapters/__fixtures__/`; `dash/vitest.config.ts` `coverage.exclude: ['**/__fixtures__/**']` |
| A non-test TS module under `src/` is not collected as a test (include is `src/**/*.{test,spec}.?(c|m)[jt]s?(x)`) but is counted by coverage (include `src/**/*.{ts,tsx}`); it is pure object builders fully exercised by the two suites | `dash/vitest.config.ts:13,40-41` |
| `check:reachable` runs a second pass whose entries are the test files and which reports test-support files no test reaches — the new module is reached by two suites, so it passes and is gated | `dash/scripts/unreachable.mjs` header; `dash/package.json` `check:reachable` |
| The duplicates gate is `kyber-weave review duplicates . --out artifacts/duplicates.json`, declared blocking in review.gates; it exits 0 whether or not clusters exist (clusters are Warning evidence; only a failed `--out` write fails), so acceptance is asserting cluster ids absent from a regenerated report | `src/KyberWeave.Cli/Commands/Review/ReviewDuplicatesCommand.cs:14-19,55-68,86-89`; `.kyber-weave/kyber-weave.yml:82-84` |
| `artifacts/` is gitignored — the duplicates report is local/CI evidence, never a committed artifact | `.gitignore:17` |
| A CodeGraph index exists at the repo root and may lag the working tree; the command warns `KW-REVIEW-032` on unreadable symbols and `KW-REVIEW-030` when no index is read | `.codegraph/` present; `ReviewDuplicatesCommand.cs:42-53,70-78` |
| No governed document besides the todo and issue describes the canon test fixtures; nothing else in `docs/` references the cluster ids | `docs_explore` + grep over `docs/` (excluding archive) 2026-09-26 |
| The finishing PR must archive this plan and its index row (CI `docs validate --merge-ready` fails on any plan still in `docs/plans/`) | `docs/plans/README.md` KW-DOC-LIFECYCLE-003 |
| CI runs `.github/workflows/ci.yml` on PRs; no `.coderabbit.yaml` exists, so CodeRabbit acts through its GitHub App — invoke with an `@coderabbitai review` comment when no review appears on open | `.github/workflows/ci.yml`; repo root listing 2026-09-26 |

## 3. Decisions

All decisions were answered by this plan under the user's standing delegation ("dont ask me any
questions, run autonomously", relayed by the conductor 2026-09-26). Each records the chosen
option and a one-line rationale; none is material enough to change scope destructively, so no
BLOCKED handoff was warranted.

### D1 — Where the shared fixtures live — ANSWERED: `dash/src/canon/fixtures/records.ts`

Extend the canon suite's existing `fixtures/` directory (today holding
`asad-session-shape.json`) with a TS fixture module, mirroring the established
`src/refresh/fixtures/integration-harness.ts` precedent of a TS helper module beside the suites
that consume it. Rejected: `__fixtures__/` (that convention holds adapter JSON payloads and is
coverage-excluded — a code fixture should be visible to coverage, not hidden from it); a
repository-wide `dash/src/test-utils` (no such convention exists; inventing one is broader than
the issue).

### D2 — Scope boundary — ANSWERED: only the two reported helpers in the two reported files

`tokens` and `turn` move to the shared module; `findings.test.ts` and `sessions.test.ts` import
them. The other canon suites' local builders stay: each is single-copy (no cross-file cluster,
no drift risk) and deliberately shaped for its suite's assertions — sweeping them is scope creep
beyond the accepted follow-up and beyond the issue's acceptance check.
`measurability.test.ts`'s `turn` is left alone deliberately: it builds a `ContextTurn` from
`../analysis/context.js` with a different signature and is a member of neither reported cluster
(issue #129 explicitly allows this). `multiHarnessRecords` (`findings.test.ts:225`) stays local
for the same single-copy reason.

### D3 — Export shape — ANSWERED: keep the names and signatures byte-identical

The module exports `tokens(over: Partial<CanonicalRecord['tokens']> = {})` and
`turn(spanId: string, parts: ContentPart[], over: Partial<CanonicalRecord> = {})` with the exact
bodies now duplicated, so the consuming diff is a pure import swap with zero assertion edits.
Renaming or re-shaping would touch every call site for no acceptance value and blur the
observation-preserving property the Test contract depends on.

### D4 — Acceptance command and oracle — ANSWERED: regenerate and assert

Acceptance is `codegraph sync` (so the index reflects the working tree), then
`dotnet run --project src/KyberWeave.Cli -- review duplicates . --out artifacts/duplicates.json`
(gate-identical invocation), asserting cluster ids `dup-7222c0d2` and `dup-3bfb5437` are absent
and the total cluster count is exactly 52 on the `origin/main` (`1ccc761`) base — main baseline
54 measured at `1ccc761` minus the two target clusters, per set-difference evidence: the branch
removes exactly `[dup-3bfb5437, dup-7222c0d2]` and adds none (main baseline retained at
`artifacts/duplicates-main-baseline.json`, gitignored). Re-baselined twice on 2026-09-26, both
times factually and never weakening the guard: D4 first demanded 56 from a planning-time read
of 58 (a stale pre-`#117` index); the count became 54 when the true pre-fix count at HEAD
`9a2eea7` proved to be 56; and 52 after the T5 stage-A rebase, because main's own
`#122`/`#136`/`#139` test work had meanwhile removed two more clusters (main baseline 54, not
56). The guard still asserts both cluster ids absent and an exact count, which is what guards
against the refactor minting a new cluster (for example the shared `tokens` body newly matching
`split-identity.test.ts`'s `usage`). The report is local evidence only (`artifacts/` is
gitignored); CI regenerates it through the blocking duplicates gate regardless.

### D5 — PR terminal condition — ANSWERED: stop at CodeRabbit approval; do not merge

The user's loop ends at "code rabbit approves the pr". Merging is not in the stated loop and is
the one irreversible step (it closes #129), so the plan terminates at: CI green, CodeRabbit
approval posted, PR left open for the owner's merge. The conductor may relay a later merge
instruction; it is not part of this plan.

## 4. Test contract (test-first)

The change is an observation-preserving refactor guarded by tests that already exist; test-first
here means the consuming test files change first (RED: unresolved import), the fixture module
lands second (GREEN: same assertions pass). No new test file is authored — a test that tests a
test helper adds coverage overhead and protects nothing the two suites do not already pin.

| Task | Test surface | Runner | Observable behavior |
|---|---|---|---|
| T1 | `dash/src/canon/findings.test.ts` and `dash/src/canon/sessions.test.ts` (existing suites; only the two local helper definitions become imports from `./fixtures/records.js` — zero assertion edits) | `npm --prefix dash run test` (Vitest) | RED first: with the import swapped and the module absent, both suites fail to resolve `./fixtures/records.js`. GREEN after the module exists: every previously passing test in both suites passes, unmodified |
| T2 | `artifacts/duplicates.json` (regenerated, gitignored evidence) | `codegraph sync`, then `dotnet run --project src/KyberWeave.Cli -- review duplicates . --out artifacts/duplicates.json` | `dup-7222c0d2` and `dup-3bfb5437` absent; total clusters exactly 52 on the `origin/main` base (main baseline 54 at `1ccc761` minus the two, per the amended D4); no `KW-REVIEW-032` stale-index warning |
| T3 | Verification no-op on the rebased branch (main's `#135` already migrated the todo; nothing to edit — see §5 T3) | `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `docs drift .` | Zero findings in both on the rebased tree (evidence folded into T4/T5) |
| T4 | This plan, archived into the finishing PR | same as T3, plus `docs validate . --merge-ready` | Zero findings — the merge-ready check passes only because the plan left `docs/plans/` |
| T5 | Stage A: the rebased branch; stage B: GitHub PR checks + CodeRabbit review | Stage A: T1 gate set + T2 oracle re-run post-rebase; stage B: `gh pr checks`, `gh pr comment`/`gh pr view` | Stage A: gates green on the `origin/main` base (the stale-base `report-api-parity` failure is main-fixed by `#140`); stage B: every `ci.yml` check reports success, CodeRabbit posts an Approve-level summary, loop re-enters on any non-approve summary |

Supporting gates T1 must also leave green (they are what CI runs): `npm --prefix dash run
typecheck`, `npm --prefix dash run lint`, `npm --prefix dash run check:reachable` (its test pass
proves the new module is reached from tests), and `npm --prefix dash run test` in full.

## 5. Tasks

### T1 — Shared fixture module, test-first

- **Objective:** One definition each of `tokens` and `turn`; both suites consume it.
- **Files/symbols:** new `dash/src/canon/fixtures/records.ts` (exports `tokens`, `turn`;
  imports types from `../types.js`; opens with a why-comment carrying the issue #129
  provenance per house style); `dash/src/canon/findings.test.ts` (delete `tokens` :19-27 and
  `turn` :29-49, import from `./fixtures/records.js`; `multiHarnessRecords` stays);
  `dash/src/canon/sessions.test.ts` (delete `tokens` :87-95 and `turn` :97-117, import from
  `./fixtures/records.js`).
- **Order:** commit the consuming edit first and observe RED per the Test contract, then add
  the module and observe GREEN.
- **Acceptance:** full dash gate set green (`test`, `typecheck`, `lint`, `check:reachable`);
  zero assertion edits in either suite.
- **Dependencies:** none.
- **Required skills:** `test-dev`.

### T2 — Duplicates acceptance (verification only)

- **Objective:** Prove the issue's acceptance check on the working tree.
- **Files/symbols:** none changed; produces `artifacts/duplicates.json` (gitignored).
- **Acceptance:** D4's oracle holds (`dup-7222c0d2`/`dup-3bfb5437` absent, 52 clusters on the
  rebased tree, no stale-index warning). If a new cluster appears involving
  `fixtures/records.ts`, stop and re-scope per D4's guard before any push.
- **Dependencies:** T1.
- **Required skills:** `test-dev` (runs the CLI gate; may need `dotnet restore KyberWeave.sln`
  first; `codegraph` resolves at `~/.local/bin/codegraph` in agent subshells).

### T3 — Close the migrated todo — collapsed to a verification no-op

Amended 2026-09-26, after discovering local HEAD `9a2eea7` sat 7 commits behind `origin/main`:
main's `#135` ("docs(todo): migrate open todos to GitHub issues #125-#133") already closed this
loop by deleting the open todos — no archive copies, no per-todo Closed rows; the todo README
carries one migration note instead. T3's original edits (an archive copy under
`docs/archive/todo/` plus Open/Closed row changes in `docs/todo/README.md`) are superseded by
that deliberate deletion-as-migration choice — re-adding them would semantic-conflict with
main. The record of this todo's closure is issue #129's closure by the PR.

- **Objective:** Verify nothing docs-side remains to do for the seed todo.
- **Content:** on the rebased branch (T5 stage A), the local T3 edits are dropped during
  conflict resolution; nothing replaces them. The working tree at amendment time still carries
  the superseded edits (archive copy + todo README changes) — that is expected; they are
  discarded at the rebase, not fixed before it.
- **Acceptance:** `docs validate .` and `docs drift .` zero-findings on the rebased tree
  (evidence folded into T4/T5).
- **Dependencies:** T2, T5 stage A.
- **Required skills:** `app-docs-standard` (only if a finding demands an edit).

### T4 — Archive this plan into the finishing PR

- **Objective:** The finishing PR carries the plan's own archival, as KW-DOC-LIFECYCLE-003
  requires.
- **Files/symbols:** move this document to
  `docs/archive/plans/2026-09-26-canon-fixture-consolidation.md` (frontmatter `id` becomes the
  archive path, `status: archived`, matching the archived-plan convention); append a Closeout
  section recording gate results and the CodeRabbit outcome — and, per the 2026-09-26
  amendments, the D4 count re-baselinings (oracle now 52 on the `origin/main` base), the T3
  collapse under main's `#135`, and the rebase onto `origin/main`; move its row in `docs/plans/README.md` from Active to the
  Archived table with harvest targets ("None — no governed document describes canon test
  fixtures (plan §2); D1–D5 are recorded in the plan itself").
- **Acceptance:** `docs validate . --merge-ready` zero-findings on the PR branch.
- **Dependencies:** T2, T3, T5 stage A (closeout records their results; archival lands on the
  rebased branch).
- **Required skills:** `app-docs-standard`.

### T5 — Branch, rebase, PR, CI, CodeRabbit loop

- **Objective:** Deliver the change as a PR closing #129 on current main, through green CI to
  CodeRabbit approval.
- **Steps — stage A, rebase (added by the 2026-09-26 amendment; must complete before T4):**
  branch `fix/129-canon-fixture-consolidation` from local HEAD `9a2eea7` and commit T1's code
  change; `git fetch origin`, then rebase onto `origin/main` (7 commits ahead; local main is a
  strict ancestor, so no unique history is lost). Prescribed conflict resolutions:
  `docs/plans/README.md` — keep this plan's Active-Plans row over main's "No active plans."
  paragraph, taking main's `last-reviewed: 2026-09-26`; `docs/todo/README.md` and
  `docs/archive/todo/canon-test-fixture-consolidation.md` — drop the local T3 edits entirely
  (superseded by `#135`; see T3). No code conflicts are expected: `dash/src/canon/` is
  untouched between `9a2eea7` and `origin/main` (verified by diff). After the rebase, re-run
  the T1 gate set (`test`, `typecheck`, `lint`, `check:reachable`) and the T2 oracle on the
  rebased tree before T4.
- **Steps — stage B, PR and review loop:** after T4's archival commit, push; open the PR (title
  "Consolidate canon CanonicalRecord test fixtures", body with `Fixes #129`, linking the issue
  and this plan); wait for `gh pr checks` all green — the `report-api-parity.test.ts` failure
  observed on the stale local base is that test's fixed `2026-09-19` dates aging out of the
  `--days 7` window, already fixed on main by `#140` (`1ccc761`, dates anchored to now), so a
  rebased branch starts green; if CodeRabbit has not reviewed on open, comment
  `@coderabbitai review`; address every CodeRabbit finding as a new commit that re-runs the T1
  gate set (and T2 if code moved), then request a re-review (`@coderabbitai review` again or
  reply-and-resolve per its UI); repeat until CodeRabbit posts an Approve-level summary.
  Terminate there per D5 — do not merge.
- **Acceptance:** stage A — post-rebase T1 gate set and T2 oracle green on the `origin/main`
  base; stage B — `gh pr checks` all success, CodeRabbit final summary an approval, the PR left
  open.
- **Dependencies:** T2 (stage A); T4 (stage B).
- **Required skills:** `create-pull-request-github`, `github-cli`.

## 6. Dependency graph and concurrency

```
T1 (test-dev) ──► T2 (test-dev) ──► T5 stage A: rebase (github-devops)
                                    ──► T3 (no-op verify) ──► T4 (docs-dev)
                                    ──► T5 stage B: PR / CI / CodeRabbit (github-devops)
```

`MAX_CONCURRENCY: 1` (amended 2026-09-26: T3 collapsed from a parallel docs edit into a
verification no-op, removing the only parallel pair). T4 needs T2's oracle, T3's clean
verification, and the rebased base; stage B needs the archived plan in its commit.

## 7. Risks and mitigations

- **Stale CodeGraph index reports the pre-refactor tree.** Mitigated: T2 syncs before the gate;
  the command itself warns `KW-REVIEW-032` if indexed symbols no longer match the tree, and the
  T2 oracle treats that warning as failure.
- **The refactor mints a new cluster** (shared body newly matching another suite's builder).
  Mitigated: D4's exact-count oracle (52 on the `origin/main` base) fails loudly on any new
  cluster, before any push.
- **Coverage floor distortion from the new module.** Not a risk: the floor figure comes only
  from .NET cobertura reports; the dash report is listed but cannot displace it, and the module
  is pure builders fully exercised by both suites.
- **CodeRabbit findings request scope beyond this plan.** Any finding that would widen D2's
  scope gets a reply declining it with the issue-link rationale (or, if the user accepts it
  during the loop, a new todo per the todo discipline — not silent scope creep in this PR).

## 8. Out of scope

- Consolidating any other canon suite's local fixture builders (D2).
- Touching `measurability.test.ts`'s `ContextTurn` builder (D2).
- Any production-code change under `dash/src/canon/` (`types.ts`, `store.ts`, `findings.ts`,
  `sessions.ts` are the seam's context, not its targets).
- Merging the PR (D5).
- A `.coderabbit.yaml` review configuration (none exists; introducing one is a workflow decision
  the user has not asked for).

## 9. Verification gates (summary)

Local, before push: the full T1 dash gate set; T2's duplicates oracle; `docs validate .`,
`docs drift .`, and `docs validate . --merge-ready` after T3/T4. CI replays the declared gate
suite through `.github/workflows/ci.yml`, including the blocking duplicates and ts-test gates.
The PR-level exit criterion is T5's: green checks plus CodeRabbit approval.

## 10. Review and closeout

The end-of-change review is the repo's own council over the PR diff plus the CodeRabbit loop
(T5) — the plan's review requirement is satisfied by reaching T5's terminal condition, recorded
in the Closeout appended by T4. `docs-dev` closeout is T4 itself (T3 collapsed to a
verification no-op by the 2026-09-26 amendment); no canonical document harvest is expected
(plan §2), so the archived plan remains the record of D1–D5.

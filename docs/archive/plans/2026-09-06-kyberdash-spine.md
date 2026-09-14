---
id: archive/plans/2026-09-06-kyberdash-spine
title: KyberDash Diagnostic Spine — State, Work, Dispatch
doc-type: plan
status: archived
owner: dpalfery
last-reviewed: 2026-09-13
component: KyberDash
---

# KyberDash Diagnostic Spine — State, Work, Dispatch

**Status:** Complete (Archived 2026-09-13)
**Archive Date:** 2026-09-13
**Date:** 2026-09-06
**Goal:** Get the six-level diagnostic hierarchy into the running dashboard without burning another ten agent-hours on unreachable components.

## Implementation evidence (2026-09-13)

Harvest of D17–D22 into [architecture](../../dash/architecture.md) and [runbook](../../dash/runbook.md). ADRs 0012–0015 unchanged except ADR 0014 status note that `kyber purge-content` was not shipped. Q3 retention is [ADR 0018](../../adr/0018-kyberdash-content-retention-purge.md).

| Decision | In-tree |
|---|---|
| D17 Sessions, not Context tab | `dash/dash/src/pages/Sessions.tsx`; `nav-rail-sessions`; `page-sessions` |
| D18 content + 14-day purge | `dash/kyber/canon/retention.ts`; called from `refreshHarnessSources` |
| D19 labelled derived runs | `run-grouping-basis` on RunDetail and HarnessDetail |
| D20 Compare in spine rail | `nav-rail-compare`; Compare removed from `NAV_TABS` |
| D21 sidebar spine rail; Share on Usage | `App.tsx` `section === 'usage'` Share block |
| D22 Tailwind density | `@theme` density tokens; compact spine pages |

Phase A/B remain PASS. PD7 calibration UI is mounted on Finding detail. PD8 boundary allowlist includes `refresh/**`.

**Not claimed:** pipeline T8 live-source coverage; findings API `harness` query (client-side filter in `fetchFindings`).

**Execution checkpoint (2026-09-06):** A real local host passed G1–G7 (7/7): the
dashboard lands on Attention, drills through the spine, retains one harness selector,
and renders unavailable metrics and empty charts honestly. T7 resolved Q1 below.

**Harness-filter regression checkpoint (2026-09-06):** Follow-up browser gate G4a
verifies the one selector exposes canonical Claude Code, GitHub Copilot, and Gemini
choices, and proves a Claude Code selection receives a successful
`runs?harness=claude-code` response and drills into a run.

**Phase B implementation checkpoint (2026-09-12):** Implemented and **task-reviewed
PASS**. End-of-run council **APPROVE** 2026-09-12 after `dist-sea/**` ESLint ignore.
Not a new ADR; ADRs 0012–0015 remain the harvest. Do **not** re-dispatch B1–B4,
B4-api, DC-1, DC-4, G1–G7, or G4a.

**Finalize checkpoint (2026-09-12):** User refused further Q2–Q7 round-trips. Ledger
recommended answers are Approved decisions **D17–D22**. Dispatch remaining exclusive
tasks below. No third plan file.

Consolidates and replaces three earlier drafts (the 09-05 diagnostic-hierarchy plan, the 09-06 spine-structure plan, the 09-06 execution playbook). Interaction reference: `KyberDash.dc.html` is **not in this repository** — density mapped onto Tailwind is the goal (D22), not a pixel copy of a missing file.

Task prompts (Phase A only): [`tasks/`](../../plans/tasks/). Gate: [`tasks/spine.spec.ts`](../../plans/tasks/spine.spec.ts) copied to `dash/e2e/spine.spec.ts`. Playwright `data-testid` contract: [`tasks/README.md`](../../plans/tasks/README.md). Phase A `npx --prefix dash playwright test e2e/spine.spec.ts` **must stay green**.

---

## 1. Problem / Motivation

Phase A made the six-level spine clickable. Phase B closed the four diagnostic questions on the landing path. Remaining work is look-and-feel, wiring parked session UI now that Q2 is answered, content retention (after pipeline T2), labelled derived-run surfacing (mostly present), folding Compare into the spine rail, and calibration/merge-zone tasks that must not rebuild engines.

The 09-05 screenshot claim ("five pages have no importer") is **false on 2026-09-12**. Do not re-implement the walking skeleton.

## 2. Approved decisions

- **D1 — A task is done when a headless browser, driving the real dev server against the real store, observes the change.** Not when a test passes. Not when a file exists. No exceptions. Live host: `npm --prefix dash run dev` against `~/.kyberdash/canon.db`.
- **D2 — Slice vertically.** The first slice is one harness → one run → one turn, *reachable*, using whatever components exist, however ugly.
- **D3 — Context is replaced, not extended.** A session list cannot be relabelled into `Run` and `AgentExecution`.
- **D4 — `Attention` is the landing surface.** Usage survives as a tab; it does not open the app.
- **D5 — One harness selector, in the shell.** It becomes the harness level of the spine.
- **D6 — A missing counter renders as an em-dash with a reason, never `0`.**
- **D7 — A chart with no data does not render.** (Approved-decision D7 is **empty charts**, not Phase-D task D7 calibration.)
- **D8 — Every level answers the four questions or does not ship.** (Approved-decision D8 is **four questions**, not Phase-D task D8 merge-zone.)
- **D9 — Fabricated data never renders as measurement.**
- **D10 — Decision ids never appear in product UI.**
- **D11 — No composite efficiency score, ever.** Six independent dimensions; unmeasurable is a dash.
- **D12 — Recommend, do not act.**
- **D13 — Recommendations prefer relocating context to deleting it.**
- **D14 — This plan is Phase B execution authority.** The 09-05 diagnostic-hierarchy plan is superseded. User authorized execution despite earlier `needs-review` frontmatter.
- **D15 — Dead UI is wired onto the live spine or deleted.** Prefer wiring when the symbol is a real spine level (Attention, Harness, Run, Execution, Turn, Finding, Compare). Delete true orphans. The Q2-park exception is **closed by D17**: `ContextExplorer` / `AgentSessionDashboard` / `TimelineView` must be reachable as Sessions or they violate D15.
- **D16 — Do not rebuild Phase D engines that already exist.** `dash/kyber/canon/runs.ts`, `harnesses.ts`, `findings.ts`, `store.ts` (`listRuns` / `listHarnessRollups` / `listFindings`), `dash/kyber/analysis/findings.ts`, `dash/kyber/analysis/compare.ts` (`compareRuns`), `dash/kyber/analysis/calibration.ts`, and `dash/kyber/server/bridge.ts` already serve HTTP + some UI — **wire, do not rebuild**.
- **D17 — Sessions hedge (Q2).** Keep as **Sessions**. Do **not** restore a Context tab. Wire `ContextExplorer` / `AgentSessionDashboard` as a Sessions surface reachable from the shell. `TimelineView` rides with them.
- **D18 — Store content + 14-day purge (Q3).** Persist content; purge after 14 days. Author a new ADR later (docs-dev, not pipeline T9). Do not invent hashes-only. **Pipeline T2 owns `store.ts` first.** Q3 is a later additive job in disjoint files (`dash/kyber/canon/retention.ts`), not a second rewrite of the checkpoint migration.
- **D19 — Labelled derived cluster, never silent (Q4).** `groupingBasis` already exists — do **not** rebuild E1 / `runs.ts`. Surface the label in Run UI (Harness list + Run header).
- **D20 — Compare folds into the spine (Q5).** Relocate live `CompareRuns` off being only a peer nav tab into the diagnostic spine. Quarantine and Problems stay tabs (they are not spine levels).
- **D21 — Sidebar is the spine rail (Q6).** Share moves to Usage. One task owns `App.tsx` for D20+D21 together.
- **D22 — Map onto Tailwind (Q7).** Density is the goal, not pixel-copy of missing `KyberDash.dc.html`.

## 2a. Open questions (decision ledger)

No OPEN rows. Deferred documentation (Q3 ADR, spine harvest) is §6.

| Q# | Question | Options | Recommended | Depends on | Status |
|----|----------|---------|-------------|------------|--------|
| Q1 | Antigravity token counts | — | Producer-emitted zeroes in `canon.db` | — | ANSWERED: producer zeros → T7 |
| Q2 | What happens to the Context tab? | (a) delete (c) keep as Sessions | (c) Sessions hedge | — | ANSWERED: Sessions; wire explorer → D17 |
| Q3 | Store context content vs hashes | Content + 14-day purge vs hashes | Content + purge; ADR later | inspector; pipeline T2 | ANSWERED: content + 14-day purge → D18 |
| Q4 | Derived run boundaries | Explicit vs labelled derived cluster | Labelled derived, never silent | E1 exists | ANSWERED: labelled derived; do not rebuild E1 → D19 |
| Q5 | Compare / Quarantine / Problems | Fold Compare into spine vs keep tabs | Compare folds into spine | T8 historical | ANSWERED: Compare into spine; Q/P stay tabs → D20 |
| Q6 | Sidebar as spine rail | Yes / no | Yes; Share moves to Usage | shell | ANSWERED: sidebar spine rail → D21 |
| Q7 | Pixel fidelity vs Tailwind map | Desktop fork vs tokens | Map onto Tailwind | Phase C | ANSWERED: Tailwind density → D22 |

## 3. Investigation findings

**Docs MCP:** `docs_explore` returned a corpus miss (0 documents considered) for plan / KyberDash queries. Findings below are **self-gathered** (2026-09-12 finalize) from this plan, the refresh-pipeline plan, [`docs/dash/architecture.md`](../../dash/architecture.md), ADR 0006 / 0012–0016, CodeGraph, and targeted reads.

### 3.1 Phase B still done (do not re-dispatch)

B1 findings-first, B2 `scorecard-matrix`, B3 Turn inspector, B4-api `GET /api/kyber/compare/runs`, B4 live `CompareRuns`, DC-1/DC-4 deletes — task-reviewed PASS 2026-09-12. Walking skeleton remains `dash/dash/src/App.tsx`.

### 3.2 Already implemented leftovers (do not rebuild)

| Item | Evidence | Disposition |
|------|----------|-------------|
| Runs / rollups / findings / `compareRuns` | Consumed by HTTP + UI (§3.5 of prior checkpoint) | D16 — no E1–F3 rebuild |
| `groupingBasis` on `RunRow` | `dash/kyber/canon/types.ts`; stored in `run` | Do not rebuild E1 |
| Derived label in Run UI | `RunDetail.tsx` badge `derived run` / `explicit run`; `HarnessDetail.tsx` Grouping Basis column | Q4 **mostly done** — Q4 task is testid + D1 browser proof, not a new engine |
| Calibration **engine** | `dash/kyber/analysis/calibration.ts`, store `getCalibrationSummary`, tests `dash/tests/calibration.test.ts` | Do not rewrite |
| Calibration **HTTP** | `GET /api/kyber/calibration` in `dash/kyber/server/routes.ts` (~375); architecture table already lists it | Do not add a second route |
| Calibration **UI** | **Missing** — no `fetchCalibration` under `dash/dash/` | PD7 wires a reachable surface |
| Merge-zone **mechanical tests** | `dash/kyber/tools/boundary.test.ts` (`scanMergeZoneBoundaries` must be empty); `tests/KyberWeave.Tests/MergeBoundaryTests.cs` (no KyberDash leak into `dash/src/`) | PD8 is **verify**, not a new suite |
| `SCHEMA_VERSION` | Already **10** in `store.ts` (plan text that said "bump from 9" is stale) | Pipeline T2 owns the next additive bump |
| `dash/kyber/refresh/**` | **Does not exist** | Pipeline T1+ creates it |
| `dash refresh` | Still sequential `refreshLocalProviders` + public `--provider` | Pipeline T0–T10; ADR 0016 decided, **not shipped** |
| Share in sidebar | `App.tsx` Share block is always visible (~960) | Q6 moves it onto Usage |
| Compare peer tab | `NAV_TABS` includes Compare; `showSpine` already includes `section === 'compare'` | Q5 removes Compare as a peer tab; keep spine level `compare` |
| `ContextExplorer` | No `App.tsx` importer; tests only | D17 — must become reachable |
| `fetchKyberSessions` | Already in `kyberApi.ts` | Q2 reuses; do not duplicate |
| G4a Gemini button | `e2e/spine.spec.ts` requires a visible **Gemini** harness-selector button | Must stay green. Pipeline T6 forbids harness id `gemini` in **canonical rows**. Do not "fix" G4a by deleting the button unless inventory proves it is the same identity as a stored `gemini` harness. |

### 3.3 Merge-zone write rules for remaining work

New UI under `dash/dash/src/components/kyber/` or `pages/`. `App.tsx` is upstream-conflict surface — **exactly one task (Q5Q6)** may edit it. Do not give `App.tsx` to Phase C, Q2, PD7, or pipeline T8 unless T8's hard-coded-alias exception fires (then T8 still must not take `App.tsx` if Q5Q6 owns it — wait until Q5Q6 lands).

No `dash/src/**` edits. Unrelated dirty frontend/docs must not be reverted.

## 4. Task list

Acceptance for every UI task is a **browser path** against `npm --prefix dash run dev` and live `~/.kyberdash/canon.db` (D1). Named `data-testid`s from [`tasks/README.md`](../../plans/tasks/README.md) plus the ids below — do not rename Phase A ids.

| Testid | On |
|--------|-----|
| `page-attention` / `page-harness` / `page-run` / `page-execution` / `page-turn` / `page-finding` | each level's root |
| `drill-harness-<id>` / `drill-run-<id>` / `drill-execution-<id>` / `drill-turn-<n>` / `drill-finding-<id>` | clickable descent |
| `breadcrumb-attention` … `breadcrumb-turn` | breadcrumb segments |
| `harness-selector` | the single shell harness selector |
| `metric-<key>` + `data-measured="true\|false"` | metric cards |
| `chart-<key>` + `data-empty="true\|false"` | charts |
| `context-band-<key>` / `context-content` | Turn bands and the pane text lands in |
| `finding-list` / `finding-list-empty` / `finding-card-<id>` / `finding-confidence-badge` | findings |
| `scorecard` / `dimension-<key>` / `dimension-unmeasurable` | six-dimension cards |
| `page-compare` / `compare-n-guard` / `scorecard-matrix` | Phase B |
| `run-grouping-basis` | Q4 grouping label |
| `page-sessions` / `nav-rail-sessions` | Q2 Sessions |
| `nav-rail-attention` / `nav-rail-compare` / `nav-rail-usage` | Q5Q6 spine rail |
| `page-calibration` or `calibration-summary` | PD7 |

### Phase B (complete — record only, do not dispatch)

| # | Phase | Component | Description | Skills | Status |
|---|-------|-----------|-------------|--------|--------|
| B1 | B | Findings | First-block ranked findings | `react-dev` | Done — PASS 2026-09-12 |
| B2 | B | Scorecard matrix | Cross-harness matrix | `react-dev` | Done — PASS 2026-09-12 |
| B3 | B | Turn inspector | Inspector on Turn | `react-dev` | Done — PASS 2026-09-12 |
| B4-api | B | Run compare API | Thin `compareRuns` HTTP | backend/TS in `dash/kyber` | Done — PASS 2026-09-12 |
| B4 | B | Run compare UI | Live `CompareRuns` | `react-dev` | Done — PASS 2026-09-12 |
| DC-1 | B | Orphans | Delete ContextView/SchemaView | `react-dev` | Done — PASS 2026-09-12 |
| DC-4 | B | Compare leftover | Delete CompareView panel | `react-dev` | Done — PASS 2026-09-12 |

### Remaining exclusive tasks

| # | Phase | Component | Description | Skills | Status |
|---|-------|-----------|-------------|--------|--------|
| Q4 | D19 | Grouping label | Testid + D1 proof of existing labelled derived/explicit badges | `react-dev`, `playwright` | Done |
| PD7 | Phase D task D7 | Calibration surface | Wire existing `/api/kyber/calibration`; do not rebuild engine | `react-dev`, `playwright` | Done |
| PD8 | Phase D task D8 | Merge-zone tests | Re-run existing mechanical tests; add assertion only if a gap is proven | `typescript` | Done (allowlist includes `refresh/**`) |
| Q2 | D17 | Sessions | Reachable Sessions wrapping explorer + dashboard + TimelineView | `react-dev`, `playwright` | Done |
| Q5Q6 | D20+D21 | Shell | Sidebar spine rail; Compare off peer tabs; Share on Usage; mount Sessions | `react-dev`, `playwright` | Done |
| C-tokens | C / D22 | Tailwind tokens | Density tokens in `@theme`; not a pixel mock | `react-dev` | Done |
| C-density | C / D22 | Spine density | Compact spacing on live spine pages after shell/sessions/calibration | `react-dev`, `playwright` | Done |
| Q3 | D18 | Retention | 14-day content purge job **after pipeline T2** | backend/TS in `dash/kyber` | Done — `retention.ts` |
| S-docs | Closeout | Spine docs | Harvest D17–D22 after implementation + council; Q3 ADR | `app-docs-standard`, `kyber-weave-docs` | Done 2026-09-13 |

### Q4 — Labelled derived cluster (testid + proof)

- **Objective:** Run UI never presents a derived cluster as an explicit harness run. Labels already exist; this task must not rebuild `runs.ts`.
- **Files / symbols (exclusive):** `dash/dash/src/pages/RunDetail.tsx` (`run-grouping-basis` on the existing badge); `dash/dash/src/pages/HarnessDetail.tsx` (same testid on the Grouping Basis cell). **Do not edit** `dash/kyber/canon/runs.ts`, `App.tsx`, or E1.
- **Skills:** `react-dev`, `playwright`
- **Depends on:** none
- **Acceptance (browser):** Attention → harness → run. `run-grouping-basis` visible on `page-run` (`derived run` or `explicit run`). Harness run table shows grouping basis, never a silent row. G1–G7 / G4a stay green.

### PD7 — Calibration surface (Phase-D **task** D7, not approved-decision D7)

- **Objective:** A reachable UI consumes `GET /api/kyber/calibration`. Empty / not-yet-calibrated is honest copy (`statusMessage`), not a fake curve. **Do not** rewrite `calibration.ts`, routes, or `getCalibrationSummary`.
- **Files / symbols (exclusive):** `dash/dash/src/lib/kyberApi.ts` (`fetchCalibration` only — additive); new `dash/dash/src/components/kyber/CalibrationSummary.tsx`; mount on `dash/dash/src/pages/FindingDetail.tsx` (`calibration-summary`). **Do not edit** `App.tsx`, `calibration.ts`, `routes.ts`, `bridge.ts`, `store.ts`.
- **Skills:** `react-dev`, `playwright`
- **Depends on:** none
- **Acceptance (browser):** Open a finding (`page-finding`). `calibration-summary` shows calibrated or not-yet-calibrated from the live API. `<5` scored pairs does not render a lying 100% chart (D7 empty-chart decision still applies). No product copy of "D7" / "PD7".

### PD8 — Mechanical merge-zone (Phase-D **task** D8, not approved-decision D8)

- **Objective:** Prove ADR 0006 still holds. **Do not** invent a second engine or rewrite `boundary.ts` unless a failing assertion shows a gap.
- **Files / symbols (exclusive):** read-only unless a gap: `dash/kyber/tools/boundary.test.ts` (`scanMergeZoneBoundaries` length 0; non-adapter `dash/kyber/**` must not import `dash/src/**`); `tests/KyberWeave.Tests/MergeBoundaryTests.cs` (no KyberDash source leaked into `dash/src/`).
- **Skills:** `typescript` (vitest) + existing C# test runner
- **Depends on:** none
- **Acceptance:** `npx --prefix dash vitest run kyber/tools/boundary.test.ts` passes. `dotnet test` filter `MergeBoundaryTests` passes. If both green with the assertions above, **mark PD8 done with that evidence** and make **no source edit**. If a gap exists, add only the missing assertion in `boundary.test.ts`.

### Q2 — Sessions surface (D17)

- **Objective:** `ContextExplorer` + `AgentSessionDashboard` (+ `TimelineView` via dashboard) are reachable as **Sessions**, not a restored Context tab.
- **Files / symbols (exclusive):** `dash/dash/src/pages/Sessions.tsx` (**new** — wrap `ContextExplorer`); `dash/dash/src/components/ContextExplorer.tsx`; `dash/dash/src/components/AgentSessionDashboard.tsx`; `dash/dash/src/components/kyber/TimelineView.tsx` only if a testid is required. Reuse `fetchKyberSessions`. **Do not edit `App.tsx`** (Q5Q6 mounts `nav-rail-sessions` → `<Sessions />`).
- **Skills:** `react-dev`, `playwright`
- **Depends on:** none (page can land before the rail)
- **Acceptance (browser):** After Q5Q6 merge: shell → Sessions → `page-sessions`. Opening a session renders `AgentSessionDashboard` (timeline present). No `nav-tab-context`. G1 still lands on Attention.

### Q5Q6 — Sidebar spine rail + Compare-into-spine + Share-on-Usage (D20+D21)

- **Objective:** Sidebar is the spine rail (Attention, Sessions, Compare as rail destinations; Usage remains a tab plus Share). Compare is not only a peer `NAV_TABS` entry. Quarantine and Problems stay header tabs. **Sole owner of `App.tsx`.**
- **Files / symbols (exclusive):** `dash/dash/src/App.tsx` (`NAV_TABS`, `showSpine`, `openSpine`, sidebar `SideLink`s, Share block moved under `section === 'usage'`, mount `<Sessions />`); `dash/dash/src/App.test.tsx` (tab/rail contract). **Do not edit** explorer files, `CompareRuns.tsx`, or `index.css`.
- **Skills:** `react-dev`, `playwright`
- **Depends on:** Q2 (`pages/Sessions.tsx` must exist to import)
- **Acceptance (browser):** Header `nav-tabs` has Attention, Usage, Quarantine, Problems — **not** Compare as a fifth peer (update `App.test.tsx`). Sidebar `nav-rail-attention` / `nav-rail-compare` / `nav-rail-sessions`. Compare rail → `page-compare` with live `CompareRuns`. Share controls visible on Usage, not on Attention. `harness-selector` count remains 1 (G4). G1–G7 / G4a green.

### C-tokens — Tailwind density tokens (D22)

- **Objective:** Map density onto existing Tailwind `@theme` — tighter type, gaps, and chrome. Not a fork of a missing HTML mock.
- **Files / symbols (exclusive):** `dash/dash/src/index.css` (`@theme inline` and related tokens only).
- **Skills:** `react-dev`
- **Depends on:** none
- **Acceptance:** Tokens compile (`npm --prefix dash run typecheck`). Visual proof is C-density. Do not edit `App.tsx`.

### C-density — Apply density on live spine surfaces

- **Objective:** Compact the shipped spine pages using C-tokens. No new information architecture.
- **Files / symbols (exclusive):** `dash/dash/src/pages/Attention.tsx`; `dash/dash/src/components/kyber/FindingList.tsx`; `dash/dash/src/components/kyber/Scorecard.tsx`; `dash/dash/src/components/kyber/ScorecardMatrix.tsx`; `dash/dash/src/pages/TurnDetail.tsx`. **After** Q4/PD7/Q5Q6 so RunDetail/FindingDetail/App are not dual-owned — if those pages still look loose, C-density may touch them **only after** those tasks merge. **Do not edit `App.tsx`.**
- **Skills:** `react-dev`, `playwright`
- **Depends on:** C-tokens, Q4, PD7, Q5Q6
- **Acceptance (browser):** Attention at 1280px: findings + matrix remain first diagnostic blocks without extra empty-card chrome. G1–G7 / G4a green. Contrast still readable (no grey-on-grey collapse).

### Q3 — Content + 14-day purge (D18)

- **Objective:** Keep stored content; purge content older than 14 days. No hashes-only. No `store.ts` rewrite racing T2.
- **Files / symbols (exclusive):** new `dash/kyber/canon/retention.ts`; `dash/kyber/canon/retention.test.ts`. May call existing `CanonStore` accessors. **Do not edit `store.ts` or `source-state.ts`.** If a schema bump is truly required, wait until T2 has merged, then a one-line `MIGRATIONS` follow-up owned by this task only — prefer metadata + `UPDATE`/`NULL` content columns with no migration.
- **Skills:** backend/TS in `dash/kyber`
- **Depends on:** **pipeline T2** (serialize). Disjoint from T2 files.
- **Acceptance:** Focused test: content newer than 14 days remains; older `content_json` is purged; `records.raw` is not deleted (ADR 0008). No ADR in this task (docs-dev later).

### S-docs — Spine closeout (do not start in this window)

Pipeline T9 must not harvest spine D17–D22. After remaining spine tasks + code-review + council, a docs-dev updates architecture/runbook and authors the Q3 content-retention ADR. **Do not archive this plan until that evidence exists.**

## 5. Sequencing / dependency graph

```
Q4 grouping testids          ──┐
PD7 calibration UI           ──┤  immediate (disjoint files)
Q2 Sessions page             ──┤
PD8 verify existing tests    ──┤
C-tokens index.css           ──┘
pipeline T0                  (parallel; other plan)
        Q2 ──► Q5Q6 App.tsx (sole App owner)
        Q4, PD7, Q5Q6, C-tokens ──► C-density
pipeline T2 ──► Q3 retention
Q5Q6 ──► pipeline T8 (dashboard Playwright / temp DB)
remaining spine + council ──► S-docs (later)
```

Phase A `e2e/spine.spec.ts` after every UI merge.

Pipeline T8: production UI edits only if a hard-coded alias blocks filters; **forbid `App.tsx`** (owned by Q5Q6). Sequence T8 after Q5Q6.

## 6. Residual decisions / risks

- **G4a vs Gemini-never-a-harness.** Selector label "Gemini" may remain. Canonical rows must not use harness id `gemini`.
- **T8 live-source coverage.** Fixture Playwright is not installed-root smoke.
- **Findings client-side harness filter.** `GET /api/kyber/findings` has no `harness` query; HarnessDetail filters in `fetchFindings`.
- **Empty calibration** is honest; do not seed predictions.
- **Phantom completion.** Unit tests still cannot close a D1 UI task.
- **Dirty worktree.** Unrelated frontend/docs must not be reverted.

## 7. Out of scope

- Re-dispatch Phase A or Phase B.
- Rebuild D1–D6 engines (`runs.ts`, `harnesses.ts`, `findings.ts`, `compareRuns`).
- Refresh button / UI (refresh-pipeline plan).
- Editing `dash/src/**`.
- Archiving this plan before implementation evidence (closed 2026-09-13).
- Electron / TUI / menubar parity.
- URL routing / shareable spine.

## 8. Required skills

- `react-dev` — Q2, Q4, Q5Q6, C-tokens, C-density, PD7
- `playwright` — browser acceptance on those UI tasks + keep `e2e/spine.spec.ts` green
- backend/TS in `dash/kyber` — Q3 only
- `typescript` — PD8 verify
- `app-docs-standard`, `kyber-weave-docs` — S-docs later, not now

Do not map skills to agents here.

## 9. Verification harness

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run dev            # live canon.db
npx --prefix dash playwright test e2e/spine.spec.ts
npx --prefix dash vitest run kyber/tools/boundary.test.ts
```

Per-task browser paths are in §4. Completion report must answer **what did you open, and what did you click**.

- **Phase A:** G1–G7 + G4a still green; Usage / Quarantine / Problems still render.
- **Phase B:** do not re-dispatch.
- **Review:** repository code-review on each remaining diff. Security review if Q3 changes the content-retention contract.
- **Docs:** `docs validate .` and `docs drift .` after this plan edit. Spine harvest is S-docs later.

## 10. Dead-code inventory (task map)

| Symbol | File | Verdict | Task |
|--------|------|---------|------|
| `CompareRuns` | `pages/CompareRuns.tsx` | Wired (PASS 2026-09-12); relocate into spine rail | Q5Q6 |
| `ContextExplorer` | `components/ContextExplorer.tsx` | Must be reachable Sessions | Q2 |
| `AgentSessionDashboard` | `components/AgentSessionDashboard.tsx` | Rides with Sessions | Q2 |
| `TimelineView` | `components/kyber/TimelineView.tsx` | Rides with dashboard | Q2 |
| Calibration engine/API | `calibration.ts` / routes | Wired backend; UI missing | PD7 |
| Merge-zone tests | `boundary.test.ts` / `MergeBoundaryTests.cs` | Exist | PD8 verify |
| `groupingBasis` badges | `RunDetail` / `HarnessDetail` | Present | Q4 testids |

## 11. Phase A (historical)

Prompts remain in [`tasks/`](../../plans/tasks/). Do not re-run T8 as if pages were unreachable.

## 12. Phase C — look and feel

Assigned as C-tokens + C-density. Palette fidelity is explicitly not the goal; **density is**.

## 13. Phase D — engines

D1–D6 as originally specified **already have in-tree implementations**. Remaining: **task** D7 = PD7 (wire calibration UI); **task** D8 = PD8 (existing mechanical tests). Do not open a new backend engine rewrite.

## 14. Related

- [`tasks/`](../../plans/tasks/) — Phase A dispatch prompts and the Playwright gate
- [Refresh pipeline plan](2026-09-06-kyberdash-refresh-pipeline.md) — archived with residual T8 live-source / findings-filter risks
- 09-05 diagnostic-hierarchy plan — **superseded**; not execution authority (archived 2026-09-12)
- [KyberDash architecture](../../dash/architecture.md), [runbook](../../dash/runbook.md), [telemetry inventory](../../dash/telemetry-inventory.md)
- [ADR 0006](../../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) — merge zone
- [ADR 0011](../../adr/0011-asad-only-context-view-and-payload-contract.md) — revisited by D17 (Sessions rail, ASAD still the only session view)
- [ADR 0012](../../adr/0012-progressive-disclosure-6-level-diagnostic-spine.md)–[0015](../../adr/0015-opt-in-llm-context-review-seam.md)
- [ADR 0016](../../adr/0016-kyberdash-harness-source-refresh.md) — shipped checkpointed refresh
- [ADR 0018](../../adr/0018-kyberdash-content-retention-purge.md) — Q3 content retention

---
id: plans/2026-11-24-sessions-list-column-headers
title: Sessions list column headers and a plain "out of scope" explanation
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-11-24
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - KyberDash
---

# Sessions list column headers and a plain "out of scope" explanation

**Status: Ready, 2026-11-25.** Task T1 complete (commits d3c8a173b, dd5a4c06b, 0c56d9acb); closeout mapping below.

## Request

> What does out of scope mean in the sessions list? And we should have a column header on that table so that we understand what each piece is.

## Problem and goal

The KyberDash web dashboard's Sessions list (`ContextExplorer` in `dash/web/src/components/ContextExplorer.tsx`) renders each session as a flex strip of unlabeled cells: session id, title, harness badge, turn count, cost, and how long ago it started. Nothing names the columns, so a word such as "out of scope" in the cost cell has no visible meaning.

**Answer to the question.** "out of scope" is a cost status, not an error. When a harness reports its own cost, KyberDash shows that figure. Otherwise the engine estimates cost only from a price table that covers the harness: the published LiteLLM rates cover the Claude and Codex harnesses, and the Copilot credits table covers the Copilot harnesses. A session from any other harness that did not report its own cost has no table to price it, so its status is `out_of_scope` and KyberDash shows those words instead of a made-up figure or `$0.00` (`CanonPricing.PricePublishedTurn` and `CanonPricing.IsPublishedTableHarness` in `dash/server/KyberDash.Domain/CanonPricing.cs`; [architecture: CostBlock](../dash/architecture.md#costblock-and-cost-basis); requirement KD-005).

**Goal.** The list gets one header row that names each column and lines up with the cells beneath it, and the cost cell's hover text explains every word status in plain language, using the same explanation the session view's cost tile shows, so "out of scope" explains itself where it appears.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| "What does out of scope mean in the sessions list?" | Problem and goal (the answer, relayed to the owner in the run summary); T1 (cost cell hover text and cost tile explanation say it in plain words) | T1 tests: the `out_of_scope` cost cell title contains `STATUS_EXPLANATIONS.out_of_scope`; the cost tile renders the same sentence |
| "we should have a column header on that table so that we understand what each piece is" | T1 (`SessionListHeader` above the rows, sharing `SESSION_LIST_COLUMNS` widths with `AgentSessionRow`) | T1 tests: header with Session, Title, Harness, Turns, Cost, Started renders once in the data state, never in loading, error or empty states, and each header cell carries the same width classes as the row cell beneath it |

## Scale

`extends-component` on KyberDash: a display-only change to the web dashboard's Sessions list and cost tile. No engine, API, store or data-model change.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Convert the list to a semantic `<table>`, or add a header strip to the existing flex rows? | Add a header strip (`SessionListHeader`) whose cells use the same width classes as the row cells, from one shared `SESSION_LIST_COLUMNS` constant. | A `<table>` cannot hold the row's sibling buttons (subagent toggle, row button, parent link) and the expanded session dashboard without a rewrite of every row and its tests; the shared constant keeps header and cells from drifting apart. The header is a plain `div` of visible text labels with no table or grid ARIA roles, because the rows are not a grid; screen readers read the labels as text before the rows, and every row control keeps its current accessible name. | ANSWERED — autonomous: derived (React standard: semantic HTML first, ARIA only where semantics cannot express it) |
| D2 | Which column labels? | Session, Title, Harness, Turns, Cost, Started. | They name what each cell already shows: the short id, the session title, the harness badge, the turn count, the cost figure or status words, and the `started` time rendered as "5m ago". Cell contents keep their current text (for example "10 turns"). | ANSWERED — autonomous: low-confidence, no authoritative source (wording only; cheap to change) |
| D3 | Rows do not line up today: the id and harness cells vary in width, and only rows with subagents carry the leading toggle. How do header and rows align? | Session id cell gets a fixed width with truncation (`w-36 truncate`, full id stays in its `title`); the harness badge sits in a fixed-width cell (`w-32`); a root row without subagents renders an `aria-hidden` spacer the width of the toggle (`data-testid="subagent-toggle-spacer"`). | Without fixed widths a header cannot sit over its column. The spacer fixes an existing misalignment in code this task already edits. | ANSWERED — autonomous: low-confidence, no authoritative source (widths local to one component) |
| D4 | When does the header show? | Only in the data state with at least one row, for both the fetched and the embedded (`sessions` prop) lists; never with loading, error or empty states. | The React standard keeps loading, error, empty and data states distinct; a header over "No sessions found." labels nothing. | ANSWERED — autonomous: derived |
| D5 | How does "out of scope" explain itself in the list? | The cost cell's `title` for a word status becomes `Cost basis: <basis> — <explanation>`; a priced cell keeps `Cost basis: <basis>`, and a rolled-up figure keeps `Cost including subagents`. | Hover text needs no new layout or control; the existing test that the title names the basis still holds. | ANSWERED — autonomous: derived |
| D6 | Where does the explanation text live? | One exported map `STATUS_EXPLANATIONS: Record<Exclude<CostStatus, 'priced'>, string>` in `SessionCostPanel.tsx` beside `STATUS_WORDS`; the cost tile's inline strings and the list's cell title both read it. | Today the sentences are inline JSX in the cost tile only; a second copy in the list would drift. Static wording is display text, not business logic, so it stays in the web layer (react rules). | ANSWERED — autonomous: derived |
| D7 | Keep the word "out of scope", or rename the status? | Keep the words; rewrite only the explanation, from "Harness is outside rate table applicability." to "No KyberDash price table covers this harness, so its cost is not estimated." | The words are documented in requirement KD-005, the architecture CostBlock section and the runbook, and asserted by tests; the owner asked what they mean, not for a rename. The old sentence was the jargon. | ANSWERED — autonomous: derived |
| D8 | Child rows show a trailing "parent:" link outside the row button, which shifts their right-hand cells left. Fix in this plan? | No: the header aligns with root rows; child rows sit indented under their parent as a tree level. Left as a PR note. | Moving the link inside the row would nest a button in a button; removing it changes navigation an existing test (`AgentSessionRow parent navigation`) covers. Neither was asked for. | ANSWERED — autonomous: derived |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute (docs validate blocker is the stale installed CLI 0.1.8-rc.30.da2a9a71; a CLI built from main 2f661f462 reports no findings), 2026-11-25 |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| KyberDash | architecture | `docs/dash/architecture.md` › Costblock And Cost Basis › Display | The Sessions list has a column header (Session, Title, Harness, Turns, Cost, Started) sharing widths with the row cells; a word-status cost cell's hover text names the basis and the plain explanation from `STATUS_EXPLANATIONS`, the same text the cost tile shows. |
| KyberDash | runbook | `docs/dash/runbook.md` › cost status troubleshooting ("Other statuses") | "out of scope" in plain words: no KyberDash price table (published LiteLLM rates for Claude and Codex harnesses, Copilot credits for Copilot harnesses) covers the harness, so no cost is estimated; hover the cost cell to see why. |

## Investigation findings

- Discovery read revision `2f661f462` on branch `claude/feat/sessions-list-column-headers` in the worktree (codegraph and kw-docs provenance matched).
- `AgentSessionRow` (`dash/web/src/components/ContextExplorer.tsx`) cells, in order: optional subagent toggle (outside the row button), chevron, id (`formatSessionId`, variable width), title (`flex-1`), harness badge (variable width), turns (`w-16`), cost (`w-28 whitespace-nowrap shrink-0`, asserted by the issue #228 test), started (`w-16`), optional parent link (outside the row button).
- `ContextExplorer` renders rows straight into a `Card` after the search box; empty, loading and error states each have their own `data-testid` (`explorer-empty`, `explorer-loading`, `explorer-error`).
- Status words come from `formatCostFigure` and `STATUS_WORDS` in `dash/web/src/components/SessionCostPanel.tsx`; the explanation sentences exist only inline in `SessionCostPanel` (the `block.status !== 'priced'` block).
- `out_of_scope` is produced by `CanonPricing.PricePublishedTurn` and `CanonPricing.PriceStoredTokens` when `IsPublishedTableHarness` is false (`PublishedApplicabilityHarnesses`: the claude-* and codex-* identities); Copilot harnesses route to the credits table through `IsCopilotHarness`.
- Existing tests to extend: `dash/web/src/components/ContextExplorer.test.tsx` (`renderExplorer`, `stubServer`, the `AgentSessionRow row cost cell (issue #186)` block) and `dash/web/src/components/SessionCostPanel.test.tsx`. `dash/web/src/components/analysis/session-dashboard.test.tsx` renders `ContextExplorer` with fake hooks; the header adds no hook.
- React standard fetched through `docs_get_coding_standard_for_technology` (react, parent typescript).
- `PricePublishedTurn` and `PriceTurn` return an existing `basis: harness` block unchanged before any scope check, so a harness that reports its own cost is never `out_of_scope`.
- The `react` task gate (`technology-gates.react`) runs `dash-typecheck` and `dash-lint`; the Test contract's runner command is the tests' proof, as in the archived 2026-11-23 KyberDash sectioned-views plan, whose React tasks list only the two task-gate runs.
- Discovery: codegraph 4, docs 2, direct reads 9 (plan-authoring and test-first references, the plans index, `dash/AGENTS.md` gates, `CanonPricing.cs` harness sets and pricing branches, `ContextExplorer.tsx` row and list markup, `ContextExplorer.test.tsx` fixtures, `SessionCostPanel.tsx` explanation block, `docs/dash` architecture and runbook passages: each to confirm one detail the codegraph answer trimmed or that is documentation).

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/web/src/components/ContextExplorer.test.tsx` (new describe `ContextExplorer column headers`; extend `AgentSessionRow row cost cell (issue #186)`) | `npm --prefix dash run test:related -- web/src/components/ContextExplorer.tsx web/src/components/SessionCostPanel.tsx` | Data state (fetched list): `explorer-column-headers` renders once, before the first row, with labels Session, Title, Harness, Turns, Cost, Started in that order. Embedded `sessions` with rows: header renders; embedded `[]`: no header. Empty server page: `explorer-empty` and no header. Held response: `explorer-loading` and no header. Failed response (500): `explorer-error` and no header. Each header cell's class list contains the same `SESSION_LIST_COLUMNS` width class as the matching row cell (id, harness, turns, cost, started). Root row without subagents renders `subagent-toggle-spacer`; a root with subagents renders `subagent-toggle-<id>` and no spacer. Cost cell title: for `no_rate`, `not_billed`, `out_of_scope` and `partial` it matches `/published/i` and contains `STATUS_EXPLANATIONS[status]`; for a priced block it is exactly `Cost basis: published`; for a rolled-up figure it stays `Cost including subagents`. Existing assertions (cost text, `w-28 whitespace-nowrap shrink-0`) keep passing. | `SESSION_LIST_COLUMNS`, `STATUS_EXPLANATIONS` missing from imports, or header and spacer assertions failing | All listed tests pass with no assertion weakened; run reports a non-zero test count |
| T1 | `dash/web/src/components/SessionCostPanel.test.tsx` (extend) | same command | For each non-priced status the tile renders `STATUS_EXPLANATIONS[status]`; `STATUS_EXPLANATIONS.out_of_scope` equals "No KyberDash price table covers this harness, so its cost is not estimated."; a priced block renders no explanation. | `STATUS_EXPLANATIONS` not exported | Same tests pass |

## Tasks

### T1: Column header and plain cost-status explanations in the Sessions list

- **Objective:** Add a header row naming each Sessions list column, aligned to fixed-width row cells, and explain every cost status word in the cost cell's hover text from one shared map that the cost tile also uses.
- **Capabilities:** React and TypeScript UI with Vitest and Testing Library tests
- **Difficulty:** easy
- **Technology:** react
- **Session:** `T1`
- **Depends on:** none
- **Files:**
  - `dash/web/src/components/SessionCostPanel.tsx`: add exported `STATUS_EXPLANATIONS: Record<Exclude<CostStatus, 'priced'>, string>` beside `STATUS_WORDS` with `not_billed` "Model is explicitly not billed by provider.", `out_of_scope` "No KyberDash price table covers this harness, so its cost is not estimated.", `no_rate` "No published rate available for this model.", `partial` "Only a portion of turns could be priced."; `SessionCostPanel` renders `STATUS_EXPLANATIONS[block.status]` in its non-priced block instead of the four inline strings.
  - `dash/web/src/components/ContextExplorer.tsx`: add exported `SESSION_LIST_COLUMNS` (width classes for `id` `w-36 shrink-0 truncate`, `title` `min-w-0 flex-1`, `harness` `w-32 shrink-0`, `turns` `w-16 shrink-0 text-right`, `cost` `w-28 shrink-0 whitespace-nowrap text-right`, `started` `w-16 shrink-0 text-right`) and use them in `AgentSessionRow` cells (the harness badge moves inside a `harness` cell and keeps `data-testid="agent-harness-badge"`); `AgentSessionRow` renders an `aria-hidden` `subagent-toggle-spacer` the toggle's width when a non-child row has no subagents; cost cell `title` per D5 using `STATUS_EXPLANATIONS`; add `SessionListHeader` (`data-testid="explorer-column-headers"`, same leading spacer, chevron slot, padding and gap as the row button, labels Session, Title, Harness, Turns, Cost, Started in `text-[11px] text-tertiary-foreground`) rendered by `ContextExplorer` above the rows only when `sessions` has at least one row.
  - `dash/web/src/components/ContextExplorer.test.tsx`: new describe `ContextExplorer column headers`; extend `AgentSessionRow row cost cell (issue #186)` with the title cases.
  - `dash/web/src/components/SessionCostPanel.test.tsx`: explanation cases.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T1 --red`
  - `kyber-weave review gates . --agent <worker> --task T1`
- **Acceptance:**
  - Every Test contract row for T1 passes, with a non-zero test count; the `--red` run failed on the new assertions before implementation.
  - Existing tests in `ContextExplorer.test.tsx`, `SessionCostPanel.test.tsx` and `dash/web/src/components/analysis/session-dashboard.test.tsx` keep their assertions; a locator the new header made ambiguous may be narrowed and is named in the report.
  - `npm --prefix dash run typecheck` and `npm --prefix dash run lint` pass through the task gate.
  - No change outside the four files above; no engine, API or style-token change.

## Dependency graph

T1 alone; no dependencies.

## Risks

- Fixed id width truncates long readable ids: the full id stays in the cell's `title`, and `w-36` fits the shortened uuid form (`8…4` plus a harness prefix in most cases).
- A header label inside the list could make an existing `getByText` locator ambiguous (for example "Cost"): the acceptance lets the worker narrow it and name the edit.

## Out-of-scope boundaries

- No change to the cost status values, their words, the engine's pricing, or the `/api/kyber/*` contract.
- No change to the version plumbing another run is editing: `scripts/release-local.sh`, `.github/workflows/release.yml`, `dash/tray/src-tauri` version metadata, the dashboard `/api/kyber/version` endpoint.
- PR note: `dash/web/src/components/ContextExplorer.tsx` `AgentSessionRow` — child rows' trailing "parent:" link shifts their right-hand cells left of the header columns — fixing it means nesting buttons or removing tested navigation, neither asked for (D8).

## Verification gates

- T1 task gate: the `--red` run and the passing run, covering the Test contract's runner command, `dash-typecheck` and `dash-lint`.
- End-of-run review council runs the host's full gates, including `ts-test` over `dash/`.

## Review

The end-of-run review council under the `code-review` skill reviews the whole change; the plan carries no review command.

## docs-dev closeout

`docs-dev` harvests the two Design harvest rows into `docs/dash/architecture.md` (Costblock And Cost Basis, Display paragraph) and `docs/dash/runbook.md` (cost status explanation), fills the Closeout mapping, and archives the plan.

## Closeout mapping

Checked on 2026-11-25 against the delivered change (T1 commits d3c8a173b, dd5a4c06b, 0c56d9acb).
Criteria needing a live browser are not performed here: `NOT VERIFIED: column alignment of the
header over the rows in a rendered browser — needs a live dashboard session`.

| ID | Destination | Documentation-impact |
|---|---|---|
| D1, D2, D3, D4 | [KyberDash architecture: Display](../dash/architecture.md#costblock-and-cost-basis) | extends-component; header row, labels, shared widths and the data-state-only rule are described under Display; last-reviewed 2026-11-25 |
| D5, D6, D7 | [KyberDash architecture: Display](../dash/architecture.md#costblock-and-cost-basis) and [runbook: no published rate](../dash/runbook.md) | extends-component; hover text format, shared `STATUS_EXPLANATIONS` and the new "out of scope" wording; last-reviewed 2026-11-25 |
| D8 | Documentation waiver — a PR note about child rows' trailing parent link; the architecture Display paragraph records the reserved trailing slot, and no further invariant survives | n/a |
| AP1 | Documentation waiver — approval record only, preserved in this plan's Decisions table | n/a |

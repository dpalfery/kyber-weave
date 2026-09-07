# T8 — The walking skeleton: make the hierarchy clickable

**Model:** strong. **ONE agent, ONE window, NO parallelism.** **Run after:** T1–T4 merge.

This is the task the last two windows should have been. Read all of it before starting.

---

TASK: Five page components — `Attention`, `HarnessDetail`, `RunDetail`, `FindingDetail`, `CompareRuns` — exist in `dash/dash/src/pages/` and are imported by nothing except three test files. `App.tsx` navigation is `useState<KyberPage>` over a five-entry `NAV_TABS` const (`usage · context · compare · quarantine · problems`), dispatched by a ternary chain in `<main>`. The diagnostic hierarchy the product is organised around is not reachable in the running app.

Make it reachable. **Ugly is fine. Reachable is the requirement.**

DONE WHEN THESE PASS (all fail right now — run them first and read the failures):

```bash
npm --prefix dash run dev &
npx --prefix dash playwright test e2e/spine.spec.ts -g "G1|G2|G4"
```

FILES YOU OWN (touch nothing else):

```
dash/dash/src/App.tsx
dash/dash/src/pages/HarnessDetail.tsx
dash/dash/src/pages/RunDetail.tsx
dash/dash/src/pages/FindingDetail.tsx
dash/dash/src/pages/TurnDetail.tsx          (new file)
dash/dash/src/components/kyber/HierarchyBreadcrumb.tsx
dash/dash/src/App.test.tsx
```

`pages/Attention.tsx` was rewritten by T3 and already emits `data-testid="page-attention"` and `drill-harness-<id>`. Read it; do not restructure it.

## What to build

**1. Replace the flat page enum with a location stack.**

```ts
type SpineLocation = {
  level: 'attention' | 'harness' | 'run' | 'execution' | 'turn' | 'finding' | 'compare'
  harnessId?: string
  runId?: string
  executionId?: string
  turnIndex?: number
  findingId?: string
}
```

A reducer with `push` / `pop` / `replace` / `goTo(level)`. `useState<KyberPage>` cannot express a six-level hierarchy; that is why nothing connects today.

**2. `Attention` becomes the landing page.** The app opens on it. `Usage` survives as a tab; it does not open the app. If the first thing a developer sees is a spend grid, this is CodeBurn.

**3. Wire every `onSelect*` prop the pages already declare.** `onSelectHarness`, `onSelectRun`, `onSelectFinding`, `onSelectTurn`, `onSelectExecution` are all declared and all unconnected. Connect them to `push`.

**4. One harness selector.** The shell's `HARNESS:` strip becomes the harness level. `ContextExplorer` renders the same eleven choices again, 100px below — but that file is not yours. If deleting the duplicate requires editing `ContextExplorer.tsx`, **report it and stop**; passing G4 may require scoping the shell strip to the Usage page instead, which is within your files.

**5. `TurnDetail` — new, minimal.** Reachable from `RunDetail`. Lists the turn's context composition bands with `data-testid="context-band-<key>"`. Clicking a band puts that block's text into `data-testid="context-content"`. `ContextInspector.tsx` already does this well and is wired into `SessionInspectorDrawer` — **read it and reuse it**; do not reimplement. If it needs a turn-scoped prop it does not have, report that; rehoming it properly is task K4, not this one.

**6. Breadcrumb navigation.** `breadcrumb-attention` … `breadcrumb-turn`, each clickable, popping the stack. Browser back/forward is out of scope.

## What NOT to build

- **No styling.** Every page may be `<pre>{JSON.stringify(data, null, 2)}</pre>` inside a root with the right `data-testid`. Density and palette are Phase M.
- **No router.** No `react-router`, no URL sync, no deep links. `SpineLocation` is the shape a router would later consume; building one now is premature.
- **No new components.** The components exist. This task is wiring.
- **No new signals, findings or API endpoints.** If a page needs data no endpoint serves, render what exists and report the gap.

## Constraints

- Do not add fixtures or mocks. The gate runs against the real dev server and the developer's real `~/.kyberdash/canon.db`.
- **Do not write a new unit test as evidence of completion.** `Scorecard.test.tsx` passes today while the page it tests is unreachable. That is exactly how ten hours were lost. The gate above is the only evidence that counts.
- If the gate cannot pass for a reason outside your owned files, STOP and report what blocks it. Do not widen your file list.
- **Do not break the Usage page.** It is the only screen with working data today. `App.tsx` carries device state, share state, period state, provider state and query invalidation tangled together — read all 1,030 lines before editing. Usage, Quarantine and Problems must still render.
- Verify by opening `127.0.0.1:4747` in a browser and clicking, at 1280px. Report what you clicked and what you saw. "Tests pass" is not a report.
- If the live store has no runs for a harness, that level renders an empty state — it does not crash and it does not fabricate rows.

REFERENCE: `docs/plans/2026-09-06-kyberdash-spine.md` Phase A / T8, decisions D1–D5. Interaction reference: `KyberDash.dc.html` in the design project — read its structure, ignore its styling for this task.

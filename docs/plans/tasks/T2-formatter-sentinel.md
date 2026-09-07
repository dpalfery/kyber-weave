# T2 — Make `0` unrepresentable for an unreported counter

**Model:** Haiku. **Parallel with:** T1, T3, T4. **Blocked by:** T7 (answer Q9 first).

---

TASK: The session overview renders `TOTAL INPUT 0`, `CACHE READ 0`, `CACHE CREATION 0`, `TOTAL OUTPUT 0` and `TOOL CALLS 0` in KPI type for a session that simultaneously reports 5 spans, 5 turns, 5 requests and 2.5s duration. Five requests did not consume zero input tokens — that is missing data wearing a measurement's clothes. The honest treatment already exists three cells away (`COST —`, `CACHE HIT RATIO —`, `TOOLS OFFERED — / "not exported by gemini"`). Push it into the formatter so no future card can opt out.

**Read T7's finding before starting.** If Q9 concludes the counts are emitted and dropped in ingest, this task is still correct but stops being the whole fix.

DONE WHEN THIS PASSES (it fails right now — run it first and read the failure):

```bash
npm --prefix dash run dev &
npx --prefix dash playwright test e2e/spine.spec.ts -g "G3"
```

FILES YOU OWN (touch nothing else):

```
dash/dash/src/lib/utils.ts
dash/dash/src/components/MetricCard.tsx
```

WHAT TO CHANGE:

1. `fmtTokens`, `fmtNum`, `usd` accept `number | null | undefined` and return an em-dash sentinel (`—`) for `null` / `undefined`. They must still return `0` for a genuine numeric zero.
2. Add `fmtMeasured(value: number | null | undefined, reason?: string)` returning `{ text, measured: boolean, reason?: string }`.
3. `MetricCard` renders `data-testid="metric-<key>"` and `data-measured="true|false"`, and carries `reason` into its `title` attribute so hovering an em-dash explains itself.

THE DISTINCTION THAT MATTERS:

- A harness that **does not report** tool calls → `—` + reason. `data-measured="false"`.
- A harness that **does report** tool calls, and the session made none → `0`. `data-measured="true"`.

Both must be asserted. Getting this backwards makes the product lie in the other direction.

CONSTRAINTS:

- Do not add fixtures or mocks. The gate runs against the real dev server and the developer's real `~/.kyberdash/canon.db`.
- Do not write a new unit test as evidence of completion. The gate above is the only evidence that counts. (Unit tests for the zero-vs-absent distinction are still welcome — they are documentation, not evidence.)
- If the gate cannot pass for a reason outside your owned files, STOP and report what blocks it. Do not widen your file list. In particular: if callers pass `?? 0` before reaching the formatter, report the call sites — do not go edit them.
- `lib/utils.ts` is shared with the Usage pages. Check for regressions there specifically; a `—` where Usage expects `$0.00` is a real break.

REFERENCE: `docs/plans/2026-09-06-kyberdash-spine.md` decision D6.

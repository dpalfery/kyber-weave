# T1 — Delete decision-id narration from spine components

**Model:** Haiku. **Parallel with:** T2, T3, T4. **Blocks:** nothing.

---

TASK: Remove all internal decision-id and compliance narration from the diagnostic components. The UI currently cites its own spec at the user — "Independent Vectors (D3)", "Ranked by Decision D6 formula (estimated waste × outcome risk × confidence)", "Diagnostic integrity: D3 requires independent dimensions; D9 keeps cost derived & secondary". Decision ids are provenance for the team and noise for a developer at 11pm.

DONE WHEN THIS PASSES (it fails right now — run it first and read the failure):

```bash
npm --prefix dash run dev &
npx --prefix dash playwright test e2e/spine.spec.ts -g "G5"
```

FILES YOU OWN (touch nothing else):

```
dash/dash/src/components/kyber/Scorecard.tsx
dash/dash/src/components/kyber/FindingList.tsx
dash/dash/src/components/kyber/EvidenceTable.tsx
dash/dash/src/components/kyber/ConfidencePanel.tsx
dash/dash/src/components/kyber/RecommendationPanel.tsx
dash/dash/src/components/kyber/TurnAlignedDiff.tsx
dash/dash/src/pages/CompareRuns.tsx
```

`pages/Attention.tsx` is owned by T3 and also carries this narration. Do not touch it.

WHAT TO REMOVE:

- Any `D<number>` reference, "Decision", "per Decision", "ADR" string in rendered text, `title` attributes, or `aria-label`s.
- The "Independent Vectors (D3)" badge in `Scorecard`'s header.
- `Scorecard`'s compliance footer ("Diagnostic integrity… / Telemetry classes: deterministic · inferred · coverage-gap").
- The "Dashes (—) indicate unmeasurable telemetry, never zero" explainer. The dash speaks for itself; the reason belongs in the cell's `title`.

WHAT TO KEEP:

- Every measurement-class value (`deterministic` / `inferred` / `coverage-gap`) as a **displayed value**, not as a sentence about policy.
- Every `data-testid`.
- Code comments citing decisions. Those are for us. Only *rendered* text is in scope.

Reduce each panel's explanatory copy to one sentence or none.

CONSTRAINTS:

- Do not add fixtures or mocks. The gate runs against the real dev server and the developer's real `~/.kyberdash/canon.db`.
- Do not write a new unit test as evidence of completion. The gate above is the only evidence that counts.
- If the gate cannot pass for a reason outside your owned files, STOP and report what blocks it. Do not widen your file list.
- Do not restyle, rename or refactor anything this task does not require. Deletion only — no layout changes, no token changes.
- Existing unit tests that assert on the deleted strings should be updated, not deleted.

REFERENCE: `docs/plans/2026-09-06-kyberdash-spine.md` decision D10.

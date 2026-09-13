# C-density diagnostics sweep (post-edit)

- Date: 2026-09-12
- Exclusive files (whole-file):
  - `dash/dash/src/pages/Attention.tsx`
  - `dash/dash/src/components/kyber/FindingList.tsx`
  - `dash/dash/src/components/kyber/Scorecard.tsx`
  - `dash/dash/src/components/kyber/ScorecardMatrix.tsx`
  - `dash/dash/src/pages/TurnDetail.tsx`
- IDE `ReadLints`: no linter errors found
- ESLint project command: ignore-pattern warnings only (`dash/**`); `--no-ignore` clean, exit 0
- Typecheck: `npm --prefix dash/dash run typecheck` exit 0; `npm --prefix dash run typecheck` exit 0

No remaining diagnostics in the task-scoped files.

## Browser (D1)

- Opened `http://localhost:5173/` at 1280×800.
- `page-attention` visible (G1). Child order: breadcrumb, title, `finding-list`, `scorecard-matrix`.
- Findings + matrix first diagnostic blocks; 5 finding cards; 14 harness rows; chrome padding 12px (`p-chrome`); title 20px (`text-density-display`); foreground `rgb(232,234,238)` on `rgb(22,24,29)`.
- Clicked evidence `Turn #10` → Turn 10 (`page-turn`). Clicked header Attention → G1 again.

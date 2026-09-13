# PD7 diagnostics sweep (post-edit)

- Date: 2026-09-12
- Files edited/created:
  - `dash/dash/src/lib/kyberApi.ts`
  - `dash/dash/src/pages/FindingDetail.tsx`
  - `dash/dash/src/components/kyber/index.ts`
  - `dash/dash/src/components/kyber/CalibrationSummary.tsx` (new)
- IDE `ReadLints` on all four: no linter errors found
- ESLint project config (`dash/**` ignore): ignored-file warnings only, `eslint_exit=0` — see `lint-sweep.txt`
- ESLint `--no-ignore` on the four files: clean, `eslint_no_ignore_exit=0`
- `npm --prefix dash/dash run typecheck`: no errors in PD7 files. Remaining errors are `src/App.tsx` (`findLastIndex`) and `src/pages/RunDetail.tsx` (`turnCount`) — not in exclusive file set, not introduced by this task.
- Workspace IDE pass on the exclusive paths: clean

Remaining in exclusive files: none.

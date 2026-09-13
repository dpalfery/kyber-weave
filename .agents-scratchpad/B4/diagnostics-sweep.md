# B4 diagnostics sweep (post-edit)

- Date: 2026-09-12
- Files: `dash/dash/src/App.tsx`, `dash/dash/src/pages/CompareRuns.tsx`, `dash/dash/src/components/kyber/TurnAlignedDiff.tsx`
- IDE `ReadLints`: no linter errors found
- ESLint (`dash/` cwd): see `lint-sweep.txt` — `dash/dash/**` remains ignored by `dash/**` in eslint.config.js (same as baseline)
- `npm --prefix dash/dash run typecheck` remaining (outside exclusive files / pre-existing):
  - `App.test.tsx(10,3)`: `KyberComparePanel` export removed by B4 (DC-4 / test-dev hand-off)
  - `App.tsx(97)` `findLastIndex` / implicit any — pre-existing, not introduced
  - `RunDetail.tsx` turnCount — not in B4 files

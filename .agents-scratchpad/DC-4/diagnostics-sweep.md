# DC-4 diagnostics sweep (post-edit)

- Date: 2026-09-12
- Edited/deleted: `dash/dash/src/components/kyber/CompareView.tsx` (deleted), `dash/dash/src/components/kyber/index.ts`, `dash/dash/src/components/kyber/kyber-views.test.tsx`, plus blast-radius `dash/dash/src/lib/kyberApi.ts` (inlined `KyberComparisonTable` so the deleted module is not a type import)
- IDE `ReadLints` on remaining exclusive files + kyberApi.ts: no linter errors found
- ESLint (`npm --prefix dash run lint`): `dash/dash/**` is ignored by `dash/eslint.config.js` (`dash/**`); see `lint-sweep.txt`
- `npm --prefix dash/dash run typecheck`: no errors in DC-4 files. Pre-existing failures remain in `App.tsx` (`findLastIndex`) and `RunDetail.tsx` (`turnCount` possibly null) — not introduced here; no `CompareView` module errors

Workspace-wide IDE diagnostics for affected paths: clean on remaining kyber view files.

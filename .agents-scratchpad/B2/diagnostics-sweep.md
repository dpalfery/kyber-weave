# B2 diagnostics sweep (post-edit)

- Exclusive files:
  - `dash/dash/src/pages/Attention.tsx`
  - `dash/dash/src/components/kyber/index.ts`
  - `dash/dash/src/components/kyber/ScorecardMatrix.tsx` (created)
- IDE `ReadLints` on all three: no linter errors found
- ESLint `--no-ignore` over the three paths: exit 0 (see `lint-sweep.txt`)
- Workspace-wide IDE pass on these paths: clean

Remaining outside this file list (not fixed):
- `dash/dash/src/components/kyber/Scorecard.test.tsx` still expects `Workspace Diagnostic Scorecard` and `harness-health-table` on Attention. Hand-off to test-dev; B2 must not author tests.

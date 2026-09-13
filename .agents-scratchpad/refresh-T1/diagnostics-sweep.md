# T1 diagnostics sweep (post-implementation)

- Date: 2026-09-12
- Exclusive files: `dash/kyber/refresh/types.ts`, `registry.ts`, `registry.test.ts`

## IDE

`ReadLints` on the three exclusive files: no linter errors.

## Typecheck

```bash
npm --prefix dash exec -- tsc --noEmit -p dash/kyber/tsconfig.json
```

Exit 0.

`npm --prefix dash run typecheck` uses upstream `dash/tsconfig.json` (`include: src/**/*` only) and does not typecheck `kyber/refresh/**`. The merge-zone config above is the feasible T1 gate.

## ESLint

`npm --prefix dash run lint -- kyber/refresh/*.ts` still expands to `eslint .` (script is `eslint .`). Exit 0 with 0 errors / 211 warnings, all in vendored `dash/src/**` and `dash/tests/**` (warn-tier). No merge-zone errors reported for `kyber/refresh/**`.

## Focused tests

```bash
npx --prefix dash vitest run kyber/refresh/registry.test.ts
```

GREEN: 13 passed.

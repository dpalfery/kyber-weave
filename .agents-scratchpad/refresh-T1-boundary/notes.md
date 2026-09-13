# refresh-T1-boundary

## Change
- `isAllowedUpstreamImporter` now treats `dash/kyber/refresh/**` as an adapter seam (alongside `synth/**` and `canon/adapters/**`).
- Mechanical test: refresh may import `dash/src/providers`; non-adapter kyber (`kyber/cli/**`) still fails.

## GREEN

```
npx --prefix dash vitest run kyber/tools/boundary.test.ts
exit 0
✓ dash/kyber/tools/boundary.test.ts (13 tests) 360ms
Test Files  1 passed (1)
Tests  13 passed (13)
```

## Exclusive files
- dash/kyber/tools/boundary.ts
- dash/kyber/tools/boundary.test.ts

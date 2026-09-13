# T4 GREEN

- `npx --prefix dash vitest run kyber/synth` — 9 files, 97 passed
- `npm --prefix dash run typecheck` — pass
- eslint on exclusive synth files — pass

RED was 11 failing T4 tests (plus later 2 assertion tweaks) before `synthesizeEnvelopes`, source-unit `parseProblem`, and turn-id `deduplicate`.

# T6 — Split-aware derivation and availability

Implemented 2026-09-13. Did not edit `store.ts`, CLI, refresh/orchestrator, `App.tsx`, or `dash/src`.

## Approach

- `normalizeHarnessName` no longer collapses Copilot / Cursor / Cline / Antigravity surfaces.
- Gemini remains a recognizable name for E4 survey lookup (`cacheAvailability('gemini')`) but `isExcludedHarnessIdentity` keeps it out of derived sessions, runs, and rollup seeding.
- `GEMINI_SELECTOR_LABEL = 'Gemini'` is display-only; UI selector copy stays in App.tsx / Attention.
- `buildSessions` / `buildRuns` group `recordsForSession` by canonical harness so T2’s `sessionKeys()` (still one row per native id) cannot merge Cursor and Cursor Agent.
- `buildHarnessRollup()` seeds `HARNESS_DESCRIPTORS` plus observed coding-harness ids, never Gemini.
- Kilo aliases collapse only to `kilo-shared-runtime` except `kilo-vscode-legacy`.
- Generic `claude` → `claude-unclassified`; do not guess CLI.

## RED then GREEN

Focused file: `dash/kyber/canon/split-identity.test.ts` (10 failed → 13 passed).

## OPEN

- `store.sessionKeys()` still documents Cursor/Cursor Agent as one conversation. T6 works around it; T2 may later group by `(harness, session_id)`.
- `dash/kyber/cli/refresh.test.ts` still encodes pre-T6 gemini/merge expectations (T5 exclusive).

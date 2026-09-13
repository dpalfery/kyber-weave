# refresh T3 — native-unit boundary adapter

Inspected 2026-09-12. Exclusive files only under `dash/kyber/refresh/source-reader*` and `fixtures/`.

## STATUS

GREEN

## ARTIFACTS

- `dash/kyber/refresh/source-reader.ts`
- `dash/kyber/refresh/source-reader.test.ts`
- `dash/kyber/refresh/fixtures/claude-cli-session.jsonl`
- `dash/kyber/refresh/fixtures/codex-cli.jsonl`
- `dash/kyber/refresh/fixtures/codex-desktop.jsonl`
- `dash/kyber/refresh/fixtures/codex-t3code-desktop.jsonl`

Did not edit `registry.ts`, `registry.test.ts`, `store.ts`, synth, CLI, UI, or `dash/src/**`.

## RED

`source-reader.test.ts` failed to import `./source-reader.js` (module missing). 0 tests collected.

## GREEN

`npx vitest run kyber/refresh/source-reader.test.ts` — 10 passed.

Fixtures cover: cross-cutoff slice, Claude special-path, Codex originator (including `t3code_desktop` → unclassified), Antigravity roots, Copilot sourceType, Kiro paths, Kilo shared fallback, fingerprint change detection, bounded `mapWithConcurrency`.

## SUMMARY

Kyber adapter walks one harness job’s native units through exported Provider / `DateRange` / `SessionCache` / fingerprint / parser seams. Claude empty `createSessionParser` is recovered by directory expansion + `loadClaudeCalls` after `parseAllSessions(..., 'claude')`. Missing client evidence stays unclassified/shared; never guessed.

## OPEN_QUESTIONS

Production Claude jobs that omit injected `parseAllSessions` will call the live upstream special parse (host corpus). T5 should keep injecting a cache dir / lock around that call.

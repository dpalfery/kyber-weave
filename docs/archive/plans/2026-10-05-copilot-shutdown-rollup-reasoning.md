---
id: plans/2026-10-05-copilot-shutdown-rollup-reasoning
title: Honest unobservability for reasoning on outputless Copilot shutdown rollups (#241)
doc-type: plan
status: complete
owner: dpalfery
last-reviewed: 2026-10-05
component: KyberDash
---

# Honest unobservability for reasoning on outputless Copilot shutdown rollups — #241

Fixes [issue #241](https://github.com/dpalfery/kyber-weave/issues/241) ("KyberDash: honest unobservability for reasoning on outputless copilot shutdown rollups"). Development mode: **test-first**.

## Problem

Copilot CLI file-based shutdown rollups (`dash/src/providers/copilot.ts:1006-1010`, deduplication key `copilot:<sid>:shutdown:<model>:<n>`) carry `outputTokens: 0` by design: output is intentionally excluded on the shutdown rollup to avoid double-counting with per-turn `assistant.message` calls. However, reasoning tokens (`reasoningTokens`, e.g. 29,734 tokens observed in live audits) ride alongside inbound input and cache metrics.

In PR #255 (issue #193), folding exclusive reasoning into output via `billableOutputTokens` was added for exclusive providers, but Copilot was preserved as unfolded (`dash/src/synth/synth.ts:307`) because folding would manufacture generated output from reasoning alone on an output-absent row. Consequently, these shutdown rollup records have `output: 0` and `reasoning: 29734`. When passed to `validateRecordTokens` / `tokenValidator`, `validateTokens` fails with `TOKEN_REASONING_EXCEEDS_OUTPUT` ("reasoning (29734) is not a subset of output (0)"). The record is rejected as a validation error problem, preventing valid Copilot sessions from being stored or surfaced.

## Goal

Apply the honest unobservability standard (`docs/rules/honest-unobservability.md`) to Copilot shutdown rollups:
1. Keep `output` at 0 without manufacturing output from reasoning alone.
2. Surface `reasoning` as measured by the CLI.
3. Explicitly declare `output` as `not_measurable` on the record's `measurability` block (`reason: 'Output tokens are excluded from Copilot shutdown rollups to avoid double-counting with per-turn calls.'`).
4. Update `validateTokens` to accept `measurability?: Measurability`. When `measurability?.output` has `availability === 'not_measurable'`, bypass the `reasoning <= output` subset check while still enforcing non-negative reasoning (`reasoning >= 0`).

## Approved decisions

- **D1 (Pre-approved in intake):** Honest unobservability over manufacturing output. Never fold reasoning into output on outputless Copilot shutdown rollups; keep `output` at 0 and do not synthesize output tokens.
- **D2 (Pre-approved in intake):** Output absence declared via canonical `Measurability`. Stamped on the synthesized record's `measurability.output` as `{ availability: 'not_measurable', reason: 'Output tokens are excluded from Copilot shutdown rollups to avoid double-counting with per-turn calls.' }`.
- **D3 (Pre-approved in intake):** Validator awareness of unobserved output. `validateTokens` takes `measurability?: Measurability`. When output is declared `not_measurable`, reasoning is not required to be a subset of unobserved output, allowing the valid shutdown rollup record to pass validation.

No open questions remain (`NO_QUESTIONS`: user pre-decided all constraints and conventions; approve-and-execute gate pre-approved as "Hal approves").

## Investigation findings

1. **Emitter location:** In `dash/src/providers/copilot.ts:993-1021`, the shutdown rollup generator yields `ParsedProviderCall` with `deduplicationKey: copilot:${sessionId}:shutdown:${model}:${n}`, `outputTokens: 0`, and `reasoningTokens`.
2. **Synthesizer handling:** In `dash/src/synth/synth.ts:386`, `synthesizeCall` converts tokens and sets `measurability: measurabilityFor(call.provider)`. It can detect a Copilot shutdown rollup via `call.provider === 'copilot' && call.deduplicationKey.includes(':shutdown:')` and attach the `not_measurable` output declaration.
3. **Validation layer:**
   - `dash/src/canon/types.ts:219`: `validateTokens(tokens: TokenUsage, location?: string, measurability?: Measurability)`
   - `dash/src/canon/adapters/quarantine.ts:77`: `tokenValidator(record: CanonicalRecord)` passes `record.measurability` to `validateTokens`.
   - `dash/src/canon/adapters/copilot.ts:544`: `validateRecordTokens(record: CanonicalRecord)` passes `record.measurability` to `validateTokens`.
4. **Existing regression test:** `dash/src/synth/synth.test.ts:354` contains a test for issue #240 with a non-shutdown deduplication key (`copilot:sess-1:call-1`). That test must remain preserved for #240, while a new test specifically targeting shutdown rollups is added for #241.

## Mode contract

- **Development mode:** `test-first`
- **Test contract:**
  - RED: Add failing test in `dash/src/synth/synth.test.ts` for Copilot shutdown rollup with `outputTokens: 0` and `reasoningTokens > 0`, asserting that `synthesizeCall` stamps `measurability.output` as `not_measurable` and `tokenValidator(record)` is `undefined` (valid, not rejected).
  - RED: Add test in `dash/src/canon/types.test.ts` asserting that `validateTokens` succeeds when `reasoning > output` if `measurability.output` is `not_measurable`, and still fails if `reasoning < 0` or if output is measurable.
  - GREEN: Implement the unobservability declaration in `dash/src/synth/synth.ts` and validator support in `dash/src/canon/types.ts`, `dash/src/canon/adapters/quarantine.ts`, and `dash/src/canon/adapters/copilot.ts`.
  - VERIFY: Full TypeScript test suite, typecheck, lint, check:reachable, dotnet build/test, docs validate, docs drift.

## Dispatchable tasks

- **T1 (RED tests):**
  - Add unit tests in `dash/src/canon/types.test.ts` for `validateTokens` with `measurability.output` marked `not_measurable`.
  - Add unit test in `dash/src/synth/synth.test.ts` for synthesizing a Copilot shutdown rollup call (`copilot:sess-1:shutdown:claude-sonnet-4-5:1`) with `outputTokens: 0` and `reasoningTokens: 127`, verifying `measurability.output` and `tokenValidator(record) === undefined`.
- **T2 (GREEN implementation):**
  - Update `validateTokens` in `dash/src/canon/types.ts` to accept `measurability?: Measurability` and skip `TOKEN_REASONING_EXCEEDS_OUTPUT` when output is `not_measurable`.
  - Update `tokenValidator` in `dash/src/canon/adapters/quarantine.ts` and `validateRecordTokens` in `dash/src/canon/adapters/copilot.ts` to pass `record.measurability`.
  - Update `synthesizeCall` in `dash/src/synth/synth.ts` to attach `output: notMeasurable(...)` when synthesizing Copilot shutdown rollups.
- **T3 (Documentation and verification):**
  - Update `docs/dash/architecture.md` describing the honest unobservability rule for Copilot shutdown rollup reasoning.
  - Run all gates: `npm --prefix dash run typecheck`, `npm --prefix dash run lint`, `npm --prefix dash run test`, `npm --prefix dash run check:reachable`, `/Users/hal/.dotnet/dotnet build`, `/Users/hal/.dotnet/dotnet test`, `docs validate`, `docs drift`.
- **T4 (Closeout & Archive):**
  - Move plan to `docs/archive/plans/2026-10-05-copilot-shutdown-rollup-reasoning.md` and update `docs/plans/README.md`.
  - Open draft PR against `main`.

## Dependency graph & Concurrency

- T1 -> T2 -> T3 -> T4
- MAX_CONCURRENCY: 1 (sequential test-first delivery)

## Risks and boundaries

- Do not fold reasoning into output for Copilot.
- Do not affect standard model invocations where output is measurable (reasoning must still not exceed output there).
- Disclose known-failing host tests: `migration.test.ts` (DROP COLUMN), `cursor.test.ts` (six-month-cap DATE-ROT), and the 9 MCP external-runner test failures.

## Completion evidence

- T1: RED unit tests in `dash/src/canon/types.test.ts` and `dash/src/synth/synth.test.ts` confirmed failing as expected.
- T2: GREEN implementation in `dash/src/canon/types.ts` (`validateTokens`), `dash/src/canon/adapters/quarantine.ts` (`tokenValidator`), `dash/src/canon/adapters/copilot.ts` (`validateRecordTokens`), and `dash/src/synth/synth.ts` (`isCopilotShutdownRollup`).
- T3: Documentation updated in `docs/dash/architecture.md`. All gates verified:
  - `npm --prefix dash run typecheck` passed (0 errors)
  - `npm --prefix dash run lint` passed (0 errors)
  - `npm --prefix dash run check:reachable` passed (0 unreachables)
  - `npm --prefix dash run test` passed (284/284 test files, 4,408/4,408 tests)
  - `/Users/hal/.dotnet/dotnet build` passed (0 warnings, 0 errors)
  - `/Users/hal/.dotnet/dotnet test` executed: 2,550 passed, with only the 9 pre-existing Mcp external-runner test failures disclosed on this machine
  - `docs validate . --merge-ready` and `docs drift .` passed (0 findings)
- T4: Plan archived to `docs/archive/plans/` and indexed in `docs/plans/README.md`.

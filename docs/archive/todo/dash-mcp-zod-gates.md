---
id: archive/todo/dash-mcp-zod-gates
title: Pre-existing dash MCP gates fail (ts-typecheck TS2740, two Zod assertion tests)
doc-type: todo
component: KyberDash
status: superseded
owner: dpalfery
last-reviewed: 2026-10-26
documentation-impact: no-design-change
---

# Pre-existing dash MCP gates fail

> [!NOTE]
> **Closed by PR [#439](https://github.com/dpalfery/kyber-weave/pull/439).** `tools.ts` and
> `server.ts` used zod 3 APIs (`ZodEffects`, `.innerType()`) while `dash/package.json` and the
> lockfile resolve zod 4, so `tsc` and the bundled MCP server both failed on the same line.

Found 2026-10-26 during the whole-change review of the
[issue #436 plan](../plans/2026-10-26-issue-436-squad-preset-canonical-profiles.md):
`review gates` reports `ts-typecheck` and `ts-test` FAIL on files that change
is unrelated to (`git diff -- dash/` empty, change is C#/docs only), so these
failures are pre-existing and parked, not caused by that fix.

## Evidence (reproduced on the working tree, dash/ untouched)

1. `npm --prefix dash run typecheck`:
   `src/mcp/tools.ts(72,98): error TS2740: Type 'ZodEffects<ZodObject<...>>'
   is missing ... from type 'ZodObject<...>'` — a tool input schema wrapped
   in an effect (`.refine()` or similar) is passed where a plain `ZodObject`
   is required.
2. `npx vitest run src/mcp/context-inspect.test.ts src/mcp/server.test.ts`
   → 2 failed / 2 passed:
   - `context-inspect.test.ts`: expects `mode: expect.objectContaining({
     enum: ['manifest', 'content'], ... })` — actual JSON-schema shape for
     the Zod enum no longer matches (suspected Zod major-version output
     change, not confirmed).
   - `server.test.ts:21`: `tool.inputSchema.additionalProperties` is
     `undefined`, expected `false` (suspected MCP SDK and/or Zod
     serialization change, not confirmed).

## Suggested next step

Confirm the Zod / `@modelcontextprotocol/sdk` versions in
`dash/package.json` against what the tests were written for, then either pin
or adapt `tools.ts` and the two assertions. Do not bundle with the #436 fix.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| ZOD-1 | Why did `ts-typecheck` fail in `dash/` on a change that touched no `dash/` file? | The lockfile resolves zod 4.6.7 and the MCP SDK 1.32.x, but `src/mcp/server.ts` and `tools.ts` used the zod 3 refinement API (`ZodEffects`, `.innerType()`); a `dash/node_modules` that predated the zod 4 bump still had 3.25.76, which is why the failure looked unrelated to `dash/`. | `npm ci` reproduces it deterministically: under 4.6.7 `z.ZodEffects` is `undefined`, so `instanceof` throws `TypeError: Right-hand side of 'instanceof' is not an object` at runtime, which is what killed `sea-mcp-smoke` and the self-update loop on both Linux and macOS. | ANSWERED — reproduced locally after `npm ci`; matches both failing CI jobs |
| ZOD-2 | How is the wire-schema/refinement split expressed without zod internals? | Declare it: `AnalyticsToolDefinition.inputSchema` is the plain `z.ZodObject<z.ZodRawShape>` the SDK converts to JSON Schema, plus an optional `validationSchema` the handler parses; `dash_inspect` supplies `inspectBase` and the refined `inspect` respectively. | The documented `dash_inspect` contract is unchanged — the registered wire schema is identical and cross-field rules are still enforced, now by a declared schema instead of by unwrapping a refinement. | ANSWERED — autonomous; internal implementation choice with no observable contract change |
| ZOD-3 | What stops the refinement from being silently dropped? | `context-inspect.test.ts` asserts that a cross-field violation (`mode` without `target`; `assembled_turn` on a run target) is rejected as an MCP error before the analytics server is called. | Registering `inspectBase` alone passes every pre-existing test; the assertion was confirmed RED with `validationSchema` removed and GREEN with it. | ANSWERED — test-first |

## Closeout mapping

Links are written from this archive entry's location (`docs/archive/todo/`).

| ID | Destination |
|---|---|
| ZOD-1 | Documentation waiver: the zod-3/zod-4 diagnosis is a transient dependency fact, not a lasting invariant; the durable record is the suite that now passes under the lockfile. |
| ZOD-2 | Documentation waiver: internal registration detail. The documented `dash_inspect` wire contract in [dash/architecture.md](../dash/architecture.md#conversational-analytics-over-mcp-kyberdash-mcp) is unchanged, so no governed document needs a new statement. |
| ZOD-3 | Documentation waiver: test-only enforcement of an unchanged contract; the test file is the record. |

## Design harvest

Documentation waiver: no design survives. The dependency versions were already declared and
locked; the change replaces a zod 3 API with a declared-schema split, keeps the registered
wire schema and every documented `dash_inspect` behaviour identical, and adds a test. Nothing
about the MCP boundary, the eight tools, the envelope, or the display-layer rule moved, so no
canonical document gains or loses a statement.

## Resolution

The suggested next step was the answer, and the suspicion was correct.

1. **`ZodEffects` does not exist in zod 4**, so `dash/src/mcp/server.ts`'s `forRegistration`
   helper — `current instanceof z.ZodEffects` and `current.innerType()` — failed to compile
   (`TS2339` on both members) and threw at runtime. The split the helper existed to perform is
   now declared rather than derived, as recorded in ZOD-2.
2. **The two Zod assertion tests were already written for zod 4** and pass unchanged against
   the lockfile's version; they failed only under the stale zod 3 install. After `npm ci`,
   `npm --prefix dash run test` gives 499 files / 7469 tests passing, both MCP suites included,
   and `typecheck`, `lint` and `check:reachable` are clean.

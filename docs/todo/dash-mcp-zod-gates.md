---
id: todo/dash-mcp-zod-gates
title: Pre-existing dash MCP gates fail (ts-typecheck TS2740, two Zod assertion tests)
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-26
---

# Pre-existing dash MCP gates fail

Found 2026-10-26 during the whole-change review of the
[issue #436 plan](../archive/plans/2026-10-26-issue-436-squad-preset-canonical-profiles.md):
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

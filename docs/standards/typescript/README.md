---
id: standards/typescript
title: TypeScript coding standard
doc-type: coding-standard
status: current
technology: typescript
owner: dpalfery
last-reviewed: 2026-10-24
---

# TypeScript coding standard

How TypeScript is written in this repository. Agents and skills resolve this document as
`<typescript-coding-standard>` in the repository root [`AGENTS.md`](../../../AGENTS.md), so
what it says here outranks the defaults a portable agent shipped with.

This standard covers TypeScript in KyberDash's Node engine, web dashboard, and tray UI,
including TSX. It records local decisions rather than summarizing the language or establishing
a separate React architecture standard. The shared application and display-layer boundaries
remain defined by [KyberDash architecture](../../dash/architecture.md) and the
[display-layer rule](../../rules/kyberdash-display-layer.md).

## Types at boundaries

All three TypeScript projects enable `strict`. Keep values typed through their full path;
explicit `any` is an ESLint error. At an untrusted boundary—JSON, files, SQLite rows, IPC, or
HTTP—start with `unknown` and validate or narrow it before use. Use a runtime schema where a
payload contract needs validation; a TypeScript annotation alone does not validate data at
runtime.

Use tagged unions for states with different valid data and behavior. Handle every variant at
the point where behavior depends on it; make an added variant visible to the compiler instead
of quietly falling through to a default. Keep type assertions local and explain why the value
is safe when the type system cannot express the invariant. Do not spread assertions or
weaken compiler settings to make an unrelated change compile.

## Preserve meaning in data

Represent absence as absence. Do not turn missing, unknown, unavailable, or not-yet-measured
data into zero, an empty string, or an empty collection unless that is the actual domain
meaning. Preserve distinctions between observed and derived values, and include the source or
cost basis where downstream code could otherwise mistake one for the other. Keep API DTOs
compatible with their consumers and make availability explicit when a capability may not be
present.

## KyberDash boundaries and bounded work

The CLI engine and `kyberdash web` server own domain logic and jobs. The web dashboard and
tray are display layers over the same `canon.db` and `/api/kyber/*` API. A surface must not
create parallel derivations, read SQLite directly, own receivers or jobs, or duplicate engine
feature logic. UI and transport code validate input and forward it to the shared engine/API.
Consult the architecture and display-layer rule above for the full invariant and rationale.

Queries and file readers must have explicit bounds: allowlist SQL identifiers, bind values,
cap returned rows and decoded bytes, and paginate larger results. Prefer materialized
summaries and compression-aware readers where available; preserve legacy availability
behavior. Avoid N+1 queries and loading an entire corpus when a bounded summary or drill-down
will answer the request. Read-only tools must not migrate, reproject, or seed the datastore.

Synchronous SQLite work cannot be interrupted by a JavaScript timer. Do not present a timer
around synchronous work as a hard cancellation bound; use an actually bounded operation or
make the limitation explicit in its contract.

## Runtime and modules

KyberDash targets Node.js `>=22.13.0` and uses ESM. Prefer `node:`-prefixed imports
for Node built-ins in new code; much of the existing tree uses bare `'fs'`/`'path'`
specifiers, which resolve to the same modules — do not churn existing imports to match.

The engine TypeScript configuration uses `ESNext` modules with `bundler` resolution;
follow the surrounding project convention for relative `.js` specifiers in source imports.
Use type-only imports when a dependency is needed only for types.

The launcher at `dash/src/launcher.ts` must remain parseable by Node 18 so it can report the
runtime requirement before starting the application. Keep the launcher free of static
imports; load runtime-dependent modules only after its version guard.

Use existing dependencies for existing capabilities. A new dependency or drive-by version
upgrade needs a concrete reason tied to the change; do not add a package merely to avoid a
small, clear implementation.

## Async work, resources, and errors

Await work whose completion or failure matters. Bound HTTP requests, retries, and timers;
cancel them where the caller can stop waiting. Remove listeners and close resources on both
success and failure. For server shutdown, stop accepting work and drain or close owned
resources before reporting completion.

Catch errors only where the code can recover, add useful context, or translate them into a
stable boundary result. Preserve the cause when wrapping an error. Do not use empty catches
for unexpected failures. If an optional feature has a fallback, state when it is unavailable
so callers do not confuse fallback output with a complete result.

Inject clocks, filesystem roots, fetch functions, and stores at seams where tests need
deterministic control. Tests must use temporary or injected paths and must not read or write
the developer's home directory or live KyberDash database.

## Output and display

Keep machine-readable stdout free of progress and diagnostics; send diagnostics to stderr.
In React views, distinguish missing data, derived values, and measured zero in both the
display and accessible text where the distinction affects interpretation. Do not make a
missing value look like a healthy zero.

## Style and verification

The repository's ESLint configuration is authoritative: TypeScript uses the recommended
`typescript-eslint` rules, rejects explicit `any`, and treats lint findings as errors. Keep
the strict compiler settings enabled. Follow nearby formatting: two-space indentation,
single-quoted strings, and generally no semicolons.

For changes under `dash/`, run the applicable gates from the repository instructions:

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
```

Changes to the engine's built output or update path also require `npm --prefix dash run build:cli`; changes to the build/update path require `./scripts/update-loop.sh`. Changes to
`dash/tray/` additionally use the tray UI and Rust gates in the root `AGENTS.md`. Test
authorship follows the observable-behavior guidance in the repository's test standard;
Vitest tests remain TypeScript and follow this document's language rules.

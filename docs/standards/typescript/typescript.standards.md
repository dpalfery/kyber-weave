---
id: standards/typescript
title: TypeScript coding standard
doc-type: coding-standard
status: current
technology: typescript
owner: dpalfery
last-reviewed: 2026-11-22
---

# TypeScript coding standard

How TypeScript is written in this repository. Agents and skills resolve this document as
`<typescript-coding-standard>` in the repository root [`AGENTS.md`](../../../AGENTS.md), so
what it says here outranks the defaults a portable agent shipped with.

This standard covers TypeScript in KyberDash's web dashboard, tray UI, and Playwright/packaging
scripts, including TSX. It records local decisions rather than summarizing the language. React
components, hooks, data states and their tests follow the
[React standard](../react/react.standards.md), which builds on this one. The shared C# application and display-layer
boundaries remain defined by [KyberDash architecture](../../dash/architecture.md) and the
[display-layer rule](../../rules/kyberdash-display-layer.md).

## Naming and layout

### Runtime and modules

KyberDash web, tray UI, and script tooling target Node.js `>=22.13.0` and use ESM (`node:`-prefixed
imports for Node built-ins in scripts and test helpers). Use type-only imports when a dependency
is needed only for types.

Relative import specifiers follow `moduleResolution`, which is `bundler` in every project here:
omit the extension, as the files around you do. ESM has no `__dirname`; derive it with
`dirname(fileURLToPath(import.meta.url))`. JSON is imported through `resolveJsonModule`; do not
add a second form. `engines.node` is the runtime contract, so an API newer than it is a
breaking change. A dependency goes in `dependencies` only if shipped code imports it; build and
test tools go in `devDependencies`, and the lockfile changes in the same change through
`npm install <pkg>`, never by hand. A change that compiles under one `tsconfig` (`dash`, `web`,
tests) and not another is not done.

Use existing dependencies for existing capabilities. A new dependency or drive-by version
upgrade needs a concrete reason tied to the change; do not add a package merely to avoid a
small, clear implementation.

### Cross-platform

The dashboard, tray UI and scripts run on macOS, Linux and Windows. Build file-system paths
with `node:path` (`join`, `resolve`), never string concatenation with `/`, and compare paths
only after `path.resolve`. Home and config directories come from `os.homedir()`, not a
hard-coded `~` or `/Users`. Compared or hashed content normalizes line endings on read, or
`.gitattributes` keeps the checkout LF. `package.json` scripts avoid POSIX-only syntax
(`VAR=x cmd`, `rm -rf`) that fails under `cmd.exe`; use a small `.mjs` script.

## Coding standards

### Types at boundaries

Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.

The TypeScript projects (`dash` and `dash/tray/ui`) enable `strict`. Keep values typed through their full path;
explicit `any` is an ESLint error. At an untrusted boundary—JSON, files, SQLite rows, IPC, or
HTTP—start with `unknown` and validate or narrow it before use. Use a runtime schema where a
payload contract needs validation; a TypeScript annotation alone does not validate data at
runtime.

Use tagged unions for states with different valid data and behavior. Handle every variant at
the point where behavior depends on it; make an added variant visible to the compiler instead
of quietly falling through to a default. Keep type assertions local and explain why the value
is safe when the type system cannot express the invariant. Do not spread assertions or
weaken compiler settings to make an unrelated change compile.

`JSON.parse(text) as Row` is not narrowing: use the runtime schema or a hand-written guard
(`value is Row`) that checks every field. Replace a `!` with a named decision: when the lookup
is `undefined`, throw an error saying what was missing and for which id.

### Preserve meaning in data

Represent absence as absence. Do not turn missing, unknown, unavailable, or not-yet-measured
data into zero, an empty string, or an empty collection unless that is the actual domain
meaning. Preserve distinctions between observed and derived values, and include the source or
cost basis where downstream code could otherwise mistake one for the other. Check
`Number(raw)` and `parseInt` results with `Number.isFinite` and keep `null` on failure; `|| 0`
erases both `NaN` and a real `0`. A sum over a set with missing members is
`{ value: number | null; missing: number }`, `null` when none reported; an average divides by
the present members and is absent when there are none. Keep API DTOs
compatible with their consumers and make availability explicit when a capability may not be
present.

### KyberDash boundaries and bounded work

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

### Async work, resources, and errors

Await work whose completion or failure matters. Bound HTTP requests, retries, and timers;
cancel them where the caller can stop waiting. Remove listeners and close resources on both
success and failure. For server shutdown, stop accepting work and drain or close owned
resources before reporting completion.

Catch errors only where the code can recover, add useful context, or translate them into a
stable boundary result. Preserve the cause when wrapping an error. Do not use empty catches
for unexpected failures. If an optional feature has a fallback, state when it is unavailable
so callers do not confuse fallback output with a complete result: an unparseable file yields
"not parsed", not "zero rows". Read a Node error code through a guard
(`err instanceof Error && 'code' in err`), never `catch (e: any)`; `ENOENT` returns absent
only when absence is the answer, and every other code is rethrown. `void promise` is allowed
only when the promise already ends in a `.catch` that reports its failure, with a comment
saying why the result is ignored. Run independent awaits concurrently (`Promise.all`,
`Promise.allSettled`, which reports each rejected entry) and cap concurrency over an unbounded
set. Pass the `AbortSignal` to `fetch`, streams and `node:timers/promises`; in a wait loop only
an abort ends the wait quietly. A module that starts an interval or listener returns a way to
stop it. Shutdown runs from `SIGINT` and `SIGTERM` handlers: abort, drain with a bounded
`Promise.race` against an `unref`'d timer, then close the server. `process.exit` appears only
in a CLI entry point.

Inject clocks, filesystem roots, fetch functions, and stores at seams where tests need
deterministic control. Tests must use temporary or injected paths and must not read or write
the developer's home directory or live KyberDash database.

### Output and display

Keep machine-readable stdout free of progress and diagnostics; send diagnostics to stderr.
In React views, distinguish missing data, derived values, and measured zero in both the
display and accessible text where the distinction affects interpretation. Do not make a
missing value look like a healthy zero.

### Style

The repository's ESLint configuration is authoritative: TypeScript uses the recommended
`typescript-eslint` rules, rejects explicit `any`, and treats lint findings as errors. Keep
the strict compiler settings enabled. Follow nearby formatting: two-space indentation,
single-quoted strings, and generally no semicolons. There is no separate Prettier pass here, so
the format check is the lint gate below rather than a formatter of its own.

## Testing

_Nothing specific yet._

## Commands

_Nothing specific yet._

## Security and data handling

_Nothing specific yet._

## Done checklist

### Completion

For changes under `dash/`, run the applicable gates from the repository instructions:

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test:related -- <files>
npm --prefix dash run check:reachable
```

`typecheck` is the build check: it compiles every project in `dash/`, including `web/` and the test
tsconfig, and emits nothing. `lint` is also the format check, because ESLint owns formatting under
`dash/`. `test:related` runs the tests covering the files you touched, and `<files>` are relative
to `dash/`. The whole `npm --prefix dash run test` suite is the review council's and CI's gate; a
dev agent does not run it.

Fix the cause of a finding rather than suppressing it; a new lint suppression needs a stated
reason. When a gate could not run on an OS, name the OS in the completion digest.

Run each command directly, with no process wrapper; see
[Running tests safely](../../dash/runbook.md#running-tests-safely).

A change to the engine's shipped output also requires `npm --prefix dash run build:cli`, and a
change to the build or update path requires `./scripts/update-loop.sh`. Changes to `dash/tray/`
additionally use the tray UI and Rust gates in the root `AGENTS.md`. Test authorship follows the
observable-behavior guidance in the repository's test standard; Vitest tests remain TypeScript and
follow this document's language rules.

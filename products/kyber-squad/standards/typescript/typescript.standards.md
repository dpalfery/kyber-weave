---
id: standards/typescript
title: "TypeScript coding standard"
doc-type: coding-standard
status: draft
technology: typescript
owner: unassigned
last-reviewed: 2026-11-22
---

# TypeScript coding standard

How TypeScript is written for Node in this repository — CLIs, services, servers, data access,
and libraries. Agents and skills resolve this document as `<typescript-coding-standard>`.
React UI code (`.tsx`/`.jsx`, and the hooks and client state beside it) follows
`<react-coding-standard>` instead.


When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal, but a worker never stops for it: it uses the draft as the
best available guidance and names the draft standard it relied on in one line of its report.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Naming and layout

### Stack

- **Runtime:** the Node.js version named in `package.json` `engines.node`, which is an active
  LTS line. Raise it deliberately, in its own change.
- **Modules:** ES modules (`"type": "module"`). CommonJS appears only where a tool requires it,
  in a `.cjs` file. Built-in modules are imported with the `node:` prefix (`node:fs/promises`,
  `node:path`).
- **Compiler:** `tsc` with `strict: true`. `tsc --noEmit` is the type gate even when a bundler
  (`tsup`, `esbuild`) produces the output, because bundlers strip types without checking them.
- **Package manager:** the one whose lockfile is committed. The lockfile is committed and CI
  installs from it (`npm ci`). A new dependency needs a reason in the change description;
  prefer a Node built-in (`node:sqlite`, `fetch`, `util.parseArgs`) when
  one does the job.

Application code takes its collaborators — database handles, clocks, file-system roots, HTTP
clients, loggers — as parameters, and does not construct them internally or read a module-level
mutable for something a caller could pass.

### Modules and packages

- Relative import specifiers follow the project's `moduleResolution`. Under `NodeNext`/`Node16`
  they carry the emitted extension (`import { x } from './store.js'` for `store.ts`); under
  `bundler` they usually omit it. Match the files around you; mixing styles breaks one of the
  two build paths.
- ESM has no `__dirname`: derive it with
  `dirname(fileURLToPath(import.meta.url))` (`node:path`, `node:url`).
- Type-only imports use `import type`, so they vanish from the output and cannot create a
  runtime cycle.
- A JSON import uses the project's established form (`resolveJsonModule` or
  `with { type: 'json' }`); do not introduce a second one.
- `engines.node` is the runtime contract: using an API newer than it is a breaking change.
- A dependency goes in `dependencies` only if shipped code imports it; build and test tools go
  in `devDependencies`. The lockfile changes in the same change, through the package manager
  (`npm install <pkg>`), never by hand.
- A package with several `tsconfig` files (source, tests, a web app) is type-checked against
  each; a change that compiles under one and not another is not done.

### Cross-platform

The code runs on macOS, Linux, and Windows.

- File-system paths use `node:path` (`join`, `resolve`), never string concatenation with `/`.
  Paths derived from `import.meta.url` go through `fileURLToPath`.
- Home and config directories come from `os.homedir()` and the platform conventions, not a
  hard-coded `~` or `/Users`.
- Line endings are normalized on read where content is compared or hashed, or `.gitattributes`
  keeps checkouts LF.
- Paths are compared after `path.resolve`.
- `package.json` scripts avoid POSIX-only shell syntax (`VAR=x cmd`, `rm -rf`) that fails under
  `cmd.exe`; use a small `.mjs` script or a cross-platform tool the repository already uses.

## Coding standards

### Types

- No `any`. Data from outside the process — JSON, files, environment, HTTP bodies, rows from
  a store another program writes — enters as `unknown` and is narrowed by a validator or a
  type guard before use. Rows from a schema this repository owns are typed once, in the module
  that owns the query, by a row type that matches its `SELECT` list.
- No non-null assertion (`!`) and no `as` cast to paper over a value the compiler cannot prove.
  Narrow it, or handle the case. `as const` and `satisfies` are fine.
- Model states as discriminated unions rather than optional fields that are only valid together.
  A `switch` over a union ends in an exhaustiveness check (`const _exhaustive: never = value`).
- `JSON.parse(text) as Row` is not narrowing; it is a promise the data never breaks. Use the
  repository's validator library when the shape is large, or a hand-written guard
  (`value is Row`) that checks every field, narrowing keys with `in` and `typeof`.
- Replace `!` with a named decision: look the value up, and when it is `undefined`, throw an
  error that says what was missing and for which id.
- Exported functions declare their return types.
- `noUncheckedIndexedAccess` is on, or index results are treated as possibly `undefined`.
  `strict`, `exactOptionalPropertyTypes`, and `noImplicitOverride` stay on when the
  repository enables them; turning a new one on is its own change.

### Absent is not zero

A value that was not measured, not reported, or not parsed is **absent**, and stays absent all
the way to the surface that displays it.

- Type it as nullable: `tokens: number | null`.
- No `?? 0`, `|| 0`, `Number(x) || 0`, or `parseInt(...) || 0` on a measured quantity. `||`
  also erases a genuine `0`; use `??` only to supply a default that is *true* for the domain.
- Aggregates over a set with missing members say so: a sum, average, or rate either excludes
  the absent members and reports how many were excluded, or is itself absent. It never counts
  them as zero.
- A parse that fails returns an absent value or an error.
- Display layers render absence explicitly (`—`, "not reported"), not as `0`.
- `Number(raw)` and `parseInt` results are checked with `Number.isFinite` and kept `null` on
  failure; `|| 0` erases both `NaN` and a real `0`.
- A sum is `{ value: number | null; missing: number }`: `null` when no member reported, and
  `missing` counts the excluded members. An average divides by the count of present members,
  and is absent when that count is 0. `?? 0` is fine only to seed an accumulator once a real
  value exists, never to stand in for a missing one.
- A nullable column is `T | null` in the row type. `COALESCE(x, 0)` in SQL on a measured
  quantity is the same defect as `?? 0`.

### Data access

- **Select the columns you use.** No `SELECT *` in application code. Large text and JSON
  columns are read only by the query that needs them.
- **No N+1.** A query inside a loop over rows from another query is a defect; join, use
  `IN (...)` with bound parameters, or load the set once and index it in a `Map`. Build the
  placeholder list from `?` marks only and bind the values; an empty id set matches nothing, so return the
  empty result early without querying (`IN ()` is a syntax error); chunk id sets past the driver's bound-parameter cap (32,766 by
  default in SQLite), or use a join or temporary table.
- **Bound every read.** A query whose result can grow with history takes a `LIMIT`, a time
  window, or a cursor. Reading a whole table or file into memory needs a stated upper bound.
  Large inputs are streamed (`readline`, async iterators, `node:stream/promises` `pipeline`);
  `readFile` is not used on a file that grows without limit. Lists take `LIMIT`/`OFFSET` or a
  keyset cursor (`WHERE started_at < ? ORDER BY started_at DESC LIMIT ?`); analysis takes a
  time window from the caller; `.all()` on an unbounded query is the defect, `.iterate()`
  streams.
- A wide column (transcript text, raw JSON, blobs) is fetched by primary key, by the one query
  that displays or parses it, not carried by list queries.
- Statements that run repeatedly are prepared once, outside the loop. Multi-statement writes
  run in one transaction: `BEGIN`, then `COMMIT`, with `ROLLBACK` and a rethrow in the `catch`.
  Committing per row multiplies fsyncs.
- Look a varying identifier (sort column, table) up in a fixed object keyed by a union type
  (`SORT_COLUMNS[sortKey]`).
- One module owns each table's reads and writes; callers use its functions, not ad-hoc SQL.

### Errors and async

- Throw `Error` subclasses (or `new Error(message, { cause })`), never strings or plain
  objects. Callers branch on `instanceof` or a discriminant field, never on message text.
- A `catch` either handles the error, adds context and rethrows with `cause`, or converts it
  to a typed result. Catch variables are `unknown`.
- An error is handled once: returned, thrown, or logged — not logged and then rethrown.
- A fallback that hides a failure is an absent value disguised as data: a file that cannot be
  parsed yields "not parsed", not "zero rows". Fall back only with a recorded reason.
- Read a Node error code through a guard (`err instanceof Error && 'code' in err`), never
  `catch (e: any)`. `ENOENT` returns absent only when absence is the answer; rethrow the rest.
- `void promise` is allowed only when the promise already ends in a `.catch` that reports its
  failure; a comment says why the result itself is ignored.
  `@typescript-eslint/no-floating-promises` and `no-misused-promises` stay on.
- Independent awaits run concurrently (`Promise.all`, `Promise.allSettled`); concurrency over
  an unbounded set is capped, for example workers sharing one iterator over `items.entries()`.
  `Promise.allSettled` reports each rejected entry instead of dropping it.
- Long-running work accepts an `AbortSignal`. Timers and listeners started by a module are
  cleared by it. Processes exit through one shutdown path that drains in-flight work, bounded
  by a timeout.
- `process.exit` appears only in the CLI entry point; libraries return or throw.
- A module that starts an interval or a listener returns a way to stop it. Pass the signal to
  `fetch`, streams, and `node:timers/promises`; in a wait loop only an abort ends the wait
  quietly, any other error is rethrown. Shutdown aborts the controller, drains with a bounded
  `Promise.race` against an `unref`'d timer, then closes the server, from `SIGINT` and
  `SIGTERM` handlers.

## Testing

### Tests

Test authorship follows `<test-coding-standard>`. This standard requires that the code be
testable: dependencies passed in, no module-level mutable state, clocks injectable. Test files
(`*.test.ts`, `*.spec.ts`) are written by the developer tier that owns the task.

## Commands

### The compiler and the linter are the first reviewers

`tsc` and ESLint (with `typescript-eslint`) are authoritative for type and mechanical rules. Do
not restate those rules here, and do not merge code that fails them.

- A suppression (`// eslint-disable-next-line <rule>`, `// @ts-expect-error`) names the rule and
  says why on the same line. `@ts-ignore` and `@ts-nocheck` do not ship.
- **Naming:** `camelCase` values and functions, `PascalCase` types and classes,
  `SCREAMING_SNAKE_CASE` only for true compile-time constants. File names are `kebab-case.ts`.
- Exported functions and types carry a doc comment saying what a caller cannot see from the
  signature: units, ranges, and what an absent value means.

### Gates

Run every gate the standard lists for the package you touched, from that package's directory
or with `npm --prefix <dir> run <script>`: install (`npm ci`, lockfile unchanged), typecheck
(`tsc --noEmit` per tsconfig), lint (`eslint .`, no new suppression without a reason), build,
and the tests covering your change with a non-zero test count. A repository may declare more
(reachability, bundle size, a separate command for slow suites). Fix the cause of a finding
rather than suppressing it. When a gate could not run on an OS, name the OS in the completion
digest.

## Security and data handling

### Safety

- `child_process` uses `execFile`/`spawn` with an argument array. Interpolated input never
  reaches a shell (`exec`, `shell: true`).
- Paths built from input are resolved and checked to stay inside their root before use.
- Secrets come from the environment or a secret store.
- `npm audit --omit=dev` has no high or critical finding before a dependency upgrade merges.

## Done checklist

### Completion

Run the scripts the package declares; these are the shape they usually take. The same on macOS,
Linux, and Windows:

```bash
npm ci
npm run typecheck        # tsc --noEmit for every tsconfig in the package
npm run lint             # eslint .
npx prettier --check .   # prettier check (keep prettier or lint, as the package declares)
npm run build            # the build, when the package builds
npm test                 # vitest run (or the declared runner)
```

`npm run typecheck` compiles without emitting, so it catches a type error that a runner transpiles
past. `npx prettier --check .` is the format check where the package uses Prettier; a repository
that formats by ESLint rule has the same answer from `npm run lint`, and says so here. `npm run
build` is the build and proves the package as it will ship. The `npm test` line is the gate the
review council and CI run. A dev agent never runs the full suite; it runs the tests covering its
change (`npx vitest run <path>`).

A repository with several packages names each package's gates here.

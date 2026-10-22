---
id: standards/typescript
title: "TypeScript coding standard"
doc-type: coding-standard
status: draft
technology: typescript
owner: unassigned
last-reviewed: 2026-10-22
---

# TypeScript coding standard

How TypeScript is written for Node in this repository — CLIs, services, servers, data access,
and libraries. Agents and skills resolve this document as `<typescript-coding-standard>`.
React UI code (`.tsx`/`.jsx`, and the hooks and client state beside it) follows
`<react-coding-standard>` instead.

## Authority & status

When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal: an agent that resolves it says so and asks a human whether
to proceed on it, exactly as it does when no standard is declared.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Stack

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

## The compiler and the linter are the first reviewers

`tsc` and ESLint (with `typescript-eslint`) are authoritative for type and mechanical rules. Do
not restate those rules here, and do not merge code that fails them.

- A suppression (`// eslint-disable-next-line <rule>`, `// @ts-expect-error`) names the rule and
  says why on the same line. `@ts-ignore` and `@ts-nocheck` do not ship.
- **Naming:** `camelCase` values and functions, `PascalCase` types and classes,
  `SCREAMING_SNAKE_CASE` only for true compile-time constants. File names are `kebab-case.ts`.
- Exported functions and types carry a doc comment saying what a caller cannot see from the
  signature: units, ranges, and what an absent value means.

## Types

- No `any`. Data from outside the process — JSON, files, environment, HTTP bodies, rows from
  a store another program writes — enters as `unknown` and is narrowed by a validator or a
  type guard before use. Rows from a schema this repository owns are typed once, in the module
  that owns the query, by a row type that matches its `SELECT` list.
- No non-null assertion (`!`) and no `as` cast to paper over a value the compiler cannot prove.
  Narrow it, or handle the case. `as const` and `satisfies` are fine.
- Model states as discriminated unions rather than optional fields that are only valid together.
  A `switch` over a union ends in an exhaustiveness check (`const _exhaustive: never = value`).
- Exported functions declare their return types.
- `noUncheckedIndexedAccess` is on, or index results are treated as possibly `undefined`.

## Absent is not zero

A value that was not measured, not reported, or not parsed is **absent**, and stays absent all
the way to the surface that displays it.

- Represent it as `null` or `undefined` in the type (`tokens: number | null`), never as `0`,
  `''`, `[]`, or a placeholder date.
- No `?? 0`, `|| 0`, `Number(x) || 0`, or `parseInt(...) || 0` on a measured quantity. `||`
  also erases a genuine `0`; use `??` only to supply a default that is *true* for the domain.
- Aggregates over a set with missing members say so: a sum, average, or rate either excludes
  the absent members and reports how many were excluded, or is itself absent. It never counts
  them as zero.
- A parse that fails returns an absent value or an error — never a default that looks like data.
- Display layers render absence explicitly (`—`, "not reported"), not as `0`.

## Data access

- **Select the columns you use.** No `SELECT *` in application code. Large text and JSON
  columns are read only by the query that needs them.
- **No N+1.** A query inside a loop over rows from another query is a defect; join, use
  `IN (...)` with bound parameters, or load the set once and index it in a `Map`.
- **Bound every read.** A query whose result can grow with history takes a `LIMIT`, a time
  window, or a cursor. Reading a whole table or file into memory needs a stated upper bound.
  Large inputs are streamed (`readline`, async iterators, `node:stream/promises` `pipeline`).
- SQL is parameterized. Identifiers that must be dynamic come from a fixed allow-list.
- Statements that run repeatedly are prepared once. Multi-statement writes run in one
  transaction.
- One module owns each table's reads and writes; callers use its functions, not ad-hoc SQL.

## Errors and async

- Throw `Error` subclasses (or `new Error(message, { cause })`), never strings or plain
  objects. Callers branch on `instanceof` or a discriminant field, never on message text.
- A `catch` either handles the error, adds context and rethrows with `cause`, or converts it
  to a typed result. An empty `catch`, or one that logs and returns a default value, does not
  ship. Catch variables are `unknown`.
- An error is handled once: returned, thrown, or logged — not logged and then rethrown.
- No floating promises: every promise is awaited, returned, or explicitly passed to a handler
  that reports its failure. `void promise` needs a comment saying why the result is ignored.
- Independent awaits run concurrently (`Promise.all`, `Promise.allSettled`); concurrency over
  an unbounded set is capped.
- Long-running work accepts an `AbortSignal`. Timers and listeners started by a module are
  cleared by it. Processes exit through one shutdown path that drains in-flight work, bounded
  by a timeout.
- `process.exit` appears only in the CLI entry point.

## Safety

- `child_process` uses `execFile`/`spawn` with an argument array. Interpolated input never
  reaches a shell (`exec`, `shell: true`).
- Paths built from input are resolved and checked to stay inside their root before use.
- Secrets come from the environment or a secret store, never a literal. Do not log them.
- `npm audit --omit=dev` has no high or critical finding before a dependency upgrade merges.

## Cross-platform

The code runs on macOS, Linux, and Windows.

- File-system paths use `node:path` (`join`, `resolve`), never string concatenation with `/`.
  Paths derived from `import.meta.url` go through `fileURLToPath`.
- Home and config directories come from `os.homedir()` and the platform conventions, not a
  hard-coded `~` or `/Users`.
- Line endings are normalized on read where content is compared or hashed.

## Tests

Test authorship follows `<test-coding-standard>`. This standard requires that the code be
testable: dependencies passed in, no module-level mutable state, clocks injectable. Test files
(`*.test.ts`, `*.spec.ts`) belong to `test-dev`.

## Commands

Run the scripts the package declares; these are the shape they usually take. The same on macOS,
Linux, and Windows:

```bash
npm ci
npm run typecheck        # tsc --noEmit for every tsconfig in the package
npm run lint             # eslint .
npm run build            # when the package builds
npm test                 # vitest run (or the declared runner)
```

A repository with several packages names each package's gates here.

---
name: typescript-dev
description: Use when writing TypeScript for Node — CLIs, services, servers, data access, parsers, strict typing, absent values, async and error handling, or ESM modules. Do not use for .test.ts authorship, React UI, or CI/CD.
license: MIT
---

# TypeScript Developer

Typing, absent-value, data-access, error-handling, module, and gate decisions live in the path
declared as **<typescript-coding-standard>** in the repository root `AGENTS.md`. Read that
before the references below — this skill is procedure, not policy.

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Sub-Task | When to Use | Reference |
|---|---|---|
| Types and Absent Values | Narrowing `unknown` input, discriminated unions, exhaustiveness, removing `any`/`!`/`as`, keeping unmeasured values `null` instead of `0` | [Types and Absent Values](./references/types-and-absent-values.md) |
| Data Access | SQL against SQLite or another store, choosing columns, removing N+1 loops, bounding reads, streaming large files, prepared statements, transactions | [Data Access](./references/data-access.md) |
| Errors and Async | Error subclasses and `cause`, what a `catch` may do, floating promises, `Promise.all`, `AbortSignal`, timers, graceful shutdown | [Errors and Async](./references/errors-and-async.md) |
| ESM and Gates | `node:` imports, `.js` specifiers, `import.meta.url`, `package.json`/`tsconfig.json`, and running typecheck, lint, build, and test on macOS, Linux, or Windows before reporting done | [ESM and Gates](./references/esm-and-gates.md) |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.

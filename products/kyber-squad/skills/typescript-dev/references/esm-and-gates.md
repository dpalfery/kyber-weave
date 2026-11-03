---
name: typescript-dev/esm-and-gates
description: ESM and Node module conventions in TypeScript, package.json and tsconfig.json hygiene, and running typecheck, lint, build, and test on macOS, Linux, and Windows before reporting done.
---

# ESM and Gates

The module rules and the exact commands are in **<typescript-coding-standard>**. When the
standard names a command, run that one; the shapes below are what it usually takes.

## ES modules on Node

- Built-ins use the `node:` prefix: `import { readFile } from 'node:fs/promises'`.
- Relative import specifiers follow the project's `moduleResolution`. Under `NodeNext`/`Node16`
  they carry the emitted extension (`import { x } from './store.js'` for `store.ts`); under
  `bundler` they usually omit it. Match the files around you — mixing styles breaks one of the
  two build paths.
- There is no `__dirname` in ESM. Derive it:

  ```ts
  import { dirname } from 'node:path';
  import { fileURLToPath } from 'node:url';

  const here = dirname(fileURLToPath(import.meta.url));
  ```

- Type-only imports use `import type`, so they vanish from the output and cannot create a
  runtime cycle.
- A JSON import uses the project's established form (`resolveJsonModule`, or
  `with { type: 'json' }`); do not introduce a second one.

## `package.json` and `tsconfig.json`

- `engines.node` is the runtime contract. Using an API newer than it is a breaking change.
- A new dependency goes in `dependencies` only if the shipped code imports it; build and test
  tools go in `devDependencies`. Update the lockfile in the same change with the committed
  package manager (`npm install <pkg>`), never by hand.
- A package with several `tsconfig` files (source, tests, a web app) is type-checked against
  each. A change that compiles under one and not another is not done.

## The gates

| Gate | Usual command | Passes when |
|---|---|---|
| Install | `npm ci` | Exit 0, lockfile unchanged |
| Typecheck | `npm run typecheck` (`tsc --noEmit` per tsconfig) | Exit 0 |
| Lint | `npm run lint` (`eslint .`) | Exit 0, no new suppression without a reason |
| Build | `npm run build` | Exit 0 |
| Test | `npm test` (`vitest run`) | Exit 0 — authored by `test-dev`, but run it to prove you broke nothing |

A repository may declare more (a reachability check, a bundle-size check, a separate test
command for slow or serialized suites). Run every gate the standard lists for the package you
touched, from that package's directory or with `npm --prefix <dir> run <script>`.

Fix the cause of a finding. An `eslint-disable` or `@ts-expect-error` comment names the rule
and says why on the same line.

## Per platform

**macOS and Linux** — use the Node version from `engines.node` (via `nvm`, `fnm`, or the
standard's choice). Native dependencies need a C++ toolchain (Xcode command-line tools;
`build-essential`).

**Windows** — run the same `npm` scripts from PowerShell. Watch for:

- Scripts in `package.json` that use POSIX shell syntax (`VAR=x cmd`, `rm -rf`, `&&` inside
  `node -e` quoting). They fail under `cmd.exe`; prefer a small `.mjs` script or a cross-platform
  tool the repository already uses.
- Paths — `path.join`, never `'/'` concatenation; compare paths after `path.resolve`.
- Line endings — tests that compare file content normalize `\r\n` first, or a `.gitattributes`
  keeps checkouts LF.

When you could not run a gate on an OS, say which in the completion digest.

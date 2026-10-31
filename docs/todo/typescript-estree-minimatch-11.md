---
id: todo/typescript-estree-minimatch-11
title: Move typescript-estree off minimatch 10.2.6 when upstream requires 11.0.1
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-31
---

# Move typescript-estree off minimatch 10.2.6 when upstream requires 11.0.1

`dash/package-lock.json` carries two copies of npm `minimatch` that
[GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249) flags by version
range `< 11.0.1`:

| Node | Version | Required by | Range |
|---|---|---|---|
| `node_modules/minimatch` | 3.1.5 | `eslint@9` and its `@eslint/*` packages | `^3.1.5` |
| `node_modules/@typescript-eslint/typescript-estree/node_modules/minimatch` | 10.2.6 | `@typescript-eslint/typescript-estree` (via `typescript-eslint`) | `^10.2.2` |

The 3.1.5 copy predates `optimizationLevel` and `firstPhasePreProcess`, the code the advisory
describes, so it is a range false positive. That one needed no work.

The 10.2.6 copy does contain the vulnerable code, but nothing in this repository reaches it.
The only call site is `@typescript-eslint/typescript-estree/dist/useProgramFromProjectService.js`,
which `dash/eslint.config.js` never exercises: that config sets no `parserOptions.projectService`
and no `allowDefaultProject`, and the call there passes no `optimizationLevel`, so minimatch's
default of 1 applies where the advisory needs 2 or more. The patterns are repository-owned.

Deferred rather than patched because the available change was disproportionate: no 10.x release
is patched (10.2.6 is the last of the line), so any fix forces minimatch 11 past
typescript-estree's declared `^10.2.2`, adds a transitive package
(`@isaacs/nonrecursive-glob-match`), and cannot be proven behaviour-preserving by any gate —
lint would only prove that minimatch 11 loads. No open Dependabot alert covers this copy.
Full reasoning and rejected options: the archived plan
[2026-10-31-minimatch-globparts-advisory](../archive/plans/2026-10-31-minimatch-globparts-advisory.md)
(D2-R1).

## When to pick this up

- **Reopen trigger.** A `typescript-eslint` release whose
  `@typescript-eslint/typescript-estree` declares `minimatch` at `>= 11.0.1`. Then run
  `npm update --prefix dash typescript-eslint` and the dash gates
  (`dash-typecheck`, `dash-lint`, the dash test gate, `check:reachable`), and confirm
  `npm audit --prefix dash --json` no longer lists the typescript-estree node.
- **Escalation trigger.** Dash enables `parserOptions.projectService` with
  `allowDefaultProject`, or a future advisory reaches the default `optimizationLevel`.
  Either makes the call site live, and the scoped-override option in D2-R1 (B) becomes the
  right answer.

Use the parent-scoped override form only:
`"overrides": { "@typescript-eslint/typescript-estree": { "minimatch": "^11.0.1" } }` —
never a top-level `minimatch` override, which would force 11.x onto eslint 9's `^3.1.5`
consumers. Never run `npm audit fix`: it proposes `eslint@4.0.0`, a semver-major downgrade.

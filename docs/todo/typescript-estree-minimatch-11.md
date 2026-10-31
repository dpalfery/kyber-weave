---
id: todo/typescript-estree-minimatch-11
title: Remove the dash minimatch override once eslint and typescript-eslint accept 11
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-31
---

# Remove the dash minimatch override once eslint and typescript-eslint accept 11

`dash/package.json` carries a top-level `overrides` entry that forces `minimatch` to `^11.0.1`
(lockfile: 11.0.3) for every consumer in the package:

```json
"overrides": {
  "minimatch": "^11.0.1"
}
```

It is there for [GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249)
(CVE-2026-118764), which flags every minimatch below 11.0.1, and it replaced two lockfile
copies that no longer exist: 3.1.5 from `eslint@9` and its `@eslint/*` packages, and 10.2.6
from `@typescript-eslint/typescript-estree`.

The override sits above what those packages declare — eslint 9 asks for `^3.1.5`,
typescript-estree for `^10.2.2` — which is why it should not outlive their own movement.
The measured basis for it is in the archived plan
[2026-10-31-minimatch-globparts-advisory](../archive/plans/2026-10-31-minimatch-globparts-advisory.md)
(D2-R2): both eslint consumers reach minimatch only through the `Minimatch` class, which 10
and 11 still export, and `ts-lint` over the whole dash tree is the behaviour check.

## When to pick this up

- **Removal trigger.** An `eslint` release whose minimatch range admits `>= 11.0.1`, and a
  `typescript-eslint` release whose `@typescript-eslint/typescript-estree` requires
  `>= 11.0.1`. Check with
  `npm view eslint dependencies.minimatch` and
  `npm view @typescript-eslint/typescript-estree dependencies.minimatch`.
- **Partial trigger.** Only one of the two has moved. Keep the override for the other and
  narrow it to that parent rather than dropping it, so the finding does not come back.
- **Escalation trigger.** Lint or typecheck starts failing after an eslint or minimatch
  upgrade in a way that points at glob matching, which would mean the pinned major is now
  the problem. Then re-read D2-R2 before choosing a target version.

## Removing it

1. Delete the `overrides` entry from `dash/package.json`.
2. `npm install --prefix dash`.
3. Confirm one deduped `minimatch` at 11.0.1 or later: `npm ls --prefix dash minimatch`.
4. Confirm `npm audit --prefix dash --json` still reports zero vulnerabilities.
5. Run the dash gates: `ts-typecheck`, `ts-lint`, `ts-test`, `ts-reachable` via
   `dotnet run --project src/KyberWeave.Cli -- review gates . --paths dash/package.json`.

Every npm and gate command goes through `scripts/proc-guard.sh run -- <command>`, from the
repository root. Do not use `npm audit fix`: it still proposes `eslint@4.0.0`, a semver-major
downgrade.

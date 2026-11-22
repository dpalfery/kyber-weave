---
id: standards/typescript/skills
title: "typescript skills"
doc-type: skill
status: draft
technology: typescript
owner: unassigned
last-reviewed: 2026-11-22
---

# typescript skills

## What this folder covers

- Standards, in `typescript.standards.md`: types and absent values, data access, errors and
  async (Coding standards); ESM, packages and cross-platform (Naming and layout); the gates
  and per-platform notes (Commands).
- Procedure: run `npm ci`, typecheck, lint, build and the covering tests in that order, from
  the package directory; on macOS and Linux use the Node version in `engines.node` (native
  dependencies need Xcode command-line tools or `build-essential`); on Windows run the same
  `npm` scripts from PowerShell.
- Library documentation for any package you configure.

## Hand-off

When ceding work to delivery, provide the Node version, install, build and test commands,
and any native dependency.

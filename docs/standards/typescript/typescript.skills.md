---
id: standards/typescript/skills
title: "typescript skills"
doc-type: skill
status: current
technology: typescript
owner: dpalfery
last-reviewed: 2026-11-22
---

# typescript skills

## Commands

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash test
npx --prefix dash playwright test --config dash/playwright.isolated.config.ts
```

Every command runs directly through `npm`/`npx`. Never start the dashboard to run a test, and
Playwright only with the isolated config.

## Procedure

- Run `npm --prefix dash ci`, then typecheck, lint, the covering tests and `check:reachable`,
  in that order; the Node version is the one in `dash/package.json` `engines.node`.
- Native dependencies need the Xcode command-line tools on macOS or `build-essential` on
  Linux. On Windows run the same `npm` scripts from PowerShell.

## Boundaries

- Read `dash/AGENTS.md` for the web app's gates and `dash/tray/AGENTS.md` for the tray's.
- A shared type crosses the API in a contract module; do not duplicate its shape in a component.

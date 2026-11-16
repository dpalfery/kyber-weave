---
id: standards/typescript/skills
title: "typescript skills"
doc-type: skill
status: current
technology: typescript
owner: dpalfery
last-reviewed: 2026-11-16
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

## Boundaries

- Read `dash/AGENTS.md` for the web app's gates and `dash/tray/AGENTS.md` for the tray's.
- A shared type crosses the API in a contract module; do not duplicate its shape in a component.

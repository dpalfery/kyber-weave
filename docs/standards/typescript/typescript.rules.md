---
id: standards/typescript/rules
title: "typescript rules"
doc-type: rule
status: current
technology: typescript
owner: dpalfery
last-reviewed: 2026-11-22
---

# typescript rules

## KyberDash is a display layer

- The web dashboard and the tray display one engine's data over one `/api/kyber/*` API. They
  hold no feature logic, scheduler or job of their own.
- A capability that needs computation belongs in the engine, not in a component.
- The tray crate and the web app share the engine; do not add a second datastore or a second
  API surface.

## Types

- `strict` is on. An unmeasured value stays `null` or `undefined`; never turn it into `0`,
  `''` or a default that looks like data.

## Errors and async

- No empty `catch`, and none that logs and returns a default. No floating promise: await it,
  return it, or attach a handler that reports its failure.

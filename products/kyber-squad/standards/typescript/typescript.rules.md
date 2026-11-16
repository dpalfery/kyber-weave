---
id: standards/typescript/rules
title: "typescript rules"
doc-type: rule
status: draft
technology: typescript
owner: unassigned
last-reviewed: 2026-11-16
---

# typescript rules

## Boundaries

- Agree the contract a React UI consumes before editing a `.tsx` file; edit it only where
  the task names it.
- An unmeasured value stays `null` or `undefined`. Never turn it into `0`, `''` or any
  default that looks like data.

## Security and data handling

- Validate a value from outside the trust boundary once, at its entry point, before it is
  parsed, stored or passed to a file or collection API.

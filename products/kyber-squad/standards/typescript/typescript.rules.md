---
id: standards/typescript/rules
title: "typescript rules"
doc-type: rule
status: draft
technology: typescript
owner: unassigned
last-reviewed: 2026-11-22
---

# typescript rules

## Boundaries

- Agree the contract a React UI consumes before editing a `.tsx` file; edit it only where
  the task names it.
- An unmeasured value stays `null` or `undefined`. Never turn it into `0`, `''` or any
  default that looks like data.

## Errors and async

- No empty `catch`, and none that logs and returns a default. No floating promise: await it,
  return it, or attach a handler that reports its failure.

## Security and data handling

- SQL values are always bound parameters; an identifier that varies comes from a fixed
  allow-list, never from input.

- Validate a value from outside the trust boundary once, at its entry point, before it is
  stored or passed to a file or collection API. For structured input, that check parses it
  against a schema that types every field, nested ones included.

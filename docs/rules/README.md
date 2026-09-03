---
id: rules/index
title: Rules
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-09-03
---

# Rules

Repository-wide rules that govern how the system is built, independent of any one technology.
A rule about how C# is written belongs in the [C# coding standard](../standards/csharp/README.md);
a rule about how tests are written belongs in the [test coding standard](../standards/test/README.md);
a rule that would still hold if this repository were rewritten in another language belongs
here.

## Inventory

- [`rules/feature-runbooks`](feature-runbooks.md) — Mandates that every component declared with `Type: Feature` in the component catalog provides a companion local run/test runbook or declares an explicit non-executable rationale.

The repository's non-negotiables currently live in the root [`AGENTS.md`](../../AGENTS.md),
which is where a contributor and an agent both look first. A rule earns its own document when
it needs more than a paragraph to state — the reasoning behind it, what it costs, and what to
do at the edges.

## Writing one

Frontmatter is `doc-type: rule`, which requires only the base keys. A rule document is current
guidance and carries full retrieval authority, so it says what to do rather than describing
how something works — that is what `architecture` is for.

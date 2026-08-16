---
id: adr/index
title: Architecture decision records
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-16
---

# Architecture decision records

One record per architectural decision: what was decided, the alternatives that were rejected,
and why. An ADR is never edited to say something else — a decision that changes is recorded in
a new ADR that supersedes the old one, and the old one keeps its `id` so the documents that
cite it still resolve.

## Inventory

_No ADR has been written yet._

Decisions in this repository have so far been recorded as `<remarks>` blocks at the point the
choice is made, and as plans under [`plans/`](../plans/README.md). That works while a decision
has one obvious home in the code; an ADR earns its place when the decision spans components,
or when the rejected alternatives are the valuable part.

## Writing one

Frontmatter is `doc-type: adr`, which requires only the base keys. Cite it from the documents
it decided with `decided-by: [<id>]`, and supersede a previous record with
`supersedes: [<id>]` — both are validated, so a reference to a record that does not exist
fails `KW-DOC-SPEC-006`.

Superseded records move to `archive/adrs/`, which is outside the corpus and never returned as
current guidance.

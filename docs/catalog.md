---
id: catalog
title: Component and owner catalog
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-08-12
---

# Component and owner catalog

This table is the **authoritative vocabulary** for the `component` and `owner` frontmatter
keys. A document naming a component that has no row here fails `KW-DOC-SPEC-004`; the
check exists so that a component cannot be invented one document at a time until nobody
can say how many there are.

Adding a row is a deliberate act. It declares that a unit of the system exists, who
answers for it, and where its source lives.

| Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
|---|---|---|---|---|---|---|---|
| DocGraph | Feature | `src/KyberWeave.Core/Docs` | The documentation ontology, conformance gates, graph-first claim analysis, managed terminology, and retrieval graph served over MCP. | [docgraph/architecture.md](docgraph/architecture.md) · [docgraph/analysis.md](docgraph/analysis.md) | dpalfery | 2026-08-12 | current |
| ContextHygiene | Feature | `src/KyberWeave.Core/Skills` | Governance for the artifacts that shape an agent's context: Agent Skills and harness agent definitions. | [context-hygiene/skills.md](context-hygiene/skills.md) | dpalfery | 2026-08-01 | current |
| CI Pipelines | Feature | `src/KyberWeave.Core/Diagnostics` | The diagnostic engine every gate reports through: stable rule ids, severity gating, and SARIF. | [ci-pipelines/architecture.md](ci-pipelines/architecture.md) | dpalfery | 2026-08-01 | current |
| Distribution | Supporting | `scripts` | Self-contained platform binaries and the install path that places them. | [install.md](install.md) | dpalfery | 2026-08-01 | current |

## How the columns are read

Only two columns are parsed: **Component** (index 1) and **Owner** (index 6), counting
the empty cell produced by the leading pipe. The remaining columns are for human readers
and may be reordered or reworded freely — but moving Component or Owner requires the
matching `ontology.catalog` override in [`.kyber-weave/kyber-weave.yml`](../.kyber-weave/kyber-weave.yml),
or every document in the corpus will fail validation at once.

Rows whose first cell is empty, begins with `---`, or reads exactly `Component` are
skipped, which is what lets the header and separator rows coexist with data.

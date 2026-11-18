---
id: archive/specs/interface-driven-unit-tests/inventory/index
title: Inventory for the interface-driven unit tests specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-11-18
component: CI Pipelines
documentation-impact: no-design-change
---

# Inventory

Read-only inventory of every class in `src/` and every test class in `tests/`, made on 2026-11-17 for
this specification. Each file lists, per class, direct use of outside resources, concrete service
dependencies, services built with `new`, and the interface that should replace each.

| File | Scope |
|---|---|
| [core.md](core.md) | `Domain`, `Contracts`, `Kernel`, `Core`, `Core.Docs`, `Core.Squad` |
| [application.md](application.md) | `Application` |
| [data.md](data.md) | `Data` |
| [hosts.md](hosts.md) | `Cli`, `Mcp`, `Arbiter`, `Hooks` |
| [tests.md](tests.md) | Every test project, classified by the missing seam that keeps each test off memory |
| [domain-move.md](domain-move.md) | Every `Domain` file as data (stays) or logic (moves), the callers of that logic, and the move in 24 tasks |

## Decisions

| ID | Question | Answer / decision | Status |
|---|---|---|---|
| INV-1 | What does this inventory record? | The class-by-class evidence the specification was written from. It is not current guidance; the seams register carries the outcome. | Closed |

## Design harvest

Documentation waiver — nothing survives from this document, because it is the evidence read when the
specification was written, not a design decision or an operational rule.

## Closeout mapping

| ID | Destination | Documentation impact | Status |
|---|---|---|---|
| INV-1 | [Seams register](../../../../reference/seams.md) | no-design-change | archived |
| INV-1 | [Kyber-Squad architecture](../../../../kyber-squad/architecture.md) | no-design-change | archived |
| INV-1 | [Kyber Arbiter architecture](../../../../kyber-arbiter/architecture.md) | no-design-change | archived |
| INV-1 | [Documentation ontology](../../../../documentation-ontology.md) | no-design-change | archived |
| INV-1 | [Review council architecture](../../../../code-review/architecture.md) | no-design-change | archived |

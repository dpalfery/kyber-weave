---
id: specs/interface-driven-unit-tests/inventory/index
title: Inventory for the interface-driven unit tests specification
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
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

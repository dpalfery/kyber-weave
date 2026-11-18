---
id: specs/interface-driven-unit-tests/index
title: Interface-driven unit tests specification
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
documentation-impact: extends-component
impact-components: [CI Pipelines, KyberSquad, KyberArbiter, DocGraph, ReviewCouncil]
---

# Interface-driven unit tests specification

**Status:** Approved by dpalfery, 2026-11-17 (requirements, design and tasks).

Completes decision D5 of the layered-refactor completion specification: every product behaviour is
testable by an in-memory unit test written against interfaces, outside resources sit behind a small
set of seams implemented by thin adapters, each fake is proven against its adapter by one shared
contract suite, and integration tests become a safety net run only in CI. The one allowed exception
is a sealed framework type with no interface.

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 10 requirements in EARS form |
| [Design](design.md) | Evidence, architecture, the seams, the in-memory file system, contract tests, what stays an integration test, phases, decisions D1 to D11, no open questions |
| [Tasks](tasks.md) | 31 tasks in five phases plus closeout, test-first |
| [Inventory](inventory/README.md) | Class-by-class evidence for every project and test project |

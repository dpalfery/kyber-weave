---
name: product-owner
description: Use when authoring a feature spec's requirements (EARS), technical design, or test-driven task list — the phase playbooks for the product-owner planning flow.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Product Owner Skill

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Phase | When to Use | Reference |
|---|---|---|
| Requirements phase | EARS-format requirements from a rough idea or vision doc | [Requirements Phase](./references/requirements-phase.md) |
| Design phase | Technical design from approved requirements | [Design Phase](./references/design-phase.md) |
| Tasks phase | Test-driven, traceable checkbox task list | [Tasks Phase](./references/tasks-phase.md) |
| Closeout phase | Tasks are delivered and their tests pass; retire the specification | [Closeout Phase](./references/closeout-phase.md) |

**Rule:** Read only the reference relevant to your current phase.

## A specification has a shelf life

A specification records what was *intended*, so it goes stale the moment the implementation diverges from it — exactly like a plan. It is never canonical guidance, and an agent answering from one is quoting a proposal rather than the system.

That makes closeout part of the flow, not an afterthought. A specification whose tasks are done but which still sits in `6-Docs/specs/` is a live trap: it reads as current and it is not. Every specification this skill authors SHALL carry a final closeout task, and the task list is not complete without one.

Both the active specification set and the archive register live in the path declared as **Specification Index** in the repository root `AGENTS.md` Config Registry. Read it before opening any specification.

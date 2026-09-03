---
name: product-owner
description: Use when authoring or revising one formal feature-spec phase—EARS requirements, technical design, mode-aware implementation tasks, or closeout—and return a headless digest to conductor. Do NOT use for bounded implementation plans or delivery execution.
license: MIT
---

# Product Owner Skill

Resolve **<specification-index>** and **<docs-root>** through the repository root `AGENTS.md` Config Reg. The active specification directory is the directory containing **<specification-index>**; its archive location and inventory rules are declared by that index. Never assume a fixed documentation root.

Read the specification index before opening a feature artifact. Then identify the assigned phase and read only its reference:

| Phase | Use | Reference |
|---|---|---|
| Requirements | Turn a feature idea into numbered EARS requirements | [Requirements phase](./references/requirements-phase.md) |
| Design | Design against approved requirements and report gaps | [Design phase](./references/design-phase.md) |
| Tasks | Produce traceable tasks and the selected development-mode contract | [Tasks phase](./references/tasks-phase.md) |
| Closeout | Verify delivery, migrate durable facts, and archive the spec | [Closeout phase](./references/closeout-phase.md) |

## Headless phase contract

This skill never prompts the user and never runs an approval gate. Persist the assigned artifact and return its digest to `conductor`, which relays `GAPS` and `OPEN_QUESTIONS` and returns user decisions in a later invocation.

Normal phase work returns one of these exact status markers:

- `STATUS: READY_FOR_REVIEW`
- `STATUS: REQUIREMENTS_GAP`
- `STATUS: DESIGN_GAP`
- `STATUS: PHASE_APPROVED`
- `STATUS: SPEC_READY`
- `STATUS: SPEC_FINALIZED`
- `STATUS: SPEC_WRITE_ERROR`

Every review or gap digest includes:

```text
STATUS: READY_FOR_REVIEW | REQUIREMENTS_GAP | DESIGN_GAP
PHASE: requirements | design | tasks
ARTIFACT: <path resolved from <specification-index>>
SUMMARY: <concise persisted outcome>
GAPS: <blocking gaps, or none>
OPEN_QUESTIONS: <items for conductor to relay, or none>
```

The agent records phase approval only when the conductor supplies explicit user approval. A specification is Ready only after all phases are approved, the task artifact records `development-mode: test-first | standard`, the matching Test or verification contract is approved, and the conductor returns final **approve and execute** authorization.

## Shelf life

A specification records intent, not current behavior. Every tasks artifact ends with a `docs-dev` closeout task. Delivered specifications must migrate durable facts into canonical documentation before the specification is archived; the archive is never cited as current guidance.

---
name: product-owner
description: Use when authoring or revising one formal feature-spec phase—EARS requirements, technical design, mode-aware implementation tasks, or closeout—and return a headless digest to conductor. Do NOT use for bounded implementation plans or delivery execution.
license: MIT
---

# Product Owner Skill

Resolve **<specification-index>** and **<docs-root>** through the repository root `AGENTS.md` Config Reg. The active specification directory is the directory containing **<specification-index>**; its archive location and inventory rules are declared by that index. Never assume a fixed documentation root.

Read the specification index before opening a feature artifact. Load [spec authoring](./references/spec-authoring.md) before creating or resuming a phase file. Then identify the assigned phase and read only its reference:

| Phase | Use | Reference |
|---|---|---|
| Requirements | Turn a feature idea into numbered EARS requirements | [Requirements phase](./references/requirements-phase.md) |
| Design | Design against approved requirements and report gaps | [Design phase](./references/design-phase.md) |
| Tasks | Produce traceable tasks and the selected development-mode contract | [Tasks phase](./references/tasks-phase.md) |
| Closeout | Verify delivery, migrate durable facts, and archive the spec | [Closeout phase](./references/closeout-phase.md) |

## Discovery order

Use the host's indexes before raw reads. Load the CodeGraph and kyber-weave documentation tools first, through the harness tool search when they are deferred.

- Code: `codegraph_explore` first. It returns verbatim source with callers and blast radius, so do not re-read what it returned.
- Documentation: `docs_explore`, `docs_for_symbol`, or `docs_glossary` before searching or opening documentation files.
- Read or search a file directly only for what an index does not cover (configuration, infrastructure, files the docs index missed), when a tool reports the file stale, or to confirm one detail it did not return. When a tool or index is unavailable, fall back to direct reads and say so.
- Report discovery in your final report: `Discovery: codegraph N, docs N, direct reads N`, with a reason for each direct read of indexed code.

When the conductor's packet carries `autonomy: autonomous`, also read [autonomous decisions](./references/autonomy-decisions.md); it governs open decisions in every phase.

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
Discovery: codegraph N, docs N, direct reads N
DIRECT_READ_REASONS: <path and reason for each direct read of indexed code, or none>
```

Supervised behaviour is unchanged. The agent records phase approval only when the conductor supplies explicit user approval, with a single exception: under resolved autonomy `autonomous`, an invocation carrying the conductor's explicit `AUTONOMOUS_APPROVAL: <gate id>` input counts as that approval and is recorded with provenance `autonomous: recommendation accepted`, never inferred from silence or from the mode alone. A specification is Ready only after all phases are approved, the task artifact records `development-mode: test-first | standard`, the matching Test or verification contract is approved, and the conductor returns final **approve and execute** authorization.

The tasks-to-execute gate collapse is declared by the conductor spec-path contract: a tasks `STATUS: READY_FOR_REVIEW` with no gaps and no open questions may be presented as one consolidated review covering tasks approval and approve and execute. Supervised runs keep separate gates.

## Shelf life

A specification records intent, not current behavior. Every tasks artifact ends with a `docs-dev` closeout task. Delivered specifications must migrate durable facts into canonical documentation before the specification is archived; the archive is never cited as current guidance.

---
id: adr/0005-task-level-fast-review
title: Deterministic Agent Exit Gate Ahead of the Council
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-20
---

# ADR 0005: Deterministic Agent Exit Gate Ahead of the Council

## Status

Accepted

Amended 2026-10-20: the per-task LLM audit roughly tripled task time; a deterministic exit gate replaces it.

## Context

[ADR 0002](0002-three-layer-review-council-verdict-engine.md) replaced a single serial review
prompt with a three-layer council: fifteen specialist lenses over the diff, an adversarial
refutation pass on every `major` or `critical` finding, the host's full declared gate suite, and
a deterministic verdict engine. That design answers one question — may this change be committed —
and its cost is proportionate to that question.

Workers also needed an answer at the end of each task: did this developer finish the task it was
given, to the standard this repository declares? An LLM reviewer run on every task answered that
question, but the per-task audit roughly tripled task time, and the council answers the merge
question once per run regardless. The per-task question is answerable from deterministic checks
and the worker's completion digest, so it no longer needs an LLM pass.

## Decision

1. **Developer agents run a deterministic exit gate before they report `READY_FOR_REVIEW`.** The
   gate is the `task-gate` skill, configured under `review.task-gates` keyed by agent, and run
   with `kyber-weave review gates --agent <name>`. Only developer agents run it.

2. **The gate fixes, then verifies.** It applies the repository's declared fixers to the files the
   task changed, then runs its checks. A worker that fails a check fixes it and re-runs the gate.
   Two fix-and-rerun cycles are allowed. A gate that still fails after the second cycle trips the
   circuit breaker and the task is escalated to the conductor. Escalation does not start a council
   review.

3. **The conductor requires evidence.** It rejects a developer digest that lacks a `TASK_GATE:`
   line.

4. **`code-reviewer` is the single review.** It runs once at the end of the run, over all the
   code. The council never runs per task unless a human specifically asks. The `task-reviewer`
   agent is removed; no agent reviews a single task with an LLM.

5. **Reserved paths are reviewed at the end.** A task touching a `review.policy.always-human` path
   is flagged in the run report. The policy's `NEEDS_HUMAN` rule fires in the end-of-run review,
   on path alone, as it always did.

6. **ReSharper InspectCode stays in the once-per-run gate.** It is not part of the per-task gate.

This ADR does not supersede ADR 0002. The council, its lenses, its gates, and its verdict engine
are unchanged; the council is pointed at runs instead of tasks, and a deterministic exit gate runs
ahead of it.

## Alternatives Considered

- **A per-task LLM reviewer with a pass cap.** Rejected. It roughly tripled task time, and what it
  checked was already answered by the declared checks and the end-of-run council.

- **A cheaper council — fewer lenses, or a gate subset.** Rejected. `review gates` runs what the
  host declared, in declaration order, with no filter, and that is deliberate: a review citing a
  hand-picked subset is citing evidence it selected. Thinning the lens catalogue instead would
  degrade the one review that has to be trustworthy at merge.

- **Escalating a failing task to the council after its fix cycles.** Rejected. A per-task council
  run costs fifteen lenses and a full gate suite to settle a question about one task, and it
  recurs on every task that fails twice.

- **Reusing `APPROVE` / `REQUEST_CHANGES` / `NEEDS_HUMAN` for the per-task gate.** Rejected. The
  gate reports whether declared checks passed; a verdict is the verdict engine's, and reusing its
  words would give two producers of `APPROVE` with different confidence behind them.

## Consequences

- A developer task costs its declared checks and no reviewer agent. The worker is released when
  the gate passes or is escalated.
- Mechanical defects are corrected by the gate's fixers before the gate reports, so they cost
  nothing to report and need no reviewer pass.
- The council's cost per run is bounded and predictable: once, plus whatever a human asks for. It
  no longer scales with task count.
- Deep concerns (security, authorization, performance at scale, blast radius, duplication,
  analyzer triage, supply chain, coverage-backed test adequacy) are covered once, by the
  end-of-run council, rather than per task.
- The per-task RED/GREEN-ordering audit is no longer performed by an LLM. The council's
  test-adequacy lens covers part of it at the end of the run, and the conductor still mechanically
  checks that a RED digest exists before GREEN is accepted. This loss is accepted.
- The `TASK_GATE:` line is the conductor's evidence that the gate ran; a digest without it is
  rejected rather than trusted.

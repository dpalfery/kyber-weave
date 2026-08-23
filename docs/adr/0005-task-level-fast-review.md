---
id: adr/0005-task-level-fast-review
title: Task-Level Fast Review Ahead of the Council
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-08-22
---

# ADR 0005: Task-Level Fast Review Ahead of the Council

## Status

Accepted

## Context

[ADR 0002](0002-three-layer-review-council-verdict-engine.md) replaced a single serial review
prompt with a three-layer council: fifteen specialist lenses over the diff, an adversarial
refutation pass on every `major` or `critical` finding, the host's full declared gate suite, and
a deterministic verdict engine. That design is correct for the question it answers — may this
change be committed — and its cost is proportionate to that question.

Both conductors, and the `bug-crusher` skill, called it somewhere else entirely: the moment a
worker reported `READY_FOR_REVIEW` on any individual task. A two-file task therefore paid for
fifteen lens invocations, a twelve-gate suite that builds the solution, runs the test suite with
coverage, runs ReSharper InspectCode, and validates the entire documentation corpus, and a
verdict engine run. The cost was not wasted — it was simply spent answering a merge question
about work that was nowhere near merge.

Two distinct questions had been collapsed into one instrument:

1. **Did this worker finish the task it was given, to the standard this repository declares?**
   Answerable from the task's acceptance criteria, the worker's completion digest, the diff, and
   the standards under **<standards-root>**.
2. **May the accumulated change be committed?** Answerable only by the council, and only once
   the work is accumulated.

## Decision

Introduce `task-reviewer`, a standalone single-agent reviewer, and run every task up a
**three-pass ladder**:

| Pass | Reviewer | On failure |
|---|---|---|
| 1 | `task-reviewer` | Fix list returns to the worker — feedback round 1 |
| 2 | `task-reviewer` | Fix list returns to the worker — feedback round 2 |
| 3 | `code-reviewer` | Unresolved findings enter the conductor's findings collection; the task loop ends |

1. **The fast pass is one agent, not a council.** No fan-out, no gate suite, no verdict engine.
   It holds `capability-profile: investigator`, so it establishes its own scope with `git diff`
   and reads files — the conductors hold `process.execute: deny` and cannot hand it one.

2. **Its outcome is `PASS` or `FAIL`, never a verdict.** `APPROVE`, `REQUEST_CHANGES`, and
   `NEEDS_HUMAN` remain the verdict engine's, and a single agent cannot compute them — only feel
   them. Two vocabularies that cannot be confused on sight is the point: a `PASS` says this task
   is done to standard, and says nothing about merge.

3. **The escape hatch is a better reviewer, not another cycle.** A task that fails twice goes to
   `code-reviewer`, which has the lenses and the gate evidence the fast pass deliberately lacks.
   There is no third fast pass.

4. **Residual findings are tracked, then planned.** Findings surviving pass 3 enter a
   per-objective findings collection the conductor holds in task state. Before the objective's
   council review, the whole collection goes to `architect` for a solution and a plan; those
   tasks re-enter the ladder at pass 1.

5. **The council still runs, once per objective**, before any commit or pull request — and
   immediately, skipping the ladder, for any task touching a path
   `review.policy.always-human` reserves.

This ADR does not supersede ADR 0002. The council, its lenses, its gates, and its verdict engine
are unchanged; what changes is where they are pointed.

## Alternatives Considered

- **A cheaper council — fewer lenses, or a gate subset.** Rejected. `review gates` runs what the
  host declared, in declaration order, with no filter, and that is deliberate: a review citing a
  hand-picked subset is citing evidence it selected. Thinning the lens catalogue instead would
  degrade the one pass that has to be trustworthy at merge.

- **Reusing `APPROVE` / `REQUEST_CHANGES` / `NEEDS_HUMAN` for the fast pass.** Rejected. It is a
  drop-in for the conductors' existing branches, which is exactly the problem — two producers of
  `APPROVE` with materially different confidence behind them, indistinguishable in a transcript.

- **More fast passes before escalating.** Rejected. A defect the fast pass cannot get fixed in
  two rounds is usually one it cannot see; a third round samples the same limited view again.

- **A `task-reviewer` skill holding the procedure.** Rejected. `review-lens` and `review-triage`
  carry their whole procedure in the agent body; a fast pass that must load a second file to
  start is not fast.

## Consequences

- The common case — a task that is finished and follows the standard — costs one agent pass
  instead of a council run, and the worker is released immediately either way.
- Deep concerns (security, authorization, performance at scale, blast radius, duplication,
  analyzer triage, supply chain, coverage-backed test adequacy) are covered once per objective
  rather than once per task. `task-reviewer` names each of them as out of scope in every report, so
  a `PASS` cannot be read as clearance it did not give.
- Claims about executed behaviour are checked against the tree rather than against gate output at
  the per-task step. The fast pass reports an uncorroborated claim as unsupported, which is a
  weaker statement than the council's, and says so.
- The conductors carry a per-objective findings collection and an `architect` remediation step
  that did not exist before. The remediation plan passes through the existing approval gate.

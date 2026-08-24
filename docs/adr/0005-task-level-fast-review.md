---
id: adr/0005-task-level-fast-review
title: Deterministic Fixes and Task-Level Review Ahead of the Council
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-08-22
---

# ADR 0005: Deterministic Fixes and Task-Level Review Ahead of the Council

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

The two principles this record establishes are stated for day-to-day reference in the
[review index](../code-review/README.md#principles):

1. **`code-reviewer` reviews all the code at the end of the run; `task-reviewer` reviews single
   tasks.** The council never runs per task unless a human specifically asks.
2. **Review cost is a budget.** Cap the passes both reviewers make, and use deterministic tooling
   — the ReSharper command line tools and the Roslyn compiler checks — to find *and fix*
   formatting and code-quality issues before either reviewer is invoked.


Two changes, and the cheap one comes first.

### 1. A deterministic fix pass, before any reviewer

The worker's completion gate applies fixes rather than only measuring. Scoped with `--include` to
the files the task changed:

```bash
dotnet format <solution> --include <changed files>
dotnet format <solution> analyzers --include <changed files>
dotnet jb cleanupcode <solution> --profile="<cleanup profile>" --include="<changed files>"
```

Predefined type keywords, `var` where the standard forbids it, redundant qualifiers, unused
usings, fields that can be `readonly`, formatting — all corrected, never reported. The pass is
idempotent, so re-running it after a rework cycle is safe, and `--include` keeps the diff equal to
the task.

The profile fixes only what the repository already declares. Brace style with no `.editorconfig`
rule behind it, member reordering, and file layout stay out: those are the tool's opinion, and
reordering members in particular buries the change under churn a reviewer must read past.

`task-reviewer` is correspondingly **forbidden** from reporting anything those tools own. A
mechanical finding in its list means the gate did not run, and that single fact is the finding.

### 2. `task-reviewer` owns single tasks; `code-reviewer` owns the run

| Scope | Reviewer | Passes |
|---|---|---|
| One completed task | `task-reviewer` | up to 3 |
| The whole run, at the end | `code-reviewer` | once |
| One task, on explicit human request | `code-reviewer` | on demand |

1. **The fast pass is one agent, not a council.** No fan-out, no gate suite, no verdict engine.
   It holds `capability-profile: investigator`, so it establishes its own scope with `git diff` —
   the conductors hold `process.execute: deny` and cannot hand it one.

2. **Its outcome is `PASS` or `FAIL`, never a verdict.** `APPROVE`, `REQUEST_CHANGES`, and
   `NEEDS_HUMAN` remain the verdict engine's, and a single agent cannot compute them — only feel
   them.

3. **Nothing a task does summons the council.** Not a failed pass, not a reserved path, not a
   concern that looks serious. Each such route is a per-task council bill, and it is the bill
   this ADR exists to stop drawing. A `FAIL` on pass 3, or an `ESCALATION: end-of-run`, records
   the finding; it does not start a review.

4. **Residual findings are tracked, then planned.** The per-objective findings collection lives
   in the conductor's task state. Before the end-of-run review, the whole collection goes to
   `architect` for a solution and a plan; those tasks re-enter the ladder at pass 1.

5. **Reserved paths still escalate — at the end.** A task touching a
   `review.policy.always-human` path runs the ladder like any other and is flagged in the run
   report. The policy's `NEEDS_HUMAN` rule fires where it always did: in the end-of-run review,
   on path alone, before any finding is weighed.

This ADR does not supersede ADR 0002. The council, its lenses, its gates, and its verdict engine
are unchanged; what changes is that it is pointed at runs instead of tasks, and that a
deterministic layer now runs ahead of it.

## Alternatives Considered

- **A cheaper council — fewer lenses, or a gate subset.** Rejected. `review gates` runs what the
  host declared, in declaration order, with no filter, and that is deliberate: a review citing a
  hand-picked subset is citing evidence it selected. Thinning the lens catalogue instead would
  degrade the one pass that has to be trustworthy at merge.

- **Reusing `APPROVE` / `REQUEST_CHANGES` / `NEEDS_HUMAN` for the fast pass.** Rejected. It is a
  drop-in for the conductors' existing branches, which is exactly the problem — two producers of
  `APPROVE` with materially different confidence behind them, indistinguishable in a transcript.

- **Escalating to the council after two fast passes.** Rejected, and it is the change this record exists to correct: a per-task council run costs fifteen lenses and a full gate suite to settle a question about one task, and it recurs on every task that fails twice. A third fast pass costs one agent; a finding that survives it needs a plan, not a more expensive reader.

- **A `task-reviewer` skill holding the procedure.** Rejected. `review-lens` and `review-triage`
  carry their whole procedure in the agent body; a fast pass that must load a second file to
  start is not fast.

## Consequences

- The common case — a task that is finished and follows the standard — costs one agent pass
  instead of a council run, and the worker is released immediately either way.
- Mechanical defects cost nothing at all: no review pass to report them, no rework cycle to fix
  them, no confirmation pass. They are corrected before a reviewer is invoked.
- The council's cost per run is now bounded and predictable: once, plus whatever a human asks
  for. It no longer scales with task count, which is what made it expensive.
- Deep concerns (security, authorization, performance at scale, blast radius, duplication,
  analyzer triage, supply chain, coverage-backed test adequacy) are covered once per objective
  rather than once per task. `task-reviewer` names each of them as out of scope in every report, so
  a `PASS` cannot be read as clearance it did not give.
- Claims about executed behaviour are checked against the tree rather than against gate output at
  the per-task step. The fast pass reports an uncorroborated claim as unsupported, which is a
  weaker statement than the council's, and says so.
- The conductors carry a per-objective findings collection and an `architect` remediation step
  that did not exist before. The remediation plan passes through the existing approval gate.

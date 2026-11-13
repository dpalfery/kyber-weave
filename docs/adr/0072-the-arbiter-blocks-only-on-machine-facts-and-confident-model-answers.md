---
id: adr/0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers
title: The Arbiter blocks only on machine facts and confident model answers
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-12
component: KyberArbiter
decided-by: []
supersedes: []
code-refs:
  - ArbiterTriggerFamilies
  - ArbiterEvaluator
  - HookCommand
  - HeaderBlock
  - PlanFacts
  - GateReportFacts
  - TextExcerpts
  - ArbiterHookTimeout
---

# ADR 0072: The Arbiter blocks only on machine facts and confident model answers

## Status

Accepted, 2026-11-12. It replaces decisions 1, 3, 8 and 23 of
[ADR 0053](0053-kyber-arbiter-three-step-decision-gates.md) where they made the Arbiter block on
text it could not read or on its own failure; ADR 0053 is amended in place to match. The rule
ids, the ledger, the decision record schema and the three-step model are unchanged.

## Context

The Arbiter gates AI-agent delegations in a harness hook. Most of its delegate rules were
deterministic parsers of text an agent wrote: routing headers, plan markdown, task labels,
report markers and `TASK_GATE:` lines. When an agent deviated slightly from the exact format
(a header name in another case, a `PLAN_FILE` with a space or an absolute path, a marker on the
second line, a report path with a space) the parser missed, the rule answered `undecidable` or a
violating answer, and the conductor family escalated. Every escalation sent the dispatch to the
architect and back, and small changes stalled for about three hours on format mismatches that
named no real problem.

The authors of that text are probabilistic: a language model never writes a format exactly
every time. A deterministic judge of probabilistic output turns every small deviation into a
block. TypeSafe's Jev decision model is built for this judgment: it answers one narrow, typed
question over raw text with calibrated probabilities.

## Decision

1. **Step 0 decides only machine facts.** Deterministic `decide` clauses stay only for facts the
   machine produces itself: git diffs, files on disk, the harness payload and the embedded Squad
   catalog. Step 0 may also give the answers that let a dispatch proceed.
2. **Prose judgments are Jev questions with a threshold.** Every case where a deterministic
   reading of agent-written prose used to escalate is `undecidable` at step 0 and falls to one
   narrow step-1 `choice` question that reads the raw text (the dispatch head, the task text, the
   returned report). It escalates only when Jev answers the violating choice at or above the
   rule's `confidence-at-least` floor, 0.8 for every rule this change touches.
3. **Cannot decide means allow and log.** A parse miss, provider `none`, a provider error or
   timeout, a request over budget, an answer below its floor, and an exception in the evaluator,
   the hook host or the process all allow. `undecidable` maps to the proceeding effect in every
   trigger family. The ledger event and the decision record still say what happened, and the hook
   logs `KW-ARB-HOOK-001` for errors.
4. **Parsers keep what they dislike.** A header value that fails validation, a repeated name, a
   name in another case and a header line after the block are kept as raw values. A loose
   `PLAN_FILE` that resolves to a file inside the repository reads that plan, because the file
   on disk is a machine fact. No new strict format or regex gate is added.
5. **The provider timeout is checked against the hook.** When the timeout in force cannot fit
   the rendered hook timeout, the hook and `doctor` warn with `KW-ARB-CONFIG-016`. A killed hook
   proceeds undecided, so the operator needs the warning, not a failure.

## Alternatives considered

- **Loosen the parsers with more accepted spellings.** Rejected: each new spelling is another
  strict format, and the next deviation blocks again.
- **Keep failing closed and fix the agents' formatting.** Rejected: the authors are models, so
  the formatting will keep drifting, and every drift costs a run.
- **Escalate to a human on any uncertainty.** Rejected: it is the current behaviour and the
  cause of the stalls.
- **Gate on Jev's probability of the violating option instead of confidence.** TypeSafe advises
  gating a choice on the probability of the option code acts on. For a two-option choice,
  confidence is a monotone function of the top probability, so a confidence floor is the same
  gate; every question this change adds has two options. Rules with more options keep the
  existing floor until thresholds are measured.

## Consequences

- **A dispatch no model judges proceeds.** With provider `none`, a dispatch that names no plan,
  waits on an unfinished dependency or skips its failing tests is allowed and logged. Running a
  provider is now how prose rules enforce anything.
- **Machine-fact blocks remain.** `KW-ARB-ROSTER-001`, `KW-ARB-RUN-001` (added 2026-11-12: no run
  record is live for the repository), `KW-ARB-CHECKOUT-001` (added 2026-11-12: a write into, or a return
  that left changes in, the canonical checkout while the run works in its own worktree), `KW-ARB-BUDGET-001`
  (added 2026-11-12: a developer dispatch's own count of tool calls or minutes past `arbiter.dispatch-budget`),
  `KW-ARB-PLANNER-DIFF-001`,
  `KW-ARB-READONLY-001`, `KW-ARB-GATE-001`, `KW-ARB-LENS-002`, `KW-ARB-LADDER-001`, the
  report-versus-claim contradiction of `KW-ARB-GATE-REPORT-001`, `KW-ARB-DIFF-001`'s
  out-of-scope intersection and `KW-ARB-READY-001`'s in-flight overlap still escalate at step 0.
- **Two planner answers are retired.** `plan-file-invalid` and `plan-file-outside-planning-dirs`
  are no longer produced: the path a planner is told is prose, and where it writes is checked on
  its return by `KW-ARB-PLANNER-DIFF-001` against git.
- **Return tails are stored.** Each `delegate.returned` keeps the last 2,000 characters of the
  worker's report in the run store beside the plan, so the readiness and RED-evidence questions
  read what the worker said. The ledger still holds no text.
- **New thresholds are unmeasured.** The new questions ship at a 0.8 floor and are not in shadow,
  because blocking only on confident answers is already the safe direction. `arbiter tune` reads
  their scores from the decision log once runs produce them.

## Related

- [ADR 0053](0053-kyber-arbiter-three-step-decision-gates.md) — the three-step decision gates
- [Kyber Arbiter architecture](../kyber-arbiter/architecture.md#the-blocking-contract) — the blocking contract
- [The Jev decision model](../kyber-arbiter/jev-decision-model.md) — what Jev answers and how questions are written
- [Kyber Arbiter runbook](../kyber-arbiter/runbook.md#failure-behaviour-allow-and-log) — reading what was allowed undecided

---
id: code-review/index
title: Review council
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-20
---

# Review council

Parallel code review with an auditable verdict.

| Page | Covers |
|---|---|
| [Architecture](architecture.md) | The three layers, the two lens seats, the evidence schema, the verdict rules, permissions, configuration |
| [Rule reference](../ci-pipelines/rule-reference.md#code-review--the-review-council) | Every `KW-REVIEW-*` id |
| [ADR 0002](../adr/0002-three-layer-review-council-verdict-engine.md) | Three-layer review council and verdict engine decision record |
| [ADR 0005](../adr/0005-task-level-fast-review.md) | A deterministic developer exit gate for single tasks, the council once per run |
| [Plan](../archive/plans/2026-08-20-code-review-council.md) | Original plan: why it is shaped this way, and what was rejected |

## Principles

Two rules govern every decision in this area. Where anything below appears to conflict with
them, they win, and the thing that conflicts is the bug.

### 1. The council has one scope, and a task never summons it

**`code-reviewer` reviews all the code at the end of the run. A single task gets a deterministic
exit gate run by its own developer agent, never a reviewer. `code-reviewer` never runs on a
per-task basis unless a human specifically asks it to.**

Not on a failed task gate. Not on a path the policy reserves. Not on a concern that looks
serious. Every one of those is a per-task council bill — fifteen lenses and a full gate suite
spent to settle a question about a couple of files, and spent again on the next task. A task
that cannot converge produces a *finding*, and findings are collected, planned by `architect`,
and read once by the end-of-run review.

The test for any proposed routing change: does it let a single task cause a council run? If so
it is wrong, however reasonable the trigger sounds.

### 2. Review cost is a budget, and determinism is cheaper than judgement

**Reduce the passes the reviewer makes, and use deterministic tooling — the ReSharper command
line tools and the Roslyn compiler checks — to find *and fix* formatting and code-quality
issues before the reviewer is invoked.**

The ordering follows from the cost of being wrong at each layer:

| Layer | Cost of catching a defect here |
|---|---|
| Deterministic fix (`dotnet format`, analyzer code fixes, `cleanupcode`) | One command. The defect is corrected, not reported. |
| `code-reviewer` | The full council, plus the gate suite. |

A mechanical defect that reaches a reviewer costs a pass to write it up, a rework cycle to
apply the fix, and another pass to confirm it — to arrive at an edit `cleanupcode` would have
made for free. So the machine layer runs first, as the developer's exit gate, and a mechanical
finding in the council's review is evidence the gate did not run, and *that* is the finding.

The same logic caps the passes. The gate gets two rework cycles per key and the council gets one
run. A defect surviving that many looks at the problem, not at the reviewer, and the answer is
a plan rather than another read.

## The idea in one page

A single agent reading a whole diff is one attention budget spread across a dozen unrelated
concerns, and it produces a verdict that cannot be checked. This splits that into parts that
fail differently:

- **Fifteen lenses** read the diff in parallel, each owning one concern and each declaring an
  applicability predicate so it skips diffs holding nothing for it. A change to a Markdown file
  does not pay for a database-migration reviewer to tell it so.
- **The host's own gates** — build, tests, coverage, analyzers — run as declared commands and
  produce a repeatable artifact. That artifact is what a claim about executed behaviour is
  allowed to cite. Nothing else counts.
- **A verdict engine** turns findings and gate results into `APPROVE`, `REQUEST_CHANGES`, or
  `NEEDS_HUMAN` by fixed rule. It is ordinary unit-tested code, so the same inputs give the
  same answer twice and every decision names the rule that made it.

## Two layers of review, and the first is cheaper

The council answers "may this be committed". That is not the question you have after every
individual task, and paying the council's price to ask it was the problem this structure solves.

| Layer | Who | Scope | Cost |
|---|---|---|---|
| **Developer exit gate** | The developer agent running `task-gate`: `dotnet format`, analyzer code fixes, `cleanupcode --include`, build and tests | The files one task changed | No model at all |
| **Run review** | `code-reviewer` | The whole run, once at the end | The full council |

**The bottom layer has no model in it.** Each developer's exit gate applies fixes and then
verifies, before the change leaves the worker: predefined type keywords, `var` where the standard
forbids it, redundant qualifiers, unused usings, formatting, and the declared build and tests. It
reports a `TASK_GATE:` line; the conductor rejects a missing or failing line back to the same
worker, and two reworks for the same gate key trip the circuit-breaker into the conductor's
findings collection, which `architect` plans against before the run ends. A mechanical defect that
reaches the council means the gate did not run.

**The top layer runs once.** `code-reviewer` reviews the whole run at the end — and a single task
only when a human explicitly asks. Nothing a task does summons it: not a failed pass, not a
reserved path, not a concern that looks serious. Each of those would be a per-task council bill.

What the task layer does not cover, and names as uncovered in every report: security modelling,
authorization and tenancy, performance at scale, blast radius and revertibility, cross-file
duplication, analyzer triage, dependency supply chain, and test adequacy against coverage. The
council owns all of them, once, over everything.

## Three commands

```bash
kyber-weave review gates .                                          # run the declared gates
kyber-weave review duplicates .                                     # cluster duplicate bodies
kyber-weave review verdict . --findings findings.json --gates gates.json
```

Exit codes are distinct on purpose: `0` approve, `1` changes requested, `2` needs a human.
A change that merely touches a protected path must not look, to a caller reading the exit
code, like one the review rejected.

## What makes it trustworthy

**Findings must carry proof.** Every one needs a verbatim `excerpt`, an `evidence` pointer,
and a concrete `failure_scenario`. Anything missing one is dropped before it can influence the
verdict, and the lens that produced it is named.

**The policy outranks the engine.** Paths a repository reserves — authentication, secrets, and
here the capability profiles and agent instruction surfaces themselves — escalate to a human
on path alone, before any finding is weighed. The artifacts that decide what agents may do are
not approvable by an agent.

**Suppressions expire.** There is no permanent one. A lapsed suppression returns its finding
without anyone having to remember to go looking.

**Risk comes from findings, not size.** A twelve-line migration dropping a column outranks a
three-thousand-line generated-client refresh. Size appears only as an attention ceiling.

## Related

- [Kyber-Squad](../kyber-squad/README.md) — how the agents and skills are deployed
- [Skill governance](../context-hygiene/skills.md) — how the skills themselves are gated

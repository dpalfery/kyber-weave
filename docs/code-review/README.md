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
| [ADR 0005](../adr/0005-task-level-fast-review.md) | The three-pass ladder: why `task-reviewer` runs first and the council runs once per objective |
| [Plan](../archive/plans/2026-08-20-code-review-council.md) | Original plan: why it is shaped this way, and what was rejected |

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

## Two reviewers, one ladder

The council answers "may this be committed". That is not the question you have after every
individual task, and paying the council's price to ask it was the reason `task-reviewer` exists.

Each task climbs at most three passes:

| Pass | Reviewer | Answers | On failure |
|---|---|---|---|
| 1 | `task-reviewer` | Is the task finished, and does it follow the declared standards? | Fix list back to the worker |
| 2 | `task-reviewer` | Are those fixes actually in, and did they break anything? | Fix list back to the worker |
| 3 | `code-reviewer` | Everything the fast pass does not cover | Residual findings go to the conductor's findings collection |

`task-reviewer` is one agent: no lens fan-out, no gate suite, no verdict engine. It returns
**`PASS` or `FAIL`** and never a verdict — `APPROVE`, `REQUEST_CHANGES`, and `NEEDS_HUMAN` stay
the engine's, so the two outcomes can never be confused in a transcript. A `PASS` means the task
is done to standard; it says nothing about merge.

What the fast pass does not cover, and names as uncovered in every report: security modelling,
authorization and tenancy, performance at scale, blast radius and revertibility, cross-file
duplication, analyzer triage, dependency supply chain, and test adequacy against coverage. The
council owns all of them, and runs **once per objective** before any commit or pull request —
and immediately, skipping the ladder, on any change touching a path `always-human` reserves.

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

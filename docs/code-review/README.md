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
| [Plan](../plans/2026-08-20-code-review-council.md) | Why it is shaped this way, and what was rejected |

## The idea in one page

A single agent reading a whole diff is one attention budget spread across a dozen unrelated
concerns, and it produces a verdict that cannot be checked. This splits that into parts that
fail differently:

- **Thirteen lenses** read the diff in parallel, each owning one concern and each declaring an
  applicability predicate so it skips diffs holding nothing for it. A change to a Markdown file
  does not pay for a database-migration reviewer to tell it so.
- **The host's own gates** — build, tests, coverage, analyzers — run as declared commands and
  produce a repeatable artifact. That artifact is what a claim about executed behaviour is
  allowed to cite. Nothing else counts.
- **A verdict engine** turns findings and gate results into `APPROVE`, `REQUEST_CHANGES`, or
  `NEEDS_HUMAN` by fixed rule. It is ordinary unit-tested code, so the same inputs give the
  same answer twice and every decision names the rule that made it.

## Two commands

```bash
kyber-weave review gates .                                          # run the declared gates
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

---
id: kyber-arbiter/jev-decision-model
title: The Jev decision model
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-11-12
---

# The Jev decision model

**Jev answers the Arbiter's step-1 questions: one narrow, typed question about agent-written
text, with calibrated probabilities that rule code compares against a threshold.** It is the
judge for everything the Arbiter must read rather than look up
([ADR 0072](../adr/0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md)).

## What Jev is

Jev is [TypeSafe](https://typesafe.ai)'s decision model, which TypeSafe calls a System One
model. You send it **state** (text and application data) and **typed questions**; it returns
typed answers with probabilities, not generated prose. TypeSafe's guidance is to keep the
decision in code: Jev makes the judgments, and code combines them with thresholds into the
action. The Arbiter follows that split: a rule's `effects` map and its family combination are
the code, and each `ask` is one judgment.

Sources: [TypeSafe documentation](https://docs.typesafe.ai) (index at
[docs.typesafe.ai/llms.txt](https://docs.typesafe.ai/llms.txt)) and TypeSafe's
[agent skill](https://github.com/typesafe-ai/skills/blob/main/skills/typesafe-ai/SKILL.md).
The Arbiter's client for it is `SystemOneClient`; see [the architecture](architecture.md).

## Question types and what they return

| Type | Asks | Request carries | Answer carries |
|---|---|---|---|
| `choice` | Which one option of a fixed, unordered set fits | `instructions` and `criteria`, a map of option names to descriptions | `choice`, `probabilities` for every option, `confidence` |
| `score` | Where the input sits on ordered levels | `instructions` and `criteria`, the ordered levels | `score`, `probabilities` per level, `confidence` |
| `noul` | Whether a yes/no statement is true | `instructions`, optional `criteria` for yes and no | `noul`, the probability of yes; no `confidence` |

TypeSafe also offers `box` for locating something in an image; the Arbiter does not use it.

- **Probability** is the likelihood attached to each option. Treat it as the chance that option
  is right.
- **Confidence** (choice and score only) summarises how concentrated the probabilities are,
  from 0 (spread evenly) to 1 (all on one option). For a choice it is a function of the top
  probability and the number of options, so it adds no independent information.

Option names are sent to the model, so they are descriptive (`not-in-plan`, not `B`). Question
ids are not sent; the Arbiter keys each question by rule id.

## How the Arbiter writes questions

TypeSafe's guidance, as the Arbiter applies it:

- **One judgment per question.** A question a person could answer at a glance, about one
  observable thing. "And" and "or" conditions are split into separate questions.
- **The state passes the human test.** Could a capable person answer from this state alone?
  Each rule sends only its declared fields: the raw dispatch head, the task text, the returned
  report tail, plus the machine facts beside them (whether the plan file was found, the plan's
  task list). Irrelevant fields are left out because they dilute the answer.
- **Truncate only where a cut keeps the context.** Dispatch text is cut at its head, where the
  headers and instructions sit; a report at its tail, where the digest sits. Each excerpt is
  3,000 characters, so a batched request stays under the provider budget instead of going unasked.
- **Criteria describe concrete situations,** including the violating case, rather than a
  negation of the allowing case.
- **Questions are independent.** Questions in one batched call cannot see each other's answers,
  so each carries its own context.
- **Don't ask Jev for the final decision.** The rule asks "is this work one of the plan's tasks?",
  and the effect map decides that `not-in-plan` escalates.

## Thresholds

- **Every gating choice the Arbiter added has two options and a `confidence-at-least` floor of
  0.8.** With two options, confidence rises with the probability of the chosen option, so the
  floor is TypeSafe's recommended gate: act on the probability of the option code acts on.
- **Below the floor, the answer is `undecidable`, and `undecidable` allows.** Only a confident
  violating answer blocks. A provider error, a timeout, or a request over budget is
  `undecidable` too, never a "no".
- **For a `noul`,** TypeSafe suggests 0.7 for yes and 0.3 for no as starting points, with the
  band between treated as its own outcome. The Arbiter's `noul` rule (`KW-ARB-LENS-001`) uses a
  `probability-below` ceiling.
- **Risk sets the threshold.** An action that blocks needs a higher threshold than one that
  only advises. Thresholds are tuned from the scores the decision log records (`arbiter tune`),
  not fitted to a few cases.

## Related

- [Architecture: the blocking contract](architecture.md#the-blocking-contract)
- [Runbook: failure behaviour](runbook.md#failure-behaviour-allow-and-log)
- [ADR 0072](../adr/0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md)

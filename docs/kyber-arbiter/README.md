---
id: kyber-arbiter-index
title: Kyber Arbiter
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-11-12
---

# Kyber Arbiter

> **Gate Squad delegations and review fan-out through three-step decision gates enforced by
> harness hooks: exact facts are never subject to model judgement, and agent-written text is
> never subject to an exact parser. When it cannot decide, it allows and logs.**

The Squad's in-flight decisions used to be made by the model being governed: delegation scope
rested on the conductor's instructions, the ready queue on its attention, and review fan-out
spawned every lens whether it applied or not. The Arbiter answers those questions in up to
three steps — plain code for exact facts, a decision model for judgements about meaning, and
a reasoning agent for what the model flags or is unsure about — invoked by harness hooks at
the Squad's decision points.

## Scope

The engine is harness-neutral. Hooks deliver it on eleven harnesses in three phases:
**Claude, Copilot in VS Code, Copilot CLI and OpenCode** (Phase 1); **Pi, Codex and Cursor**
(Phase 2); and **Kilo, Antigravity, Factory and Devin** (Phase 3). **Warp and ZCode** have no
hooks and use the MCP fallback, `kyber-weave-arbiter serve`.

## Start here

- [Architecture](architecture.md) — engine, rules, configuration, and the harness table
- [The Jev decision model](jev-decision-model.md) — what the step-1 model answers, its question
  types, and how the Arbiter writes questions and thresholds
- [Runbook](runbook.md) — hooks, trust steps, failure behaviour (allow and log), `setup`, `doctor`,
  `audit`, and recording harness defects
- [ADR 0053](../adr/0053-kyber-arbiter-three-step-decision-gates.md) — the decisions behind it
- [ADR 0072](../adr/0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md) — it blocks only on machine facts and confident model answers
- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-ARB-*` id and `KW-REVIEW-026`
- [Configuration](../configuration.md) — the `arbiter:` section and gate `applies-when`

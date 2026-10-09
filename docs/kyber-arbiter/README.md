---
id: kyber-arbiter-index
title: Kyber Arbiter
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-10-09
---

# Kyber Arbiter

> **Gate Squad delegations and review fan-out through three-step decision gates enforced by
> harness hooks, so exact facts are never subject to model judgement.**

The Squad's in-flight decisions used to be made by the model being governed: delegation scope
rested on the conductor's instructions, the ready queue on its attention, and review fan-out
spawned every lens whether it applied or not. The Arbiter answers those questions in up to
three steps — plain code for exact facts, a decision model for judgements about meaning, and
a reasoning agent for what the model flags or is unsure about — invoked by harness hooks at
the Squad's decision points.

## Phase 1 scope

Phase 1 delivers the harness-neutral core and hooks for four harnesses: **Claude, Copilot in
VS Code, Copilot CLI, and OpenCode**. Later phases extend the same engine to further
harnesses and add the MCP fallback; this documentation describes only what Phase 1 delivers.

## Start here

- [Architecture](architecture.md) — engine, rules, configuration, and Phase 1 harness facts
- [Runbook](runbook.md) — hooks, trust steps, fail-closed behaviour, `setup`, `doctor`,
  `audit`, and recording harness defects
- [ADR 0028](../adr/0028-kyber-arbiter-three-step-decision-gates.md) — the decisions behind it
- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-ARB-*` id and `KW-REVIEW-026`
- [Configuration](../configuration.md) — the `arbiter:` section and gate `applies-when`

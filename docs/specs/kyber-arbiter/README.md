---
id: specs/kyber-arbiter/index
title: Kyber Arbiter specification
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-10-03
---

# Kyber Arbiter specification

**Status:** Requirements and design approved 2026-10-03. Tasks drafted for delivery Phase 1, with one open design question (Q15).

A rule engine, configured in `.kyber-weave/kyber-weave.yml` and invoked by harness hooks at the squad's decision points, with up to three steps of escalation: plain code answering exact facts, a decision model (TypeSafe JEV or local Ollama) answering judgements about meaning, and a reasoning agent answering what the model flags or is unsure about. Each rule's question is answered according to the TypeSafe escalation pattern, with one confident red flag enough to escalate—signals are not averaged.

Kyber Arbiter enforces the Squad's in-flight decisions:

- **Delegation scope:** whether a delegation stays inside the approved plan task's boundaries;
- **Ready queue:** whether dependencies are complete and file scope does not overlap work in flight;
- **Delegation rosters:** whether a dispatch targets an agent the caller is authorized to dispatch;
- **Review fan-out:** whether a lens applies, with high-confidence verification skipping refutation spawns.

| Document | Covers | Phase status |
|---|---|---|
| [Requirements](requirements.md) | Requirements 1–25 and decisions D1–D33 | Approved 2026-10-03; D30–D33 added the same day |
| [Design](design.md) | Architecture, the 18-rule catalogue, trigger classification, header grammar, plan parser contract, configuration and diagnostics, ledger and audit, escalation flow, per-harness facts, Squad rendering per phase, gate applicability, Req 25 enforcement, phase mapping, size, and sources | Approved 2026-10-03 |
| [Tasks](tasks.md) | Delivery Phase 1 tasks, Test contract, reference facts, dependency graph and concurrency audit (D32) | Draft; development mode test-first |

## Decisions and questions

The owner answered decisions D1–D33 between 2026-10-01 and 2026-10-03.

One question is open:

- **Q15 (design gap, from the tasks phase).** How do Claude dispatches report their return?
  - **The problem:** Claude now launches sub-agents in the background by default, and a sub-agent's result arrives through `SubagentHandback`. Neither reaches the dispatcher's `PostToolUse` hook.
  - **The recommendation:** (a), a hand-back hook on every dispatch target.
  - **What it holds up:** tasks 4.4 and 6.6.

D32 sets the delivery: each harness phase ships as its own PR. This specification's tasks cover Phase 1, and Phases 2 and 3 become todos at closeout.

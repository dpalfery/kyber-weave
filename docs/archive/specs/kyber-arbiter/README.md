---
id: archive/specs/kyber-arbiter/index
title: Kyber Arbiter specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-10-10
---

# Kyber Arbiter specification

**Status:** Archived
**Archive Date:** 2026-10-10

Delivered and closed out 2026-10-10: all three phases shipped, each as its own PR, and every requirement (1–25) and decision (D1–D34) traces to delivered evidence. The durable decisions live in [ADR 0028](../../../adr/0028-kyber-arbiter-three-step-decision-gates.md) and [ADR 0029](../../../adr/0029-squad-owned-blocks-in-shared-hook-files.md). The canonical documents are [`docs/kyber-arbiter/`](../../../kyber-arbiter/README.md) and the [Kyber-Squad](../../../kyber-squad/architecture.md) documents.

A rule engine, configured in `.kyber-weave/kyber-weave.yml` and invoked by harness hooks at the squad's decision points, with up to three steps of escalation: plain code answering exact facts, a decision model (TypeSafe JEV or local Ollama) answering judgements about meaning, and a reasoning agent answering what the model flags or is unsure about. Each rule's question is answered according to the TypeSafe escalation pattern, with one confident red flag enough to escalate—signals are not averaged.

Kyber Arbiter enforces the Squad's in-flight decisions:

- **Delegation scope:** whether a delegation stays inside the approved plan task's boundaries;
- **Ready queue:** whether dependencies are complete and file scope does not overlap work in flight;
- **Delegation rosters:** whether a dispatch targets an agent the caller is authorized to dispatch;
- **Review fan-out:** whether a lens applies, with high-confidence verification skipping refutation spawns.

| Document | Covers | Phase status |
|---|---|---|
| [Requirements](requirements.md) | Requirements 1–25 and decisions D1–D34 | Approved 2026-10-03; D30–D33 added 2026-10-03; D32 corrected and D34 added 2026-10-04 |
| [Design](design.md) | Architecture, the 18-rule catalogue, trigger classification, header grammar, plan parser contract, configuration and diagnostics, ledger and audit, escalation flow, per-harness facts, Squad rendering per phase, gate applicability, Req 25 enforcement, phase mapping, size, and sources | Approved 2026-10-03; delivery wording corrected 2026-10-04 |
| [Tasks](tasks.md) | All three delivery phases, each with its own documentation, verification and review, then one closeout (D32, D34); the Test contract, reference facts, dependency graph and concurrency audit | Draft; development mode test-first |

## Decisions and questions

The owner decided D1–D34 between 2026-10-01 and 2026-10-04. Three questions are open, and the [task list](tasks.md#pending-decisions) holds their evidence and options:

| Question | What is open | Recommendation | Waiting on it |
|---|---|---|---|
| Q15 | How Claude dispatches report their return. Background sub-agents and `SubagentHandback` bypass the dispatcher's `PostToolUse` hook | (a) | Tasks 4.4 and 6.6 |
| Q16 | Where the Phase 1 and Phase 2 PRs merge. `docs validate --merge-ready` fails while this specification is open | None: the owner is being asked | No task, because the merge target is a delivery detail |
| Q17 | Antigravity's post-dispatch payload documents no result, so completion and RED evidence are unobservable there | (a) | Task 16.9 |

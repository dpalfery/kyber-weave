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

Delivered and archived 2026-10-10: specification work complete (approved requirements, design, and three-phase task list). Decisions recorded in the spec; implementation to be delivered in three subsequent phase PRs.

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
| [Tasks](tasks.md) | All three delivery phases, each with its own documentation, verification and review, then one closeout (D32, D34); the Test contract, reference facts, dependency graph and concurrency audit | Complete; development mode test-first |

## Decisions and questions

The owner decided D1–D34 between 2026-10-01 and 2026-10-04. Follow-up design and delivery questions Q15–Q17 are resolved as recorded below and in the [task list](tasks.md#decisions-on-design-gaps):

| Question | What was evaluated | Resolution | Details |
|---|---|---|---|
| Q15 | How Claude dispatches report their return. Background sub-agents and `SubagentHandback` bypass the dispatcher's `PostToolUse` hook | Resolved: Option (a) | Dispatch targets receive frontmatter `PreToolUse` hook on `^SubagentHandback$`, passing through `message` input and evaluating post-dispatch rules (tasks 4.4 and 6.6 written to option a). |
| Q16 | Where the Phase 1 and Phase 2 PRs merge. `docs validate --merge-ready` fails while this specification is open | Resolved | The complete specification is archived per KW-DOC-LIFECYCLE-003 so trunk carries the governed artifact; implementation PRs deliver independently per D32. |
| Q17 | Antigravity's post-dispatch payload documents no result, so completion and RED evidence are unobservable there | Resolved: Option (a) | Post-dispatch returns declared unobservable on Antigravity; rules log skip and allow (task 16.9 written to option a). |

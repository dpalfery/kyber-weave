---
id: specs/2026-11-10-arbiter-decision-model-savings/index
title: Arbiter decision-model savings specification
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-11-10
documentation-impact: extends-component
impact-components: [KyberArbiter, KyberSquad, ReviewCouncil]
---

# Arbiter decision-model savings specification

**Status:** Ready (all three phases approved; execution authorized 2026-11-10)

The Kyber Arbiter's step-1 decision models (TypeSafe's cloud JEV models, or a local Ollama
model) already answer four judgements. This specification extends them so a Kyber-Squad run
spawns fewer agents and sends more of the work it does spawn to a cheaper model. Every new
judgement fails safe: when unsure, the run does the expensive thing it does today.

| Document | Covers | Phase status |
|---|---|---|
| [Requirements](requirements.md) | Requirements 1–22: the foundation, model targeting per task, and eight spawn-removing judgements | Approved |
| [Design](design.md) | Secondary triggers and new families, render-time model-profile resolution, per-harness model capability, run stores, tool surfaces, delivery file lists | Approved |
| [Tasks](tasks.md) | 54 tasks in three phases plus closeout, test-first (RED then GREEN), with the Test contract; dispatch file `tasks.json` | Approved |

## Delivery

Three draft pull requests, in order, each shippable alone (owner pre-approved sequence):

1. **Foundation** — the ADR amendment, the new effects, triggers and facts, the
   `review.changed-hunks` filler, the model-profile resolution layering, hunk-level lens
   skipping, and the threshold-tuning report.
2. **Model targeting** — choosing a model profile per implementation task, shadow mode by default.
3. **Spawn-removing judgements** — nitpick filter, duplicate findings, trimmed worker reads,
   bug-triage shortcut, intake shortcut, docs-impact shortcut, same-failure detection, and
   bot review comment triage.

## Decisions

The permanent decision record is in [requirements](requirements.md#decisions) (D1–D13) and
[design](design.md#decisions) (D14 onward). The design's own harvest table is in
[design](design.md#design-harvest).

## Design harvest

Scale: `documentation-impact: extends-component`, `impact-components: [KyberArbiter, KyberSquad, ReviewCouncil]`.

| ID | Design item | Target component | Target doc type |
|---|---|---|---|
| H1 | New effects, triggers and their combination rules (Requirement 1) | KyberArbiter | adr (amend 0053), architecture |
| H2 | New facts and their fillers, including `review.changed-hunks` and `file.text-at-line` (Requirements 2, 20) | KyberArbiter | architecture |
| H3 | The new permanent `KW-ARB-*` rules and diagnostics | KyberArbiter | architecture, reference (rule reference) |
| H4 | Shadow mode, the shared safety contract, and the threshold-tuning report (Requirements 4, 5, 12) | KyberArbiter | architecture, runbook |
| H5 | Per-harness model-override capability and model-profile resolution at hook time (Requirements 6, 7, 9) | KyberArbiter | architecture (harness facts) |
| H6 | `arbiter:` configuration for model targeting and the new rules | KyberArbiter | architecture, reference (configuration) |
| H7 | Squad role changes: conductor, bug-crusher, code-reviewer, docs-dev, review loops (Requirements 13–20) | KyberSquad | architecture |
| H8 | Review fan-out changes: dropped nitpicks, duplicate findings, hunk-level lens skipping | ReviewCouncil | architecture |

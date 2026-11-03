---
id: archive/specs/ship-gate-catalogue/index
title: Shipped review-gate catalogue specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-10-27
documentation-impact: extends-component
impact-components:
  - ReviewCouncil
  - DocGraph
  - KyberSquad
  - CI Pipelines
  - Distribution
---

# Shipped review-gate catalogue specification

**Status:** Archived
**Archive Date:** 2026-10-27

Delivered and closed out 2026-10-27: implementation ships on the same branch and pull request (#451); every acceptance criterion traces to delivered work (see [tasks](tasks.md)). The durable harvest H1–H10 lives in canonical documentation (see [Closeout mapping](#closeout-mapping)).

Ship per-technology deterministic checks (format, lint, type-check, build, test) with
Kyber-Weave, so a repository that installs it gets them as editable `review:` configuration
for the technologies it declares, run by the review council, by each developer agent's task
gate, and by a copy-ready CI workflow.

Today an installing repository gets no gates at all, C# included: the product defaults are
empty and `docs init` writes no `review:` section.

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | Approved 2026-10-27 (autonomous). 10 requirements in EARS form, non-goals, decisions. |
| [Design](design.md) | Approved 2026-10-27 (autonomous). Embedded per-family catalogue, root detection, `working-directory`, council `include`/`os`, `docs init` review block, `review catalogue`, CI template, vendor-verified argv. |
| [Tasks](tasks.md) | Approved 2026-10-27 (autonomous), test-first; execution approved by the owner 2026-10-27. 16 file-scoped tasks in four parallel waves, a Linux real-toolchain verification job, the final council review, and the docs-dev closeout. |

## Decisions

| ID | Question | Answer / decision | Status |
|---|---|---|---|
| CO-1 | How is this specification closed out? | Archived 2026-10-27 with the implementation on the same branch and pull request (#451); durable content harvested per the Closeout mapping below; phase decisions D1–D20, design decisions and T1–T7 preserved in [requirements](requirements.md#decisions), [design](design.md#decisions) and [tasks](tasks.md#decisions). | Closed |

## Design harvest

The authoritative harvest table (H1–H10: each durable design item, its target component and
doc type) lives in [design.md](design.md#design-harvest), so it has one home. This index
carries the same ids so its own closeout stands alone:

| ID | Design item | Target component and doc type |
|---|---|---|
| H1 | Catalogue format, families, placeholders | ReviewCouncil · architecture |
| H2 | Root detection and id derivation | ReviewCouncil · architecture |
| H3 | `working-directory`, council `include` and `os`, changed set | ReviewCouncil · architecture |
| H4 | New rule ids `KW-CONFIG-002`, `KW-REVIEW-033`, `KW-REVIEW-034` | CI Pipelines · reference |
| H5 | `docs init` review block, `--no-review-gates`, re-run rule | DocGraph · onboarding |
| H6 | `review catalogue` command | ReviewCouncil · reference |
| H7 | Squad task-gate hint | KyberSquad · architecture |
| H8 | Review-gate CI workflow | CI Pipelines · runbook |
| H9 | What an installing repository gets | Distribution · onboarding |
| H10 | Answer to the .NET agent-mapping question | KyberSquad · todo |

## Closeout mapping

Harvest H1–H10 migrated into canonical documentation at `extends-component` scale (ReviewCouncil, DocGraph, KyberSquad, CI Pipelines, Distribution); implementation is the truth where it diverged from the design. No ADR: the catalogue refines the existing review architecture and established `docs init` / `review gates` patterns, so no new architectural decision constrains future work — no ADR, ADR waiver: refines existing architecture.

| ID | Canonical destination |
|---|---|
| CO-1 | Documentation waiver — closeout record only; the durable ids it points to are mapped individually below. |
| H1, H2 | [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| H3 | [Review council architecture](../../../code-review/architecture.md#council-gates), [DocGraph onboarding](../../../docgraph/onboarding.md) |
| H4 | [Rule reference](../../../ci-pipelines/rule-reference.md), [Workflows runbook](../../../ci-pipelines/workflows-runbook.md) |
| H5 | [DocGraph onboarding](../../../docgraph/onboarding.md), [Install](../../../install.md) |
| H6 | [CLI command surface](../../../cli-command-surface.md), [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| H7 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md), [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| H8 | [Workflows runbook](../../../ci-pipelines/workflows-runbook.md#the-review-gate-template) |
| H9 | [Install](../../../install.md), [DocGraph onboarding](../../../docgraph/onboarding.md) |
| H10 | [Task-gate config review](../../../todo/task-gate-config-review.md), [Windows Node gate probe](../../../todo/windows-node-gate-probe.md) |

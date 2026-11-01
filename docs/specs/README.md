---
id: specs/index
title: Specifications
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-10-31
---

# Specifications

This directory contains upfront, specification-driven development artifacts (following the Kiro spec mode and GitHub Spec Kit lineage).

## When to use a spec

A specification is used at the start of a greenfield project or a large, complex feature when architecture, interfaces, and requirements still need defining before planning and implementation begin.

- **Todo**: Captures deferred work, findings, or declined suggestions as seeds for future work.
- **Spec**: Defines requirements, architecture, and design upfront for greenfield or large-scale initiatives.
- **Plan**: Sequences concrete implementation tasks and verification steps once the architecture is known.

> [!NOTE]
> This directory represents Kyber-Weave's own internal documentation governance convention. It is distinct from the Kyber-Squad product's downstream `6-Docs/specs/` three-file workflow (`requirements.md`, `design.md`, `tasks.md`) used in consumer repositories.

A specification is open only while it is built. The pull request that finishes it archives it: CI runs `docs validate --merge-ready`, which fails on any specification still in this folder (`KW-DOC-LIFECYCLE-003`).

## Specification Decisions, Design Harvest and Closeout Policy

Every specification maintains a permanent `## Decisions` section (ID, Question, Answer / decision, Mitigating and supporting information, Status / approval provenance), preserved intact across all lifecycle transitions. Every specification declares its scale at authoring time via `documentation-impact` (`new-component` | `extends-component` | `no-design-change`) with `impact-components`, plus a `Design harvest` table mapping each durable design item to its target component and doc type. During closeout, durable content is harvested into canonical documentation and/or ADRs before archival.

## Specification inventory

| Spec | Component | Status | Date | Archive Date | Canonical Docs / Reference | Description |
| [MCP configuration wiring](mcp-configuration-wiring/requirements.md) | DocGraph | Ready | 2026-10-31 | — | [DocGraph MCP runbook](../docgraph/mcp-runbook.md) | `docs init` detects, per harness in play, whether the MCP servers the repository declares are wired and launchable, warns without failing the command, and offers a confirmed repair; a separate, bounded, receipted command performs the repository-scope remedy. Squad is untouched. |
|---|---|---|---|---|---|---|
| [Shipped review-gate catalogue](../archive/specs/ship-gate-catalogue/README.md) | ReviewCouncil | Archived | 2026-10-27 | 2026-10-27 | [Review council architecture](../code-review/architecture.md), [CLI command surface](../cli-command-surface.md), [DocGraph onboarding](../docgraph/onboarding.md), [Install](../install.md), [Rule reference](../ci-pipelines/rule-reference.md), [Workflows runbook](../ci-pipelines/workflows-runbook.md), [Kyber-Squad architecture](../kyber-squad/architecture.md) | Per-technology council and task gates shipped with Kyber-Weave and written into an installing repository's `review:` configuration by `docs init`, with `include` on council gates and a copy-ready review-gate CI workflow. |
| [Squad model presets](../archive/specs/squad-model-presets/README.md) | KyberSquad | Archived | 2026-10-24 | 2026-10-25 | [Kyber-Squad](../kyber-squad/onboarding.md), [Kyber-Squad architecture](../kyber-squad/architecture.md), [ADR 0044](../adr/0044-squad-lock-v2-and-instance-deployment-scope.md) | Named, user-owned model presets applied at render time by `squad install`/`update`, and different presets in two projects at once without deploying into either. |
| [KyberDash context surfaces](../archive/specs/kyberdash-context-surfaces/README.md) | KyberDash | Archived | 2026-09-18 | 2026-09-24 | [KyberDash](../dash/README.md), [ADR 0020](../adr/0020-kyberdash-one-time-fork.md), [ADR 0023](../adr/0023-kyberdash-report-model-and-tray-ownership.md) | One-time fork of codeburn, removal of non-context features, a Tauri tray for macOS and Windows, deep-linkable dashboard views, and a context-troubleshooting CLI report. |

---
id: specs/index
title: Specifications
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-09-18
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

## Specification inventory

| Spec | Component | Status | Date | Archive Date | Canonical Docs / Reference | Description |
|---|---|---|---|---|---|---|
| [KyberDash context surfaces](kyberdash-context-surfaces/README.md) | KyberDash | Draft | 2026-09-18 | — | [KyberDash](../dash/README.md) | One-time fork of codeburn, removal of non-context features, a Tauri tray for macOS and Windows, deep-linkable dashboard views, and a context-troubleshooting CLI report. |

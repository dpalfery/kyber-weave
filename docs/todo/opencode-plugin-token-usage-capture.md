---
id: todo/opencode-plugin-token-usage-capture
title: Per-source "last emitted" status for capture, so a stopped exporter is visible
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-31
---

# Per-source "last emitted" status for capture, so a stopped exporter is visible

Narrowed from the [KyberDash OTel harness coverage plan](../archive/plans/2026-10-27-kyberdash-otel-harness-coverage.md)
The usage and content items that were first recorded here are delivered: the OpenCode
plugin now sends per-step usage and consent-gated content through the V2 pipeline
([ADR 0048](../adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline.md),
[architecture](../dash/architecture.md#opencode-v2-capture-plugin-pipeline-kyberdash-opencode)).
Only the status signal remains open.

## Problem

A silently stopped exporter looks the same as one that was never configured. The
[per-harness OTel collection matrix](../dash/telemetry-inventory.md#per-harness-otel-collection-matrix)
shows pi, copilot chat and antigravity as stopped by their last emitted date, but the status
surfaces cannot tell that apart from a harness that was never set up.

## Follow-up — per-source last-emitted status

Add a per-source "last emitted" signal so a silently stopped exporter is visible, instead of
looking the same as one never configured. Candidate surfaces are the read-only capture/status
route, doctor, or `kyberdash kyber capture`; the signal must distinguish never-emitted,
recently-emitted, and stopped-after-emitting, and must never render absence as a measured zero.

Decide where the signal is recorded (an ingest-log per source, a store column, or an existing
checkpoint) before changing a surface, and keep the display-layer rule: the engine computes it,
the tray and web render it over `/api/kyber/*`.

## Acceptance criteria

- The capture/status surface reports per source when telemetry last arrived, distinguishes
  never-configured from stopped-after-emitting, and renders no fabricated timestamp for a source
  that never emitted.
- `docs/dash/architecture.md` and `docs/dash/telemetry-inventory.md` describe the verified status
  contract, and this todo is archived when the signal lands.

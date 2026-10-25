---
id: archive/specs/squad-model-presets/index
title: Squad model presets specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-10-25
---

# Squad model presets specification

**Status:** Archived
**Archive Date:** 2026-10-25

Delivered and closed out 2026-10-25: every acceptance criterion traces to a completed task (see [tasks](tasks.md)). The durable decisions live in [ADR 0043](../../../adr/0043-squad-lock-v2-and-instance-deployment-scope.md), [`docs/kyber-squad/onboarding.md`](../../../kyber-squad/onboarding.md), and [`docs/kyber-squad/architecture.md`](../../../kyber-squad/architecture.md).

---

User-owned, named model presets that Squad applies when it renders agents, so switching
provider or inference plan is one command and survives updates.

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 7 requirements in EARS form (approved; Requirement 7 revised for per-preset instances on macOS and Windows) |
| [Design](design.md) | Approved. Slice 1: the preset is applied as an overlay during rendering, inside the update transaction, and recorded in lock v2. Slice 2: one stable instance folder per harness and preset, linked back to the harness's main home; `squad run` launches the harness on it, and a per-harness instance catalog records how each harness is pointed there. |
| [Tasks](tasks.md) | Approved, test-first. Eight spikes on macOS and Windows, 8 slice-1 tasks, 10 slice-2 tasks, then gates, the final code review and the docs closeout. |

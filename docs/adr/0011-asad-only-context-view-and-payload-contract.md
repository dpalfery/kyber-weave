---
id: adr/0011-asad-only-context-view-and-payload-contract
title: ASAD as the Only Context View and as the Canonical Session Contract
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-04
---

# ADR 0011: ASAD as the Only Context View and as the Canonical Session Contract

## Status

Accepted, 2026-09-04. Amends [ADR 0007](0007-kyberdash-agent-session-analysis-integration.md) decisions D1 and D2: the Context tab no longer offers a second TreeTable / SessionDetails path. [ADR 0008](0008-kyberdash-single-canonical-store.md) (single store) and [ADR 0009](0009-multi-signal-ingestion-span-shaped-record.md) (logs enrichment, quarantine, source precedence) stand.

## Context

ADR 0007 embedded the Agent Session Analysis Dashboard (ASAD) in Context Explorer while still describing a dual rendering path: agent providers used `AgentSessionDashboard`, and Claude / Codex / Antigravity still used `SessionDetails` and `TreeTable`. ADR 0008 then made `canon.db` the only store and removed the hard-coded `sessions.db` path the ASAD view had been served from. Restoring the view on the canonical store required a contract: the session projection had to emit the shape the dashboard already reads, not a translation of an intermediate payload.

Harness coverage is uneven. Some sources supply counters without schemas, some supply files without OTel, and some are not collectable at all. Treating absence as zero would make the least-instrumented harness look cheapest.

## Decision

1. **ASAD is the only Context view.** Every harness uses one session-row and one expanded `AgentSessionDashboard`. TreeTable, SessionDetails, and any toggle between them are not part of the Context page.
2. **The dashboard ships complete.** Session overview, per-turn token spend, context composition, tool/schema cost, execution timeline, cost/token accounting, caveat banners, and the full-content inspector drawer. Layout, view order, and captions come from ASAD; visual language is KyberDash's.
3. **The ASAD payload is the store's contract.** `buildSessionRow` / `CanonStore.getSessionPayload` emit that JSON-safe shape directly. `GET /api/kyber/session/:id` serves it without an adapter layer between store and view.
4. **Every named harness is in scope.** Claude Code, Antigravity/agy, Copilot (extension, CLI, coding agent), Codex, pi, opencode, Cursor, and Kilo. A harness that cannot yet be collected is recorded as *not measurable with a reason*, never as absent or as a measured zero.

## Alternatives Considered

- **Keeping TreeTable as a fallback for harnesses whose payload is incomplete.** Rejected. Two views hide incomplete collection behind a second UI and recreate the dual-path defect ADR 0007 left in place.
- **Translating an intermediate session payload in the bridge or routes.** Rejected. Every consumer would reimplement the mapping, and the store would no longer be the contract.
- **Omitting harnesses with no live data from the provider list.** Rejected. Silence is indistinguishable from "we measured zero."

## Consequences

- Incomplete collection surfaces as `not_measurable` reasons and caveat banners, not as a second explorer.
- Live per-server schema bands and full-content drawer checks still depend on owner-controlled capture (Claude raw-body logs, Cursor hook registration). Those are runtime gates, not a second rendering path.
- Changing the Context page to add another session view, or inserting a payload adapter between `canon.db` and the dashboard, requires a new ADR.

## Related

- [ADR 0007: KyberDash Agent Session Analysis Integration](0007-kyberdash-agent-session-analysis-integration.md)
- [ADR 0008: Single Canonical Store](0008-kyberdash-single-canonical-store.md)
- [ADR 0009: Multi-Signal Ingestion](0009-multi-signal-ingestion-span-shaped-record.md)
- [KyberDash architecture](../dash/architecture.md)
- [Telemetry inventory](../dash/telemetry-inventory.md)

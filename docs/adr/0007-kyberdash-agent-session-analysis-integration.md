---
id: adr/0007-kyberdash-agent-session-analysis-integration
title: KyberDash Agent Session Analysis Integration, Dual-Database Architecture, and Navigation Topology
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-03
---

# ADR 0007: KyberDash Agent Session Analysis Integration, Dual-Database Architecture, and Navigation Topology

## Status

Accepted

## Context

KyberDash was established in [ADR 0006](0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) as a TypeScript soft fork of `getagentseal/codeburn` (vendored under `dash/`) hosting OpenTelemetry span analysis from the retired Python pipeline (`agent-session-analysis-dashboard`). While `dash/kyber/canon/store.ts` provided raw span persistence into `~/.kyberdash/canon.db`, the Web Dashboard and Context Explorer suffered from four key defects:

1. **Disconnected Navigation Routes and Blank Screens**: Top navigation originally included standalone buttons (`[Buckets]`, `[Schema]`, `[Timeline]`, `[Compare]`, `[Quarantine]`, `[Problems]`). In the Python pipeline, Context Composition, Tool/Schema Cost, Turn Spend, and Timeline are facets of a specific session, not global views. Navigating to them globally caused blank screens or permanent loading skeletons because the backend routes were missing.
2. **Missing `/api/kyber/*` REST Endpoints**: The web dashboard server (`dash/src/web-dashboard.ts`) had no handlers for `/api/kyber/*` routes, falling through to the SPA catch-all and returning HTML to JSON API callers.
3. **Disconnected Historical Corpus**: The Python pipeline accumulated 187 pre-analyzed historical sessions across Copilot, Gemini, and Pi (~37,000 spans) stored in `sessions.db`. KyberDash had no query layer capable of serving both live OTel sessions from `canon.db` and historical records from `sessions.db`.
4. **Context Explorer Lacked Deep Telemetry**: Selecting an agent session in `ContextExplorer.tsx` presented only a basic text tree (`TreeTable`) and four high-level token chips, omitting rich per-turn spend breakdowns, context composition heatmaps, and tool call payload inspectors.

## Decision

Four cohesive architectural decisions (D1–D4) govern the integrated KyberDash dashboard:

1. **D1: Unified Agent Session Analysis Dashboard in Context**:
   Session-specific deep analysis is embedded inside `ContextExplorer.tsx`. When an agent session (Copilot, Gemini, Pi) is selected, KyberDash renders `AgentSessionDashboard.tsx`:
   - **Overview Strip**: Metric summary chips (spans, turns, tokens in/out, cache hit ratio, cost USD and credits with basis), exact reconciliation badge (`exact_match`), subagent parent link, and harness notes.
   - **Per-Turn Spend Chart**: Stacked token progression (fresh input, cache read, cache creation, output).
   - **Context Composition Heatmap & Chart**: Token distribution by semantic part type (`system_prompt`, `instruction_context`, `tool_definitions`, `conversation_history`, `tool_result_content`, `residual`).
   - **Tool & Schema Cost Table**: Ranked tool schemas, resident size, invocation counts, and unused schema waste range (floor to ceiling).
   - **Execution Timeline / Call Tree**: Hierarchical span execution tree with status badges, durations, and auxiliary/subagent flags.
   - **Slide-out Inspector Drawer (`SessionInspectorDrawer`)**: Formats harness XML tags (`<instructions>`, `<environment_info>`, `<copilot_instructions>`, `<context>`) into collapsible `<details>` blocks with preview summaries, and formats tool call parameters and results into readable structured trees.

2. **D2: Top-Level Navigation Refactoring (5 Tabs)**:
   Top navigation in `App.tsx` is streamlined to five distinct, functional views:
   - `[Usage]`: CodeBurn device overview, top projects, and daily spend trends.
   - `[Context]`: Unified session explorer hosting the rich Agent Session Dashboard and legacy transcript TreeTable.
   - `[Compare]`: Cross-harness comparison matrix across all active agents.
   - `[Quarantine]`: Unclaimed spans and unrecognized namespaces held for triage.
   - `[Problems]`: Validation errors, token reconciliation discrepancies, and anomalies.

3. **D3: Formal REST API Contract in `dash/src/web-dashboard.ts`**:
   The embedded web server defines standard JSON endpoints wired to the storage bridge:
   - `GET /api/kyber/sessions` (supports `?limit=` and `?harness=`)
   - `GET /api/kyber/session/:id` & `GET /api/kyber/session?id=`
   - `GET /api/kyber/compare`
   - `GET /api/kyber/quarantine` (supports `?limit=`)
   - `GET /api/kyber/problems` (supports `?limit=`)
   - `GET /api/kyber/meta`
   - Backward-compatible endpoints: `/api/kyber/context`, `/schema`, `/timeline` (defaulting to the first active session when ID is omitted).
   - Precedence guard: Any unhandled `/api/kyber/*` request returns HTTP 404 JSON (never falling through to SPA HTML). Non-GET requests return HTTP 405 Method Not Allowed with `cache-control: no-store`.

4. **D4: Dual-Database SQLite Bridge (`KyberBridge`)**:
   A unified query service (`dash/kyber/server/bridge.ts`) connects to SQLite via Node's built-in `node:sqlite` (`DatabaseSync`):
   - Primary: `~/.kyberdash/canon.db` (live OTel records).
   - Fallback: `agent-session-analysis-dashboard/sessions.db` (configurable via `AGENTDASH_DB` or `KYBER_DB` environment variables).
   - Sessions, comparison matrices, quarantine entries, and problem records are unioned and deduplicated across both databases, allowing transparent offline operation on historical corpora.

## Alternatives Considered

- **Retaining Standalone Global Tabs for Schema and Timeline**: Rejected. Schema cost, turn spend, and execution timelines are inherently scoped to an individual agent session. Attempting to display them without an active session caused blank screens and architectural fragmentation.
- **Requiring a Full Database Migration**: Rejected. Rather than running a one-time migration of the 2.9 GB derived SQLite store into `canon.db`, `KyberBridge` provides seamless zero-copy dual-database querying directly over both schemas.
- **Express / Fastify Web Frameworks**: Rejected. Using the lightweight Node standard library `http.createServer` already in upstream codeburn preserves the merge zone boundaries and avoids extraneous runtime dependencies.

## Consequences

- The web dashboard presents zero blank tabs and zero broken API requests.
- Selecting agent sessions in `Context` exposes complete turn-by-turn context composition, spend charts, and XML tag-folded inspector details.
- Developers can analyze live OTel traces streamed to port 4318 alongside 187 pre-analyzed benchmark sessions from `sessions.db`.
- All endpoints are protected by hermetic contract tests in `dash/tests/kyber-api.test.ts` ensuring regression-free API delivery.

## Related

- [ADR 0006: KyberDash as a TypeScript Soft Fork with a Merge Zone and an Embedded OTLP Receiver](0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md)
- [KyberDash architecture](../dash/architecture.md)
- [KyberDash runbook](../dash/runbook.md)
- [KyberDash product index](../dash/README.md)

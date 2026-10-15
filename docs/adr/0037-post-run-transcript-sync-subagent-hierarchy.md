---
id: adr/0037-post-run-transcript-sync-subagent-hierarchy
title: "Post-Run Transcript Synchronization and Stable-Identity Subagent Session Hierarchy"
doc-type: adr
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-14
---

# ADR 0037: Post-Run Transcript Synchronization and Stable-Identity Subagent Session Hierarchy

## Status

Accepted, 2026-10-14. Establishes the subagent session hierarchy model, stable identity format, read-time spend rollups, honest `<agent name> · <task summary>` title synthesis, Schema v20, and the engine-hosted post-run transcript synchronization job.

## Context

Agentic coding tools (such as Claude Code and GitHub Copilot) routinely dispatch subagents to execute focused sub-tasks—code exploration, testing, schema drafting, and code review. In previous iterations of KyberDash, these delegated operations posed four interrelated architectural problems:

1. **Fragmented and unstable session identity:** Subagent turns were either flattened into the root session or projected with transient, non-deterministic identifiers, making it impossible to navigate parent-child relationships or bookmark a subagent session.
2. **Double-counting vs. visibility dilemma:** If subagent sessions were projected as independent rows, summing top-level sessions double-counted token usage and dollar spend across the parent-child boundary. If subagents were suppressed from listing, their individual prompt histories, tool invocations, and cost breakdowns became invisible.
3. **Missing conversational transcript context in live telemetry:** Live OpenTelemetry (OTel) streams export operational span metrics (tokens, models, durations, operations) but frequently omit complete prompt and message bodies. Meanwhile, local session transcripts (e.g. Claude Code JSONL files) record rich multi-turn conversation text keyed by request ID, but arrive asynchronously on disk.
4. **Machine label pollution in session titles:** In the absence of a structured title synthesis service, session titles routinely leaked raw telemetry artifacts—span operations (e.g. `.llm_request`, `.generate`), chat model strings, raw UUIDs, or synthetic placeholders (`synth:`, `derived:`), violating the principle of honest unobservability.

## Problem

The system required a unified architecture that:
- Models subagent sessions as first-class entities with deterministic identities and explicit parent-child linkage.
- Solves spend accounting so that total task spend is visible on parent sessions without double-counting top-level aggregates or obscuring granular per-agent metrics.
- Enriches OTel spans with full conversation histories from on-disk JSONL transcripts asynchronously without blocking ingest or contending with active store refreshes.
- Synthesizes human-readable session titles without ever fabricating synthetic labels or leaking machine identifiers.
- Complies strictly with the display-layer boundary established in [ADR 0033](0033-kyberdash-surfaces-are-display-layers.md).

## Decisions

### D1: Stable-Identity Subagent Session Hierarchy

Subagents are projected during session build (`dash/src/canon/sessions.ts`) into distinct rows in the `session` table with deterministic, stable session identifiers:
- **Claude Code subagents:** Use the deterministic format `agent-<agentId>` (e.g. `agent-researcher-001`), normalizing any existing `agent-` prefix.
- **Copilot subagents:** Use `<parentSessionId>#<invoke_agent_span_id>` (e.g. `copilot-sess-001#invoke-agent-span-1`).
- Each subagent session explicitly records `is_subagent = 1`, `parent_session = <parentSessionId>`, and `agent_name = <agentName>` in the `session` table.
- Subagent turn records are rooted with `parentSpanId = null` at their earliest invoke span, with child spans reparented beneath it, ensuring valid timeline graphs in ASAD session payloads.

### D2: Dual Spend Accounting via Read-Time Rollup

To provide complete spend visibility while strictly preventing double counting:
- Derived session rows persist only their own granular, unaggregated counters (`turn_count`, `request_count`, `total_input`, `total_output`, `cost_usd`) in their respective `payload.summary` blocks.
- On read (`KyberBridge.listSessions` in `dash/src/server/bridge.ts`), child subagent sessions whose parent exists in the store are omitted from the top-level list and nested into their parent session's `subagents` array.
- Parent sessions carry a read-time `rollup` object that aggregates spend across the parent and all nested children:
  $$\text{rollup.metric} = \text{parent.metric} + \sum_{c \in \text{subagents}} c\text{.metric}$$
- Both parent and child sessions preserve their own granular, unaggregated counters at their respective levels.
- Orphan subagents (whose parent session is absent from the store) remain visible at the top level with a self-derived `rollup` object matching their own metrics.

### D3: Honest Session Title Synthesis (`<agent name> · <task summary>`)

The naming service (`dash/src/canon/naming.ts`, `formatSessionTitle`) synthesizes display titles using a deterministic precedence ladder:
1. `<agent name> · <task summary>` when both are available.
2. `<agent name> · <human label>` when task summary is absent but label is human.
3. `<agent name> · session` when task summary is absent or machine-generated.
4. `<task summary>` when agent name is absent.
5. `<human label>` when agent name and task summary are absent.
6. `<harness> session` or formatted shortened session ID as fallbacks.

The naming service enforces machine-label suppression (`isMachineLabel`): span operations (matching `\.(?:llm_request|llm_invoke|generate|completion)$`), chat model strings (`chat <model>`), exact UUIDs, synthetic prefixes (`synth:`, `derived:`), and `<synthetic/unknown>` tokens are rejected. Absent telemetry is never fabricated into synthetic summaries or fake agent identities ([honest unobservability](../rules/honest-unobservability.md)).

### D4: Schema Migration v20

Schema version 20 (`dash/src/canon/store.ts`) extends the `session` table with five sync progress and settlement columns:
- `synced_at TEXT`: UTC ISO timestamp of the last successful transcript sync pass.
- `sync_status TEXT DEFAULT 'pending'`: synchronization lifecycle state (`pending`, `synced`, `settled`, `unavailable`).
- `settled_at TEXT`: UTC ISO timestamp when the session reached final settlement.
- `transcript_path TEXT`: local filesystem path to the source transcript file.
- `transcript_offset INTEGER DEFAULT 0`: byte offset reached during incremental delta parsing.

All `ALTER TABLE` statements in migration 19 (`v19 -> v20`) are presence-guarded, ensuring safe idempotent execution across fresh databases and pre-existing stores.

### D5: Engine-Hosted Incremental Transcript Synchronization

Post-run transcript synchronization is implemented in `dash/src/sync/transcript-sync.ts` (`runTranscriptSync`) and executed on every scheduler tick in `JobHost` (`dash/src/jobs/host.ts`):
- The job queries `session` where `sync_status NOT IN ('settled', 'unavailable') OR sync_status IS NULL`.
- If `fileSize > oldOffset`, it reads incremental delta bytes from `oldOffset` to `fileSize`.
- Delta text is parsed line-by-line as JSONL. Matching lines with `requestId` and text enrich canonical records in `records` table joining on `request_id`, inserting conversation history parts into `parts_json` and updating `content_json`.
- The session row is updated with `transcript_offset = fileSize`, `synced_at = nowIso`, and `sync_status = 'synced'`.
- If a file is truncated (`fileSize < oldOffset`), the job rewinds to offset 0, re-reads the full file, enriches records, and updates offset.

### D6: Inactivity-Based Settlement Lifecycle (30-Minute Threshold)

When a transcript file size equals `oldOffset` (no new bytes observed):
- If `sync_status === 'pending'`, it advances to `'synced'`.
- If `sync_status === 'synced'`, the engine checks session inactivity: if at least 30 minutes have elapsed since the session's latest activity (`ended` or `started` timestamp) across two unchanged passes (`(now - lastActivity) >= 30 * 60_000`), the session transitions to `sync_status = 'settled'` with `settled_at = nowIso`.
- Settled sessions are excluded from future sync queries, keeping background passes $O(\text{active sessions})$.

### D7: Missing Transcript Timeout (24 Hours)

If a session's `transcript_path` is null or does not exist on disk, and more than 24 hours have elapsed since the session ended or started (`(now - refTime) > 24 * 3600_000`), the session transitions to `sync_status = 'unavailable'`. This prevents perpetual polling of missing or deleted files.

### D8: Refresh Lock Arbitration

Before reading or updating transcripts, `runTranscriptSync` probes the store refresh lock (`STORE_LOCK_FILE`, `dash/src/refresh/lock.ts`). If another process holds the refresh lock (e.g. an ongoing terminal or scheduled `dash refresh`), the transcript sync job yields immediately and returns `{ outcome: 'busy' }`, avoiding SQLite lock contention and database busy timeouts.

### D9: Display-Layer Boundary Compliance (ADR 0033)

In accordance with [ADR 0033](0033-kyberdash-surfaces-are-display-layers.md):
- Neither the Tauri tray nor the web dashboard contains scheduler, transcript parsing, or database mutation logic.
- The transcript sync job runs exclusively inside `JobHost` within the CLI engine server (`kyberdash web`).
- Display layers consume session hierarchies and sync metadata purely through `/api/kyber/*` endpoints.

## Alternatives Considered

1. **Eager token and cost rollup at projection time into primary counters:** Rejected. Pre-aggregating child spend into parent database columns causes double-counting whenever session rows are summed in queries, and permanently destroys the parent's individual operational metrics.
2. **Synthetic prompt summarization via LLM calls:** Rejected. Violates honest unobservability by introducing subjective, unverified claims and adds external latency and cost to deterministic background jobs.
3. **Frontend-driven transcript parsing:** Rejected. Violates ADR 0033 by placing data mutation and filesystem operations in the web UI or tray shell.
4. **Full-file re-reading on every tick:** Rejected. Reading entire JSONL files on every scheduling tick produces unacceptable I/O and memory overhead as conversation histories grow into megabytes. Byte-offset delta streaming keeps each tick lightweight.

## Consequences

### Positive
- Subagent executions are first-class, navigable entities in the Sessions explorer with preserved parent-child trees.
- Top-level session listings reflect true total task spend via read-time rollups without double-counting.
- Live OTel spans are enriched with full conversation histories from disk without blocking real-time trace ingestion.
- Session titles are clean and readable, eliminating raw UUIDs and machine span names without fabricating data.
- The 30-minute settlement lifecycle and 24-hour timeout bound background job overhead.

### Costs and Constraints
- Schema migration v20 must be applied to `canon.db` instances.
- Background sync passes consume periodic CPU and I/O during active agent sessions.
- Transcript enrichment depends on local JSONL files remaining readable on disk during the active session window.
- Rebuilding derived sessions (`kyber build`) re-evaluates subagent groupings from canonical records.

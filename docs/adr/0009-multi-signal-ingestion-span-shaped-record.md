---
id: adr/0009-multi-signal-ingestion-span-shaped-record
title: Multi-Signal Ingestion into a Span-Shaped Canonical Record
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-04
---

# ADR 0009: Multi-Signal Ingestion into a Span-Shaped Canonical Record

## Status

Accepted, 2026-09-04. Amends the wording of [ADR 0006](0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) decision D3 ("there is one canonical model, and it is the span model"), which [ADR 0008](0008-kyberdash-single-canonical-store.md) restored. The singularity of the model is upheld; its exclusive derivation from the trace signal is not. Everything else in ADR 0006 and ADR 0008 stands.

## Context

The receiver serves exactly one route — `OTLP_TRACES_PATH = '/v1/traces'` in `dash/kyber/otel/receiver.ts:40` — in both OTLP encodings. That was correct while every emitter we knew about sent spans.

A survey of the seven harnesses in scope, recorded in [the telemetry inventory](../dash/telemetry-inventory.md), found that assumption no longer holds, and that the richest content is unreachable because of it:

- **Claude Code** puts full API request bodies — the system prompt *and* the tool definitions with their schemas — in `claude_code.api_request_body`, an event on the **logs** signal. Its spans carry counters only.
- **Codex** gates raw prompts behind `otel.log_user_prompt`, also on logs.
- **Cursor** emits **no traces at all**; its official export is metrics and logs only, and its documentation states plainly that there is no prompt content and no historical backfill. What we can use from Cursor comes from agent hooks, not its exporter.

Per-server tool schemas are what ASAD's tool-cost view ranks on (`schema_tokens × turns_resident`). Nothing currently reaching the collector carries them: Antigravity emits tool names only, and Copilot's exporter — already enabled and pointed at `127.0.0.1:4318` on the developer's machine — has `captureContent` switched off. The one signal that would supply them is the one we do not serve.

Separately, two ingestion defects distort every figure derived from the store. 29,627 of 54,592 records are `GlobalHttpApi.health` and `http.server GET` spans — infrastructure noise that is 60 percent of the corpus and the origin of sessions that never happened. They are stamped `op = llm.invoke` because `otel/service.ts` hard-codes that op for every received span rather than deriving it.

Finally, some harnesses are legitimately two sources at once. Claude Code emits enhanced-telemetry spans *and* writes `~/.claude/projects/**/*.jsonl`, joinable on session id. pi does the same. Without a stated precedence rule, one turn described twice is counted twice.

## Decision

1. **The canonical model is span-shaped, and is populated from any OTLP signal.** One record per model call, whatever combination of signals described it. ADR 0006 D3's claim of a single canonical model is affirmed; its restriction of that model to the trace signal is lifted.
2. **The receiver serves `/v1/logs`.** A log record **enriches the record it belongs to and never inserts a parallel one**, correlated on `(trace_id, span_id)`, falling back to `session_id` plus a timestamp window. Logs are an enrichment path onto the span-shaped record, not a second class of record.
3. **A log record that correlates to nothing is quarantined and counted, never silently discarded.** A broken correlation must surface as a number on the collector's health surface, in the same way a failed adapter vote does. Content that vanishes without a trace is indistinguishable from content that was never sent, and requirement R10.1 forbids exactly that confusion.
4. **Source precedence, where one turn has both an OTel row and a dot-folder row: counters come from the OTel row; content comes from the file row when the OTel row carries no parts. Values are never summed across sources for the same turn.** The OTel path reports what the provider billed; the file path holds what was actually sent.
5. **Non-model spans are quarantined at ingest rather than stored as records.** A span that describes no model call is not a canonical record, and `op` is derived from the span rather than assumed.

## Alternatives Considered

- **A separate table for log-derived records.** Rejected. The projection would then read two shapes and reconcile them per session, which is the dual-store problem ADR 0008 was written to end, re-created one layer down.
- **Declining the logs signal and accepting the loss.** Rejected. It forfeits every tool schema we can obtain, which is the input the tool-cost view ranks on, and forfeits Cursor entirely.
- **Reconstructing schemas by splitting `mcp__server__tool` names.** Rejected, consistent with R8.3: delimiters occur inside genuine server names, so the split is unsound. Absent a ground-truth `server` field the tokens belong to built-in definitions.
- **Summing OTel and file values for a shared turn.** Rejected. The two describe the same call, so addition doubles it.
- **Filtering the health spans in the projection rather than at ingest.** Rejected. Every consumer would have to remember the filter, and the storage cost is paid regardless.

## Consequences

- The receiver gains a route and a correlation step; the store gains no new table and the projection gains no new shape.
- Records become mutable after insert. A record may be written by a span and later enriched by a log, so ingestion must be idempotent and ordering-tolerant — logs can arrive before the span they belong to.
- Quarantine acquires a second population (uncorrelated log records) alongside failed adapter votes, and both need surfacing.
- Dropping non-model spans at ingest removes roughly 60 percent of the current corpus and the phantom sessions it manufactures. This is data loss by design and is not reversible for spans already discarded; the existing corpus is repaired by re-running ingest over retained raw payloads.
- Content volume rises materially once request bodies are captured. Claude Code caps content at 61,440 UTF-16 units by default. R12.4's storage budget must be re-measured after the first capture, not assumed.
- Capturing request bodies means capturing prompts. R12.3 already forbids captured content in tracked files; enabling this on a developer machine is the developer's decision to make, not the collector's to make for them.

## Related

- [ADR 0006: KyberDash as a TypeScript Soft Fork with a Merge Zone and an Embedded OTLP Receiver](0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md)
- [ADR 0008: Single Canonical Store; Supersede ADR 0007 D4](0008-kyberdash-single-canonical-store.md)
- [Telemetry inventory](../dash/telemetry-inventory.md)
- [KyberDash architecture](../dash/architecture.md)

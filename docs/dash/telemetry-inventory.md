---
id: dash/telemetry-inventory
title: Telemetry inventory — harness signal and content availability
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-09-04
---

# Telemetry inventory — verified harness signal and content availability

This inventory records implemented collection behavior and the remaining runtime gates as
of 2026-09-04. It does not treat an absent source as a zero: unavailable dimensions are
serialized as `not_measurable` with a source-specific reason.

## Collector and canonical-record contract

**[VERIFIED]** The local receiver accepts JSON and protobuf at both `POST /v1/traces` and
`POST /v1/logs`. Non-model spans are quarantined at ingest and cannot create derived
sessions. A log correlates by `(trace_id, span_id)`, then by session and bounded timestamp
window; it enriches one existing span-shaped record, is idempotent, and is quarantined if
it remains unmatched. These are the durable decisions in [ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md).

**[VERIFIED]** The `canon.db` session projection emits the ASAD payload directly and is
rebuilt from canonical records. For a turn present in both OTLP and a dot-folder source,
counters come from OTLP; file-derived parts are used only when the OTLP record has no parts.
Values are never summed across the two sources. `KyberBridge` reads `canon.db` only;
`AGENTDASH_DB` / `KYBER_DB` cannot expose a legacy Python `sessions.db`.

## Verified harness outcomes

| Harness/source | Verified collection outcome | Availability or gate |
|---|---|---|
| Gemini statusline / Antigravity | Gemini attribution recognizes `gen_ai.system = "gemini"`; non-model trace noise is quarantined. | Tool names are available; per-server schemas remain source-dependent. |
| Copilot Chat | Content-enabled OTLP capture maps observed system instructions, messages, rules, skills, tool definitions, tool results, and session identity into canonical buckets. A live canonical session (`08551cf5-b064-4095-9552-8a9a0a0f78d2`) renders the ASAD dashboard. | The observed per-server schema result was 0 of 81; this is an availability outcome, not a zero-valued schema measurement. |
| Copilot CLI | SQLite ingest preserves its reported ASAD taxonomy, including `context_*_tokens` and `context_tier`. | Omitted reported buckets remain unavailable rather than zero. |
| Claude Code | Enhanced-telemetry counters and dot-folder conversation/tool-result content can enter canonical records. | System prompts and tool schemas from raw API-body logs require the owner to enable `OTEL_LOG_RAW_API_BODIES=1`; that has not been assumed or configured here. |
| Codex | Dot-folder ingestion supplies the system prompt, instructions, conversation, tool results, and context window contained in rollout data. | Availability is limited to fields the source actually supplies. |
| pi | Reader support is implemented and respects OTLP/file source precedence. | No current live collection claim is made. |
| Cursor | `codeburn kyber cursor-hook` emits deterministic OTLP traces from hook JSONL with stable turn identity, supplied counters, and ordered tools. | Synthetic and CLI-post verification passed. Live collection requires the owner to register the command and execute a Cursor turn; existing hooks must not be changed by this work. |
| OpenCode | Its current disabled OTel configuration is represented as not collectable with a reason. | Owner enablement is required before collection can be verified. |
| Kilo Code | The surveyed empty local store and undocumented OTel surface are represented as not collectable with a reason. | No zero-valued data is fabricated. |

## Dashboard verification boundary

**[VERIFIED]** The Context page uses the ASAD session dashboard and canonical session
payload. The six views consume the payload directly: overview, per-turn token spend,
context composition, tool/schema cost, timeline, and cost/token accounting. Fixture and
live rendering evidence exist for the Copilot session above.

**[NOT LIVE-VERIFIED]** The full-content drawer has fixture coverage, but was not exercised
against a live session. The dashboard presents unavailability reasons instead of
shape-fallback data; an owner-controlled raw Claude body is still required for live
per-server schema bands.

## Remaining owner/runtime gates

1. Enable `OTEL_LOG_RAW_API_BODIES=1` and emit a Claude request-body log to verify live
   prompt and ground-truth per-server schema enrichment.
2. Register `codeburn kyber cursor-hook` alongside the owner's existing Cursor hooks and
   execute one turn. This repository does not edit `~/.cursor/hooks.json`.
3. Exercise the full-content drawer against a live canonical session.

## Sources

- [Claude Code monitoring & usage](https://code.claude.com/docs/en/monitoring-usage)
- [Copilot SDK — OpenTelemetry](https://github.com/github/copilot-sdk/blob/main/docs/observability/opentelemetry.md)
- [GitHub Changelog — enterprise-managed OTel export](https://github.blog/changelog/2026-07-08-enterprise-managed-opentelemetry-export-for-vs-code-and-cli/)
- [Codex configuration reference](https://developers.openai.com/codex/config-reference)
- [Cursor — OpenTelemetry Export](https://cursor.com/docs/enterprise/opentelemetry-export)
- [Kilo Code — settings](https://kilo.ai/docs/getting-started/settings)
- [opentelemetry-hooks (agent hook → OTLP runner)](https://github.com/o11y-dev/opentelemetry-hooks)

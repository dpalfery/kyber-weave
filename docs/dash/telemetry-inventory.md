---
id: dash/telemetry-inventory
title: Telemetry inventory — harness signal and content availability
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-09-06
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
The Context page contract — ASAD only, payload from `canon.db`, harnesses in scope with
reasons rather than zeros — is [ADR 0011](../adr/0011-asad-only-context-view-and-payload-contract.md).

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

## Antigravity aggregate-token investigation (T7)

**Verdict: (a), for aggregate counters.** Antigravity's September 4 statusline spans
for the two short sessions inspected (`rd-pi` and `rd-cursor`) emit
`gen_ai.usage.input_tokens: 0` and `gen_ai.usage.output_tokens: 0`. Those exact zeroes
survive into the corresponding `canon.db` `records.tokens_json` values; this is not an
ingest drop or a `NULL` value rendered as zero. **[VERIFIED]**

**[VERIFIED]** The same raw spans do export measured context-band counts under
`gen_ai.usage.sys_tokens`, `tool_tokens`, `skill_tokens`, `rule_tokens`, and
`msg_tokens`. The canonical adapter preserves them as typed `parts_json` buckets
(`system_prompt`, `tool_definitions`, `instruction_context`, and
`conversation_history`), so these names are recognized rather than an unmapped
semantic-convention rename. They are not a safe substitute for a reported aggregate
input/output counter: the producer's aggregate fields remain zero, and the component
counts describe input-context composition rather than generated output.

**[VERIFIED]** The working Gemini/Antigravity path is distinguishable: a nonzero span
from session `da5d8015-e8d1-4cbf-a6c8-612d2cff8682` carried nonzero
`gen_ai.usage.input_tokens`, `output_tokens`, and `cache_read.input_tokens` in raw
telemetry, with matching canonical token fields. The anomaly is therefore source data
for the affected short sessions, not a general Gemini adapter failure.

The required product response is the T2 formatter sentinel: show an unavailable reason
for aggregate counters instead of a numeric zero. No reader mapping change is indicated
by this investigation. Cache creation remains independently
`not_measurable` for Gemini/Antigravity.

## Cache counter and prefix byte survey (Survey E4)

Task E4 surveys whether cache counters (`cache_read`, `cache_creation`) and request-prefix
bytes can be obtained across all 10 agent harnesses in KyberDash. These signals are the
prerequisite for prefix-stability diagnosis and cache reuse analysis.

Where request-prefix bytes are unavailable, prefix-stability diagnosis degrades to the
counter-only fallback (`cache_read ÷ input`), recorded as `detect-but-cannot-locate`.
Absent signals are never rendered as zero spend or zero cache hit rate.

| Harness | Cache counters | Cache confidence | Prefix bytes | Prefix confidence | Fallback | Observed signal & rationale |
|---|---|---|---|---|---|---|
| Copilot | `supported` (`cache_read` + `cache_creation`) | `verified` | `supported` | `verified` | `detect-but-cannot-locate` | OTLP capture provides cache-inclusive usage (`gen_ai.usage.cache_read.input_tokens`, `cache_creation.input_tokens`) and maps message/instruction parts. When content capture is disabled, falls back to `cache_read ÷ input`. |
| Claude Code | `supported` (`cache_read` + `cache_creation`) | `verified` | `not_measurable` | `documented` | `detect-but-cannot-locate` | Enhanced telemetry exports cache-exclusive counters (`cache_read_tokens`, `cache_creation_tokens`). Transcripts omit runtime system prompt and tool definitions; full prefix byte reconstruction requires `OTEL_LOG_RAW_API_BODIES=1`. |
| Cursor | `unsupported` | `verified` | `not_measurable` | `verified` | `none` | `cursor-hook` and enterprise OTel export emit token totals (`promptTokens`, `outputTokens`, `schemaTokens`) without cache read/write counters. Prompt text is isolated; prefix stability cannot be computed. |
| Windsurf | `unsupported` | `documented` | `not_measurable` | `documented` | `none` | Cascade transcripts and telemetry export non-model attributes under `windsurf.*` without cache read/creation counters or request-prefix boundaries. |
| Roo Code | `supported` (`cache_read` + `cache_creation`) | `verified` | `not_measurable` | `verified` | `detect-but-cannot-locate` | Cline-family transcripts (`tasks/<id>/ui_messages.json` on `api_req_started`) record `cacheReads` and `cacheWrites`. Turn history is stored, but template system prompts are omitted on disk. |
| Cline | `supported` (`cache_read` + `cache_creation`) | `verified` | `not_measurable` | `verified` | `detect-but-cannot-locate` | Task transcripts (`ui_messages.json` / CLI session data) record `cacheReads` and `cacheWrites`. Turn history is available, but dynamic system prompts are injected at runtime. |
| Aider | `unsupported` | `documented` | `not_measurable` | `documented` | `none` | Displays cache metrics in terminal output via LiteLLM (`cache_read_input_tokens`, `cache_creation_input_tokens`), but does not export native OTLP telemetry or structured cache counters in `.aider.chat.history.md`. |
| Codex | `supported` (`cache_read`) | `verified` | `supported` | `verified` | `none` | Rollout session files record `cached_tokens` (`cache_read`); cache creation is implicit in OpenAI caching. Full system instructions (`base_instructions.text`), workspace instructions (`agents_md.text`), and conversation turns are preserved for direct prefix reconstruction. |
| Gemini / AGY | `partial` (`cache_read` only) | `verified` | `not_measurable` | `verified` | `detect-but-cannot-locate` | OTel statusline telemetry exports cached input tokens (`cached_content_token_count` / `cache_read`). Explicit caching architecture has no cache-creation counter (`PROVIDER_UNMEASURABLE: cache_creation`). Standard traces omit prompt prefix bytes. |
| OpenCode | `not_measurable` | `documented` | `not_measurable` | `documented` | `none` | Experimental OpenTelemetry is disabled by default; no session transcripts or telemetry are collected without user enablement. |

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
- [Aider — prompt caching documentation](https://aider.chat/docs/usage/caching.html)
- [Cline — architecture and session data storage](https://github.com/cline/cline)
- [Roo Code — task data storage](https://github.com/RooVetGit/Roo-Cline)
- [Windsurf — IDE and Cascade documentation](https://codeium.com/windsurf)

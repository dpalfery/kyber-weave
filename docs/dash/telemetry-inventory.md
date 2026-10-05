---
id: dash/telemetry-inventory
title: Telemetry inventory — harness signal and content availability
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-10-03
---

# Telemetry inventory — verified harness signal and content availability

This inventory records implemented collection behavior and the remaining runtime gates.
It does not treat an absent source as a zero: unavailable dimensions are
serialized as `not_measurable` with a source-specific reason.

Local-history ingest is `dash refresh` ([ADR 0016](../adr/0016-kyberdash-harness-source-refresh.md)):
schema **15** (`source_checkpoint`, `record_provenance`, `refresh_run` with
`history_weeks`), UTC `--history-weeks` default 2,
split client identities, Gemini never a stored harness id. `SURVEYED_HARNESSES` still names
`gemini` as an E4 **survey family** for cache-counter vocabulary; that is not a coding-harness
filter. A Gemini selector **label** may remain in the UI.

## Collector and canonical-record contract

**[VERIFIED]** The local receiver accepts JSON and protobuf at both `POST /v1/traces` and
`POST /v1/logs`. Non-model spans are quarantined at ingest and cannot create derived
sessions. A log correlates by `(trace_id, span_id)`, then by session and bounded timestamp
window; it enriches one existing span-shaped record, is idempotent, and is quarantined if
it remains unmatched. These are the durable decisions in [ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md).
The Context **view** contract — ASAD only, payload from `canon.db`, harnesses in scope with
reasons rather than zeros — is [ADR 0011](../adr/0011-asad-only-context-view-and-payload-contract.md).
That view is reached from the **Sessions** rail, not a Context tab.

**[VERIFIED]** The `canon.db` session projection emits the ASAD payload directly and is
rebuilt from canonical records. For a turn present in both OTLP and a dot-folder source,
counters come from OTLP; file-derived parts are used only when the OTLP record has no parts.
Values are never summed across the two sources. `KyberBridge` reads `canon.db` only;
`AGENTDASH_DB` / `KYBER_DB` cannot expose a legacy Python `sessions.db`.

## Verified harness outcomes

| Harness/source | Verified collection outcome | Availability or gate |
|---|---|---|
| Gemini statusline / Antigravity | Gemini **model** attribution recognizes `gen_ai.system = "gemini"`; non-model trace noise is quarantined. Antigravity roots (`antigravity` / `antigravity-cli` / `antigravity-ide`) are distinct harness jobs. The `kyberdash kyber antigravity-statusline` recorder appends `agy` payloads to `antigravity-statusline.jsonl`, which refresh resolves to harness **`antigravity-cli`** — the recorder path attributes there, not to `gemini`. OTLP spans asserting `gen_ai.agent.name = "antigravity"` are claimed by `antigravityAdapter` as harness **`antigravity`**, with `geminiAdapter` yielding. Canonical records must not use harness `gemini`. Legacy `gemini`-attributed records from source `agy` are not automatically migrated, but records with sufficient Antigravity evidence can be re-attributed through source-scoped repair (`kyber renormalize --source agy`), while genuine agent-name-less spans remain quarantined as `excluded_harness`. | Tool names are available; per-server schemas remain source-dependent. |
| Copilot Chat | Content-enabled OTLP capture maps observed system instructions, messages, rules, skills, tool definitions, tool results, and session identity into canonical buckets. Input-message normalization separates input text from response envelopes without negative residuals. | Observed per-server schema availability remains source-dependent. |
| Copilot CLI | SQLite ingest preserves its reported ASAD taxonomy, including `context_*_tokens` and `context_tier`. Persisted harness id is `copilot-cli`, not collapsed into `copilot`. | Omitted reported buckets remain unavailable rather than zero. |
| Copilot VS Code | Native journal request replay into input-side `ReaderTurn` snapshots keyed by native request id via `copilotVscodeReader`. Reconstructs instructions and user message while excluding current model output from input context. | Window and pressure measured; unobserved buckets explicit `null` with reason. |
| Claude Code | Enhanced-telemetry counters and dot-folder conversation/tool-result content can enter canonical records. | System prompts and tool schemas from raw API-body logs require the owner to enable `OTEL_LOG_RAW_API_BODIES=1`; that has not been assumed or configured here. |
| Codex | Dot-folder ingestion supplies the system prompt, instructions, conversation, tool results, and context window contained in rollout data. | Availability is limited to fields the source actually supplies. |
| Codex OTLP (collector path) | Codex exports logs and spans only when the owner points its exporters at the collector. `codexAdapter` claims spans by fingerprint (`codex.*` namespace plus `gen_ai.usage.*`), never by `service.name` (`codex_cli_rs`, `codex_exec`, `codex-app-server`), so records are harness `codex` without a CLI/exec/app-server split. Sessions correlate on `conversation.id`. Only an allowlist (`model`, `slug`, `conversation.id`, `event.timestamp`, `gen_ai.usage.*`, `codex.usage.*`) is persisted; prompts, tool arguments/output, `user.email` and account ids are dropped. The adapter assumes cache-inclusive input; an exclusive payload fails validation with a negative fresh input instead of being miscounted. `model` is absent on the `handle_responses` span, so such a record has no model until log enrichment is built. | Owner-capture gates before production use of this path: (1) the exported span name of `handle_responses`, (2) whether log records carry `trace_id`/`span_id`, (3) the token convention. |
| pi | Reader support is implemented and respects OTLP/file source precedence. | No current live collection claim is made. |
| Cursor | `cursorReader` extracts available user prompt, instructions, and tool context from SQLite storage without claiming an unobserved complete historical prefix. `cursor-hook` emits deterministic OTLP traces. | Window and pressure measured only when the bubble stores `contextWindow`; otherwise not measurable with reason. Unobserved historical buckets explicit `null` with reason. |
| OpenCode | Its current disabled OTel configuration is represented as not collectable with a reason. | Owner enablement is required before collection can be verified. |
| Kilo Code | Local shared-runtime SQLite store (`~/.local/share/kilo/kilo.db`) and legacy store (`.../globalStorage/kilocode.kilo-code`) ingested via `createSqliteSessionParser` under harnesses `kilo-shared-runtime` and `kilo-vscode-legacy` (`KILO_PARSER_CONTRACT_VERSION` 2). Flat token fallbacks (`tokens_input`, `tokens_output`, `tokens_reasoning`, `tokens_cache_read`, `tokens_cache_write`) validated via `finiteOrUndefined`; `type: 'markdown'` parts recognized as substantive output. Zero-record checkpoints re-parsed on contract invalidation. When stored `parserContractVersion` differs from the descriptor, `recordCount` is rebuilt from live records attributed to that source plus records created in the pass; revision-token and `lastStatus` changes accumulate `(previous?.recordCount ?? 0) + created`. | Parsed from native SQLite tables (`session`, `message`, `part`); flat token counters and markdown part types supported. Undocumented live OTel surface remains uncollected. |
| Devin | File-source ingest reads CLI transcripts under `~/.local/share/devin/cli/transcripts/` and session metadata from `sessions.db`. Discovery and parse do not require `devin.acuUsdRate`; a missing or non-finite rate still yields token/tool calls with `costIsEstimated: true` (`costUSD: 0` is never a measured zero). Doctor names the missing rate when sessions exist. | Set `devin.acuUsdRate` in `~/.kyberdash/config.json` to price ACU usage; until then cost is unpriced, not absent. |
| Warp | SQLite history under the Group Containers path opens through the read-only cache (`copyFileBestEffort`). Readable source bytes produce records even when copyfile(2) returns EPERM/EACCES; a true source EACCES/EPERM is surfaced to doctor as `permission-denied` (Full Disk Access on darwin) instead of empty sessions. Cache-dir write failures are not classified as source TCC denials. | Owner grants Full Disk Access (macOS) when the source path is TCC-denied; no fabricated rows. |

## Tool extraction support by harness (issue #180)

| Harness | Tool Extraction Status | Implementation / Scope |
|---|---|---|
| Claude Code / Desktop | **Implemented** | Extracted by Claude reader (`dash/src/synth/readers/claude.ts`) and synthesized into child `tool.invoke` spans; results bounded at 64KiB with `truncated: true` and original byte count recorded in `gen_ai.tool.result_bytes`. Structured error status preserved. |
| Codex | Deferred | Tracked in follow-up issue [#210](https://github.com/dpalfery/kyber-weave/issues/210). |
| Copilot CLI & VS Code | Deferred | Tracked in follow-up issue [#211](https://github.com/dpalfery/kyber-weave/issues/211). |
| Cursor | Deferred | Tracked in follow-up issue [#212](https://github.com/dpalfery/kyber-weave/issues/212). |
| Antigravity | Deferred | Tracked in follow-up issue [#213](https://github.com/dpalfery/kyber-weave/issues/213). |
| ZCode | Deferred | Tracked in follow-up issue [#214](https://github.com/dpalfery/kyber-weave/issues/214). |
| OpenCode | Deferred | Tracked in follow-up issue [#215](https://github.com/dpalfery/kyber-weave/issues/215). |

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

## Ingest coverage observability

Coverage is stated from rows that exist; anything unrecorded renders as unknown with a
reason, never as zero, per [honest unobservability](../rules/honest-unobservability.md).

- **Refresh window.** Each refresh run persists its window in
  `refresh_run.history_weeks` (the `--history-weeks` value of that run, default 2).
  Runs recorded before window tracking read as `null` and render as "coverage window
  unknown (recorded before window tracking)". The report and the Context Doctor banner
  print "Coverage window: last N weeks (\<from> → \<through>)" from the last
  successful run, and an empty scoped window hints
  `kyberdash dash refresh --history-weeks <n>`.
- **Ingest activity.** The live OTLP receiver writes `ingest_log` rows: one row per
  distinct span source per decoded batch (source is `service.name`, `'otlp'` when
  unnamed), sized to the arriving batch so quarantined traffic still counts as
  received, plus one `otlp:logs` row per log batch so last-received reflects any
  receiver request. Per-source activity joins `records GROUP BY source` (true for
  history, including legacy `unattributed` and `codeburn/*` rows) with `ingest_log`
  sums and `MAX(timestamp)` as `lastReceivedAt`. No rows in either table yields
  `{ status: 'unknown' }` with "no receiver activity recorded"; receiver liveness is
  never claimed from this page. `ingest_log.count` sizes the arriving batch, not the
  accepted subset: quarantined and rejected traffic still counts as received, and
  `otlp:logs` rows never produced a stored record. `GET /api/kyber/meta` exposes
  those sums as `sources[].seen`/`sources[].new`, so `new` means received-since-logging
  began, not newly stored records — read it as receiver activity, never as corpus
  growth. The audit write is fenced so it can never fail the ingest it audits.
- **Coverage route.** `GET /api/kyber/coverage` returns the refresh window, the
  ingest activity above, per-reason quarantine counts, and `source_checkpoint`
  statuses including `partial` ("N sources partial (problems recorded, coverage
  incomplete)") with "0 records" shown only where `record_count` is a measured zero.
- **Family display (display-only).** `harnessFamily` groups `claude-cli`,
  `claude-desktop`, and `claude-code` under the `claude-code` family label for
  display; stored ids stay verbatim, per-origin source counts stay visible
  beside the family label so no aggregate is fabricated, and every surface
  except the two folded twins keeps its own rollup key and API filter
  namespace (`claude-desktop` folds onto `claude-code` and `cursor-agent`
  onto `cursor` at the derived layer, issue #182).
  `/api/kyber/harnesses` rows carry `family`, the verbatim rollup `noDataReason`,
  and a checkpoint summary. Zero-data harnesses keep their rollup reason and group
  under an explicit "no records in coverage window" state.
- **Source display names.** Stored `codeburn/<provider>` names keep their prefix;
  surfaces render them through `sourceDisplayName`, which strips the prefix for
  local-file sources, renders OTLP names verbatim, and labels legacy
  `unattributed` rows as "unattributed (legacy)". The raw value rides along as
  `rawSource` for auditability.

## Dashboard verification boundary
**[VERIFIED]** The Sessions rail uses the ASAD session dashboard and canonical session
payload. The six views consume the payload directly: overview, per-turn token spend,
context composition, tool/schema cost, timeline, and cost/token accounting. The Copilot Chat
row above records collection and normalization evidence. Live session rendering remains unverified here.

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
4. Live-source `dash refresh` smoke against installed roots on a **temporary** `--db`
   remains an operator gate; T8 browser acceptance used a fixture-populated temp store,
   not `~/.kyberdash/canon.db`.
5. `GET /api/kyber/findings` has no `harness` query. HarnessDetail filters findings in
   `fetchFindings` on the client; unstamped legacy finding rows are visible on every tab.

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

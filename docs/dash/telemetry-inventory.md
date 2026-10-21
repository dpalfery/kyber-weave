---
id: dash/telemetry-inventory
title: Telemetry inventory — harness signal and content availability
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-10-19
---

# Telemetry inventory — verified harness signal and content availability

This inventory records implemented collection behavior and the remaining runtime gates.
It does not treat an absent source as a zero: unavailable dimensions are
serialized as `not_measurable` with a source-specific reason.

Local-history ingest is `dash refresh` ([ADR 0016](../adr/0016-kyberdash-harness-source-refresh.md)):
schema **20** (`source_checkpoint`, `record_provenance`, `refresh_run` with
`history_weeks`), UTC `--history-weeks` default 2,
split client identities, Gemini never a stored harness id. `SURVEYED_HARNESSES` still names
`gemini` as an E4 **survey family** for cache-counter vocabulary; that is not a coding-harness
filter. A Gemini selector **label** may remain in the UI.

## Collector and canonical-record contract

**[VERIFIED]** The local receiver accepts JSON and protobuf at both `POST /v1/traces` and
`POST /v1/logs`. Non-model spans are quarantined at ingest and cannot create derived
sessions. A log correlates by `(trace_id, span_id)`, then by session and bounded timestamp
window; it enriches one existing span-shaped record, is idempotent, and is quarantined if
it remains unmatched. Claude Code request-body logs follow a stricter rule, described in
[Claude request-body correlation](#claude-request-body-correlation). These are the durable decisions in [ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md).
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
| Copilot CLI | SQLite ingest preserves its reported ASAD taxonomy, including `context_*_tokens` and `context_tier`. Persisted harness id is `copilot-cli`, not collapsed into `copilot`. | Omitted reported buckets remain unavailable rather than zero. The `events.jsonl` journal supplies the system prompt per agent; tool definitions are not measurable. |
| Copilot VS Code | Native journal request replay into input-side `ReaderTurn` snapshots keyed by native request id via `copilotVscodeReader`. Reconstructs instructions and user message while excluding current model output from input context. | Window and pressure measured; unobserved buckets explicit `null` with reason. |
| Claude Code (`claude-cli`, `claude-desktop`, `claude-code`) | Enhanced-telemetry counters; live OTLP content from the interaction span's `user_prompt` and the `api_request_body` log (see [Claude Code OTLP content (live)](#claude-code-otlp-content-live)); plus JSONL conversation and tool-result parts on file-synth (`synth:`) records when the Claude content reader is paired at ingest ([issue #216](https://github.com/dpalfery/kyber-weave/issues/216)). | `instruction_context` and `schema_cost` stay not measurable. Session files never carry `system_prompt` or `tool_definitions`; only the OTLP request body supplies those two, and it supplies `tool_definitions` only when the sender exported a `tools` array (sub-agent calls in Desktop). See [Claude Code OTLP content (live)](#claude-code-otlp-content-live) and [Claude Desktop and CLI file-synth parts](#claude-desktop-and-cli-file-synth-parts-issue-216). |
| Codex | Dot-folder ingestion supplies the system prompt, instructions, conversation, tool results, and context window contained in rollout data. | Availability is limited to fields the source actually supplies. |
| Codex OTLP (collector path) | Codex exports logs and spans only when the owner points its exporters at the collector. `codexAdapter` claims spans by fingerprint (`codex.*` namespace plus `gen_ai.usage.*`), never by `service.name` (`codex_cli_rs`, `codex_exec`, `codex-app-server`), so records are harness `codex` without a CLI/exec/app-server split. Sessions correlate on `conversation.id`. Only an allowlist (`model`, `slug`, `conversation.id`, `event.timestamp`, `gen_ai.usage.*`, `codex.usage.*`) is persisted; prompts, tool arguments/output, `user.email` and account ids are dropped. The adapter assumes cache-inclusive input; an exclusive payload fails validation with a negative fresh input instead of being miscounted. `model` is absent on the `handle_responses` span, so such a record has no model until log enrichment is built. | Owner-capture gates before production use of this path: (1) the exported span name of `handle_responses`, (2) whether log records carry `trace_id`/`span_id`, (3) the token convention. |
| pi | Reader support is implemented and respects OTLP/file source precedence. | No current live collection claim is made. |
| Cursor | `cursorReader` extracts available user prompt, instructions, and tool context from SQLite storage without claiming an unobserved complete historical prefix. `cursor-hook` emits deterministic OTLP traces. | Window and pressure measured only when the bubble stores `contextWindow`; otherwise not measurable with reason. Unobserved historical buckets explicit `null` with reason. |
| OpenCode | Its current disabled OTel configuration is represented as not collectable with a reason. KyberDash can now set the `opencode.jsonc` OpenTelemetry flag from the Settings drawer or `kyberdash kyber capture enable --harness opencode` (see [in-app enablement](#in-app-collection-enablement)). | The endpoint is read from the process environment, not the file, so `OTEL_EXPORTER_OTLP_ENDPOINT` must still be set by the owner where OpenCode launches. Owner enablement and a live turn are required before collection can be verified. |
| Kilo Code | Local shared-runtime SQLite store (`~/.local/share/kilo/kilo.db`) and legacy store (`.../globalStorage/kilocode.kilo-code`) ingested via `createSqliteSessionParser` under harnesses `kilo-shared-runtime` and `kilo-vscode-legacy` (`KILO_PARSER_CONTRACT_VERSION` 3). Flat token fallbacks (`tokens_input`, `tokens_output`, `tokens_reasoning`, `tokens_cache_read`, `tokens_cache_write`) validated via `finiteOrUndefined`; `type: 'markdown'` parts recognized as substantive output. Zero-record checkpoints re-parsed on contract invalidation. When stored `parserContractVersion` differs from the descriptor, `recordCount` is rebuilt from live records attributed to that source plus records created in the pass; revision-token and `lastStatus` changes accumulate `(previous?.recordCount ?? 0) + created`. | Parsed from native SQLite tables (`session`, `message`, `part`); flat token counters and markdown part types supported. Undocumented live OTel surface remains uncollected. |
| Devin | File-source ingest reads CLI transcripts under `~/.local/share/devin/cli/transcripts/` and session metadata from `sessions.db`. Discovery and parse do not require `devin.acuUsdRate`; a missing or non-finite rate still yields token/tool calls with `costIsEstimated: true` (`costUSD: 0` is never a measured zero). Doctor names the missing rate when sessions exist. | Set `devin.acuUsdRate` in `~/.kyberdash/config.json` to price ACU usage; until then cost is unpriced, not absent. |
| Warp | SQLite history under the Group Containers path opens through the read-only cache (`copyFileBestEffort`). Readable source bytes produce records even when copyfile(2) returns EPERM/EACCES; a true source EACCES/EPERM is surfaced to doctor as `permission-denied` (Full Disk Access on darwin) instead of empty sessions. Cache-dir write failures are not classified as source TCC denials. | Owner grants Full Disk Access (macOS) when the source path is TCC-denied; no fabricated rows. |

## In-app collection enablement

The Settings drawer in the web dashboard (and `kyberdash kyber capture`) can switch OTLP
collection on or off for three harnesses: Copilot, Claude Code and OpenCode. Codex, pi and
Antigravity are reported read-only. Enabling edits the harness's own config and, on a real
enable, turns the OTLP receiver on; it is not evidence of collection. The inventory rows above
stay gated on an owner capture: a green enable proves keys were written, not that the harness
exported a turn. See the
[runbook](runbook.md#harness-opentelemetry-capture) and
[ADR 0031](../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md).

## Claude Desktop and CLI file-synth parts (issue #216)

**[VERIFIED]** `claude-desktop` and `claude-cli` enter the store through `dash refresh`
as file-synthesized `synth:` spans (wire source `codeburn/claude-desktop` /
Claude CLI equivalents). When ingest pairs counter calls with
`ClaudeContentReader` turns, those records carry structured parts the transcript
actually supplies — the turn inspector is not inherently empty for conversation
content. Closeout of [#216](https://github.com/dpalfery/kyber-weave/issues/216)
is to keep that capture path durable and documented; content is never fabricated.

| Content bucket | From Claude session JSONL? | Notes |
|---|---|---|
| `conversation_history` | Yes | Message text and thinking blocks from the transcript. |
| `tool_result_content` | Yes | When `tool_result` blocks are present. |
| `system_prompt` | No | Honest `not_measurable` — session files omit the runtime system prompt. |
| `tool_definitions` | No | Honest `not_measurable` — files record invocations, not definition schemas. |

Prefix-byte reconstruction for those two omitted buckets still requires
`OTEL_LOG_RAW_API_BODIES=1` (same gate as the Claude Code survey row above).
Success for Desktop/CLI file synth is non-empty conversation (and tool results
when present), not a full five-block inspector.

Pairing that attaches parts (shipped with the #216 capture fix):

- `ClaudeContentReader` emits `nativeRecordId` from Claude `message.id` so
  `matchingTurns` can join to the call's `turnId`. Id-less calls and id-map
  misses fall back to `turns[index]` unless the reader declares
  `positionalPairingUnsafe`. A positional fallback whose turn carries its own
  `nativeRecordId` still requires that id to equal the call's `turnId`, so
  readers that stamp ids only reach the positional arm for id-less calls.
  Cursor is never paired positionally.
- The reader honors the same UTC `dateRange` as refresh window-sliced calls, so
  a partial history window cannot attach an out-of-window turn's text.
- When a unit's first ingest returns zero records, the refresh orchestrator
  retries with `filePath` retained so the content reader still runs; a bare
  call array (no path) recovers counters only and strips parts.

Repair for rows ingested before part capture: advancing the Claude family
`parserContractVersion` in the harness-source registry makes prior
`source_checkpoint` rows stale. The next owner `dash refresh` re-reads
transcripts and re-synthesizes affected units still inside the 14-day
content-retention window
([ADR 0016](../adr/0016-kyberdash-harness-source-refresh.md),
[ADR 0018](../adr/0018-kyberdash-content-retention-purge.md)).
Rows older than that floor are not rewritten.
`kyber backfill` cannot repair file-synth rows — their stored `raw` is the
counter call, not the message body.

## Claude Code OTLP content (live)

**[VERIFIED]** When Claude Code exports text-bearing telemetry, the receiver stores it as
canonical content parts and the turn view renders it — no folder import required. The contract
is `harness === 'claude-code' && op === 'llm.invoke'`; the span name is never consulted (B1),
because Anthropic documents its emitted names as beta and a single rename would otherwise drop
content (R2). Two sources supply the text.

- **The interaction span's `user_prompt`.** A Claude Code interaction span carries no counters,
  so it is quarantined as `non-model span`; its prompt is harvested before the packet is filed
  and attached to every `llm.invoke` child of that interaction — records whose `parentSpanId`
  is the interaction span's id — as one `conversation_history` part per child (Q4). Every API
  call in the turn re-sends the prompt, so the turn's other context still shows as the
  unattributed residual. The span is recognised by `span.type === 'interaction'` **or** the
  name `claude_code.interaction` (B2). When the children arrive before the interaction, the
  prompt waits in `pending_logs`, keyed by trace and interaction span id, under the existing
  TTL; on expiry the prompt survives only in the quarantine packet (B8).
- **The `api_request_body` log event.** Recognised by
  `attributes['event.name'] === 'api_request_body'` **or**
  `LogRecord.body === 'claude_code.api_request_body'`; a Claude event in general is recognised
  by either of those or by the instrumentation scope `com.anthropic.claude_code.events`, and
  that recognition also stamps `harness: 'claude-code'` on the row for wipe scoping (B3). The
  JSON is read from the `body` attribute and parsed into parts (B5): user and assistant text →
  `conversation_history`, `tool_result` → `tool_result_content`, `tool_use` →
  `conversation_history` as JSON, image and document blocks → the placeholder `[<type> block]`
  (never base64), thinking blocks skipped. Message order is kept, and no heuristic
  `instruction_context` split is invented.

A redacted `user_prompt` (`<REDACTED>`) yields no part and a not-measurable reason naming
`OTEL_LOG_USER_PROMPTS=1`. A request-body log that arrives as a file reference (`body_ref`)
instead of an inline `body` yields no part and a reason naming that reference; the receiver
never opens the local path it names — telemetry is not a file-read surface (B3). A body cut at
the sender limit (`body_truncated`) or one that will not parse yields no part and a reason
naming `CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH`, which is also the knob that raises the ceiling.

Where a bucket gains parts, its per-record `not_measurable` declaration is cleared (B6), and the
turn view marks a block not measurable only when that turn's block has no parts — a turn with a
`conversation_history` part no longer inherits the session's "not measurable" banner. The
quarantine detail route and the turn-view raw-packet disclosure that expose the underlying
packets are in the [runbook](runbook.md#2f-seeing-a-turns-raw-packet).

**Live sender verification is owner gate 1 below.** The receiver contract is covered by a
synthetic-OTLP end-to-end regression through the real receiver and store; no live Claude Code
sender has exercised it on this checkout, so the sender side is
**[NOT LIVE-VERIFIED]**.

**Retention.** Assembled turn text is purged after 14 days. The raw packet keeps exactly what
the sender sent until you wipe the `claude-code` harness or the whole database
([ADR 0018](../adr/0018-kyberdash-content-retention-purge.md),
[ADR 0035](../adr/0035-kyberdash-quarantine-raw-packet-evidence.md)). The 14-day window is not
"all prompt text expires" — raw packets outlive it by design.

### Claude request-body correlation

Claude Code logs its `api_request_body` event when a model request starts, but exports the
`llm_request` span only when the request ends. The body therefore usually arrives first. The
log also carries the parent span that was active when it was logged, not the call's own span:
`claude_code.interaction` for a main-thread call, or `claude_code.tool.execution` when the call
follows a tool call. Sub-agent `llm_request` records are children of `claude_code.tool.execution`
and start about 115 ms after it ends. Correlation therefore scopes by **trace** (or by session,
for a body with no trace id), not by parent span. Desktop model-call records are harness
`claude-code`, so the existing enrichment gate admits them.

The rule, in order:

1. **Exact span.** A body whose `(traceId, spanId)` names a record enriches that record. The
   Desktop capture never takes this path (0 of 3,413 trace-bearing bodies at the 2026-10-19
   snapshot); the CLI shape does.
2. **Trace-scoped nearest start.** Otherwise the candidates are the `claude-code` `llm.invoke`
   records in the body's trace. A candidate qualifies when its start is within **2 seconds** of
   the body's timestamp **and** the call had not ended when the body was logged (the body's
   timestamp is no later than the start plus `durationMs`). The **nearest start** wins. A tie, or
   no candidate, leaves the body pending. A call that ended before the body is never chosen, so
   a body waits for its own call instead of attaching to an earlier one.
3. **Session scope for context-free bodies.** A body with no trace id but with a `session.id`
   uses the same rule over the session's `llm.invoke` records. The receiver reads the session key
   from `session.id` as well as `session_id`, so both shapes populate it.
4. **Pending.** A pending body is retried after every span batch and on the service's reconcile
   timer. A Claude request-body log waits up to **15 minutes** (`CLAUDE_REQUEST_BODY_PENDING_TTL_MS`);
   every other log keeps the 5-second default. On expiry a body that never matched is quarantined as
   "identified log remained unmatched past pending expiry". A body with no trace id, no span id, and
   no session id quarantines at once, because nothing can ever scope a match for it. A context-free
   body that names a session always pends for the full window (rule 3), whatever records the session
   holds.
5. **One body per record: the later body wins.** A record that already holds a body stays a
   candidate. When a second body matches it, the body with the **later log timestamp** supplies
   the record's body-derived parts (the same per-bucket merge as before). The earlier body is
   marked enriched as superseded and applies no parts. Neither body waits or is quarantined.
   Arrival order does not matter; the comparison is by log timestamp. The claim (`logId`, the log
   timestamp, and `request_body_id` when present) is kept in the record's `raw` under
   `kyber.claude_request_body`.

**Measured at the 2026-10-19 snapshot** (3,413 trace-bearing body logs): 3,398 (99.56%) resolve to
a unique nearest candidate; no ties; 15 have no candidate; 0 resolve to a call that had already
ended. A body is logged 1 ms after its call's start at the median and 7 ms at p95 (maximum 717 ms),
never before it. Across all 3,456 body logs, including the 43 context-free ones, 98.32% resolve.

### Tool definitions and skill listings from request bodies

**Tool definitions.** Claude Code exports a `tools` array only in some bodies. In the snapshot,
265 of the parseable bodies (7.8% of all body logs) carry one, and they are sub-agent calls. The
rest, including every main-thread body, do not.

- A parseable body with a `tools` array yields `tool_definitions` parts, one per tool, with the
  `server` when the sender named it.
- A parseable body with no `tools` array declares `tool_definitions` **not measurable**, with the
  reason "This Claude Code request body carried no tools array, so the sender did not export tool
  definitions for this call." The turn inspector shows that reason. It never shows an empty list,
  because an empty list would claim the call had no tools.
- A later body for the same record that does carry `tools` clears that declaration.
- A later body that wins over an earlier claimed body replaces the record's `tool_definitions`
  parts when the earlier body supplied them, even if the later body has no `tools` array, so the
  earlier body's tools cannot hide the reason. The claim records which buckets its body supplied
  (`suppliedBuckets`), so a `tool_definitions` part that came from the span is kept.
- A body that does not parse yields the body-failure reason instead (see accepted residuals).

**Skill listings.** Claude Code writes its skill listing into a `role: "system"` message: the line
"The following skills are available for use with the Skill tool:", a blank line, then `- name:
description` entries. The body parser keeps that text as a `conversation_history` part and records
the message role on the part ([message role on content parts](architecture.md#message-role-on-content-parts)).
The skills reader takes a listing from a body only when all of the following hold:

- The record has no transcript listing. A recorded `skills_listed` wins, then the Copilot,
  Antigravity, and system-prompt readers run before the body reader.
- The text is a `system_prompt` part, or a `conversation_history` part from a `system` message.
  Text a user or the assistant quoted is ignored, and so are parts from messages with no recorded
  role.
- The entries parse: they are read from the marker to the first non-entry line after them.

The listing is treated as initial, so it replaces the session's effective list. Claude Code sends a
listing when it is (re)issued, and the session-level merge carries it across that session's later
turns. A body cut at the sender's size limit does not parse, so it yields no listing. Stored parts
written before the role field existed carry no role and are ignored, so a record enriched before
this change shows no body listing until its body is enriched again. The startup relink does that
for quarantined bodies ([runbook §2i](runbook.md#2i-claude-request-body-relink-and-quarantined-count)).

### Accepted residuals

Counts from the 2026-10-19 store snapshot. They grow with use, so the owner check reports the
current figures on the day it runs.

- 3,456 body logs, all quarantined before the fix: 3,413 with a trace id and 43 without.
- 3,599 `claude-code` `llm.invoke` records.
- 265 parseable bodies with a `tools` array.
- 308 bodies with the skill listing: 269 parseable and 39 truncated.

| Residual | Size | Handling |
|---|---|---|
| A trace-bearing body with no call starting within 2 s | 15 of 3,413 (0.44%) | Waits the 15-minute TTL, then quarantines with the existing reason. The relink leaves it quarantined. The window is not widened: the nearest same-trace call is 3 to 53 s away, past the roughly 3-second call spacing, so a wider window would attach the body to the wrong call. The raw body stays in `quarantined_logs` as evidence. |
| A context-free body with a session id and no call within 2 s in its session | None of the 43 context-free bodies in the snapshot lacked a unique nearest call. | Pends the full 15-minute window, then quarantines with the existing reason. A tool invocation is never a candidate, so a tool record neither claims the body nor shortens the wait. A body with no trace, span, or session id quarantines at once. |
| A later body that replaces an earlier claimed body and has no `tools` array | Count not measured. | The buckets the earlier body supplied (recorded in the claim as `suppliedBuckets`) are replaced, so the turn shows the not-measurable reason rather than the earlier body's tools. Span-sourced parts in other buckets stay. A claim written before `suppliedBuckets` existed drops `system_prompt` and `tool_definitions` wholesale. |
| A body cut at the sender's size limit (near 1 MB) | 39 of the 308 listing-bearing bodies, the only truncated subset counted | Does not parse, so it yields no parts, no listing, and no tool definitions. The reason names `CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH`. The raw packet is kept. |
| A later body that is a side request rather than a retry | 50 records are the nearest match for two bodies | The later body wins. This is one comparison, and it is cheap to reverse if the wrong body shows. |
| Quarantined rows left by the one-time startup relink | Rows that were unmatched, tied, older than the 14-day window, or whose stored payload does not parse, at that pass | They stay quarantined, and a later start does not retry them, because the watermark moves past them ([runbook §2i](runbook.md#2i-claude-request-body-relink-and-quarantined-count)). |

### Enabling Claude Code content on the sender (Q2)

KyberDash does not apply these settings automatically; they are the sender's opt-in, and the
sender's opt-in is the consent ([ADR 0014](../adr/0014-unclipped-turn-inspection-and-copy-out-protocol.md)).
`kyberdash kyber capture` writes them into the harness's own user-level config only when the
owner runs it ([ADR 0031](../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md)).

| Setting | Effect | Exposure |
|---|---|---|
| `OTEL_LOGS_EXPORTER=otlp` | Sends Claude Code event logs (including `api_request_body`) to the receiver. Without it, only traces arrive and no request body is sent. | Enables the two settings below; on its own it sends counters and metadata only. |
| `OTEL_LOG_USER_PROMPTS=1` | Exports the interaction span's `user_prompt` as text instead of `<REDACTED>`. | Exports the user's prompt text verbatim. |
| `OTEL_LOG_RAW_API_BODIES=1` | Inlines the raw API request body in the log event. `file:` would spill untruncated bodies onto disk instead. | Exports the full system prompt, tool definitions, and message history verbatim. |
| `CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH` | Raises Claude Code's 61,440 UTF-16-unit inline-body ceiling (the capture writer sets 1,048,576). | Larger bodies mean more prompt text stored; the ceiling is a size limit, not a privacy control. |

**Data-exposure warning.** With these enabled, the receiver stores the system prompt, tool
definitions, message history, and the user's prompt as plaintext content parts, served on the
loopback-only `/api/kyber/*` API. Assembled turn text is purged after 14 days; the raw packet
keeps exactly what the sender sent until you wipe the `claude-code` harness or the whole
database. Set them only when you accept that exposure.

### The import-history alternative (issue #216)

A one-off owner run of `kyberdash dash import-history` fills `conversation_history` and
`tool_result_content` from the session's JSONL transcript. It never fills `system_prompt` or
`tool_definitions` — transcripts do not carry them — so the OTLP request body above is the only
route to those two buckets. Folder import is off by default on a schedule since
[issue #319](https://github.com/dpalfery/kyber-weave/issues/319)
([ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md) decision 6); the OTLP
receiver path is the import-free route to live content. This plan neither runs nor changes the
import path.

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

## Skill availability by harness

Skills are measured as two independent facts per harness: the **listing** (the skills made
available to the session) and the **invocation** (a skill actually loaded). Each side is
either measured or absent with a reason; an absent side is never zero. The engine and the
payload contract are in the [skills view contract](architecture.md#skills-view-contract).

| Harness | Listed | Invoked | Enabling settings and sources | G0 verification (2026-10-13) |
|---|---|---|---|---|
| Claude Code (`claude-code`, `claude-cli`) | **Measured.** An `attachment` entry of type `skill_listing` in the session transcript JSONL (`~/.claude/projects/*/*.jsonl`) with `content` (`- name: description` lines), `skillCount`, `isInitial`, and `names[]`. The Claude transcript parser reads it through `dash/src/ingest/skill-listing.ts` (both the normal and large-line paths) into the raw attribute `kyber.skills.listed` (`{isInitial, skills: [{name, description?}]}`). An initial listing replaces the effective list; a later non-initial listing unions into it. When the transcript records no listing, the listing is read from the OTLP request body (see [tool definitions and skill listings](#tool-definitions-and-skill-listings-from-request-bodies)). | **Measured.** Assistant `tool_use` blocks named `Skill`, from `input.skill`, plus the stored `kyber.skills.invoked` attribute. | No sender setting. The data is read from the transcript by `dash refresh`, a file source. The Claude parser contract is `7` (`CLAUDE_PARSER_CONTRACT_VERSION` in `dash/src/refresh/registry.ts`). Descriptions survive the 14-day content purge because they are kept in raw attributes. | **Verified** from real transcripts on the owner's machine. Fixture: `dash/src/canon/fixtures/skills/claude-code/`. |
| Claude Desktop (`claude-desktop`) | **Measured from the OTLP request body** when the body carries the listing, and from the transcript when one is recorded (the transcript wins). Desktop model-call records are harness `claude-code`, so the body path applies to those records. The `claude-desktop` transcript path was not separately checked for a `skill_listing`. | Same extraction as Claude Code, if the transcript carries `Skill` calls (not separately verified). | As Claude Code: the transcript, or the request body. | **Body-sourced listing verified** on the 2026-10-19 store snapshot (308 bodies carry it). Not yet confirmed on a live turn (owner gate 1). Transcript listing still unverified. |
| GitHub Copilot (`copilot`, source `github-copilot`) | **Measured** on `invoke_agent` spans from `github.copilot.context.skills`, a JSON-array string of names. This key is not `gen_ai.skills`. Descriptions come from the `<available_skills>` block in the system-prompt part, when that part is stored. A listing on a rollup `invoke_agent` applies to the turns directly under it. VS Code Copilot records no listing key; its listing is read from the `<skills>` block in the system-prompt part instead, and a prompt with no such block stays not measurable. | **Not measurable** unless a `skill` tool record exists. None exists in the owner's store, which holds only `view`, `task`, `bash`, and `edit` tool spans. | OTLP capture from Copilot ([in-app enablement](#in-app-collection-enablement)). Descriptions need the system-prompt part stored, which the content-enabled capture described in the Copilot Chat row provides. | **Verified** for the listing (one real `invoke_agent` span). Invocation is not measurable in the owner's store. Fixture: `dash/src/canon/fixtures/skills/copilot-chat/`. |
| Copilot CLI (`copilot-cli`) | **Measured** from `system.message` events in `~/.copilot/session-state/<id>/events.jsonl`. The reader attaches the prompt of the same agent (root events carry no `agentId`, subagent events name theirs) to each `assistant.message` turn as a `system_prompt` part, and the skills engine parses the `<available_skills>` block for names, descriptions and derived tokens. Root turns lead, so calls without a message id (rollup legs) that pair by position stay within the root agent's turns; a call past the turn list gets no prompt. | **Not separately verified.** It follows the Copilot rule above: not measurable unless a `skill` tool record exists. | Reads the journal by `dash refresh`, a file source. No sender setting. | **Verified** on a real session on the owner's machine (2026-10-21). Tool definitions are **not measurable**: the journal holds no tool schemas, and the prompt's `<tools>` section is guidance for a subset of tools, not the offered set. |
| Antigravity (`antigravity`) | **Measured** on `llm.invoke` spans. `gen_ai.skills` is one string, `Active Skills: a, b, ...`. The harness-reported `gen_ai.usage.skill_tokens` gives the per-turn resident size, which was identical on every turn in the owner's data. | **Not measurable.** No activation record exists in the store. | OTLP spans claimed by `antigravityAdapter` (see [Verified harness outcomes](#verified-harness-outcomes)). No separate skill setting was identified. | **Verified** for the listing (one real `llm.invoke` span). Invocation is not measurable. Fixture: `dash/src/canon/fixtures/skills/antigravity/`. |
| Pi (`pi`) | **Not measurable**, with a reason that names the absent source. | Parser-extracted names only, where the Pi parser provides them, stored as raw `kyber.skills.invoked`. Wire format **unverified**. | Pi file-source ingest. The Pi parser contract was bumped for the skills work (`PI_PARSER_CONTRACT_VERSION` is `3`). | **Unverified.** The owner's store holds no Pi records. Fixture README: `dash/src/canon/fixtures/skills/pi/`. |
| OpenCode (`opencode`) | **Not measurable**, with a reason that names the absent source. Wire format **unverified**. | Parser-extracted names only, where the OpenCode parser provides them, stored as raw `kyber.skills.invoked`. | OpenTelemetry enablement through [in-app enablement](#in-app-collection-enablement) (`opencode.jsonc`). The endpoint still comes from the process environment. | **Unverified.** The owner's store holds no OpenCode records. Fixture README: `dash/src/canon/fixtures/skills/opencode/`. |
| Other file-ingested harnesses that record invoked names (Codex, Kilo Code, Cline CLI, CodeWhale, OpenClaude, DSH) | No listing source was verified for these in G0. They are **not measurable** until one is. | Parser-extracted names only, where the parser provides them, stored as raw `kyber.skills.invoked`. The parsers' contract versions were bumped for this work (Codex is `3`, Kilo is `3`). | Dot-folder or file-source ingest through `dash refresh`. | **Unverified.** |
| Any other or unknown harness | **Not measurable**, with a reason. | Only where the parser supplies names. | None beyond the harness's own collection. | **Unverified.** |

**Invoked names on the turn record.** For file-ingested harnesses, the parsers already extract
skill names into `ParsedProviderCall.skills`. The synthesizer stores them on the turn record as
the raw attribute `kyber.skills.invoked`. The list is de-duplicated and kept in first-seen order,
and the key is absent when there are none. Absent means not measured, and `[]` on the payload is
the measured none described in the [skills view contract](architecture.md#skills-view-contract).

**Listing precedence (Claude Code).** An initial `skill_listing` (`isInitial: true`) replaces the
effective list for the session. A later non-initial listing is unioned into it. A skill's
description is the first one observed in the session. A listing read from a request body is used only when the record has no transcript listing.

**Parser contracts.** The Claude family, Codex, Pi, Copilot, Kilo, OpenCode, Cline CLI,
CodeWhale, OpenClaude, and DSH were bumped for the skill extraction. The current values are in
`dash/src/refresh/registry.ts`. Advancing a contract makes existing checkpoints stale, so the
next `dash refresh` re-reads them under the refresh window rules. Whether sessions older than
that window gain a listing after a bump is **unverified**. See the [runbook](runbook.md#2a-local-harness-source-refresh-dash-refresh).

**Fixtures.** The G0 captures are sanitized, real-shape records under
`dash/src/canon/fixtures/skills/<harness>/`. Each folder has a README that states what was found
or not found. The `claude-code` capture is read by the Claude `skill_listing` parser test.

## Antigravity tool definitions and system prompt

**Verdict: partly recoverable.** Inspected read-only against a real `~/.gemini/antigravity*`
tree (counts and shapes only; no content is committed).

| Item | On disk? | Where / why |
|---|---|---|
| MCP tool schemas (name, description, `parameters`) | **Yes** | `<root>/mcp/<serverName>/<toolName>.json`, beside an `instructions.md` per server. The agent's own prompt text (stored in the conversation DB) says each MCP server directory holds "tool schemas", read on demand; "eagerly loaded" tools are additionally registered natively. |
| Built-in tool schemas (`list_dir`, `run_command`, ...) | **No** | Defined inside the language-server binary. Absent from the conversation DB (`steps`, `gen_metadata`, `executor_metadata`, `trajectory_metadata_blob`), `cli.log`, `log/`, `brain/`, `builtin/` and the `GetCascadeTrajectoryGeneratorMetadata` response shape the provider already parses. Only invocations survive, as tool name plus JSON arguments in `steps.step_payload`. |
| System prompt | **No** | `gen_metadata` rows hold the model, token counters and request ids only. |
| Skill text | Partly | `builtin/skills` holds skill files, not what the model was shown. Not wired. |

**What KyberDash does.** `dash/src/synth/readers/antigravity.ts` reads the MCP schema
directory of the root that owns a conversation DB (`antigravity`, `antigravity-cli`,
`antigravity-ide`) and emits one `tool_definitions` part per server, with `server` set from
the directory name. It attaches them to the session's first turn only. Limits, stated so
they are not read as more than they are:

- The catalogue is a **snapshot of the directory at ingest time**, not a per-turn record. A
  server added or removed after the session ran is reflected anyway.
- It is the catalogue the agent *could reach*, not proof of what was resident in context on any
  turn, which is why `schema_cost` stays unmeasurable for Antigravity.
- Built-in tools, the system prompt and skill text stay unmeasurable. A session with no MCP
  directory shows the dashboard's "does not export tool definitions" banner and a tool drawer
  that says so instead of a blank panel.
- Invoked tool names are not yet extracted (the provider emits `tools: []`), so the Tools list
  shows offered MCP tools with zero recorded invocations. The names exist in
  `steps.step_payload`; surfacing them is a separate change.

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

1. Enable `OTEL_LOGS_EXPORTER=otlp`, `OTEL_LOG_USER_PROMPTS=1`, and `OTEL_LOG_RAW_API_BODIES=1`
   for one Claude Code process (no settings-file edit) and capture one `api_request_body`
   event, to verify the [Claude Code OTLP content](#claude-code-otlp-content-live) path against
   a live sender — and to record its `LogRecord.body`, attribute keys, scope name, and whether
   `body_truncated` fired. Until then that section, including the [request-body correlation](#claude-request-body-correlation) and its tool-definition and skill-listing reads, is **[NOT LIVE-VERIFIED]**, and live ground-truth per-server schema enrichment remains
   unverified. After the OTLP service restarts against the real store (`kyberdash web` or
   `kyberdash otel`), the owner checks:
   - the start-up line `Relinked N of M quarantined Claude request bodies in T ms` prints, and the
     quarantined Claude body count falls toward the residual in the
     [runbook §2i](runbook.md#2i-claude-request-body-relink-and-quarantined-count);
   - a recent Claude Desktop main-thread turn shows its system prompt, and its tool definitions show
     the "no tools array" not-measurable reason rather than an empty list;
   - a Claude Desktop sub-agent turn shows tool definitions;
   - a session whose request body carried the skill listing shows that listing on the Skills view
     (if the session row is stale, run the build in the runbook first).
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

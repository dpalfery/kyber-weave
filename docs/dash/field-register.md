---
id: dash/field-register
title: Field register — native reader properties and relational schema dispositions
doc-type: reference
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-11-19
keywords:
  - field register
  - native reader
  - relational schema
  - zcode
---

# Field register — native reader properties and relational schema dispositions

This register maps every C# property on KyberDash's native transcript reader types, OTLP wire-ingest types, and hook mapper types to its target column in the normalized relational schema (`RelationalSchemaCatalog`) or documents why it is `dropped`.

`FieldRegisterTests` reflects each harness's native types against the sections below and fails if any property is undocumented, duplicated, stale, mapped to a non-existent catalog column, or `dropped` without a reason. For collector DTO invariants and field-level extraction rules, see `CollectedTypes` and `DefinedFieldList`; for retired v24 table dispositions, see `LegacyTableReplacementMap`; for live collection status and runtime gates, see [Telemetry inventory](telemetry-inventory.md).

## claude-code

Claude Code ingests from two paths: the local JSONL transcript path (`ClaudeTranscriptMapper` mapping `ClaudeTranscriptLine`, `ClaudeTranscriptMessage`, `ClaudeTranscriptUsage`, and `ClaudeTranscriptContentBlock` directly to `CollectedBatch`, alongside legacy `TranscriptReaderRepository.ReadClaudeTurns` / `ReadClaudeEnvelopes` producing `ClaudeTurnGroup`, `ClaudeUsageLine`, `SourceRecordEnvelope`, `ParsedProviderCall`, `ReaderTurn`, and `ContentPart`) and the OTLP collector/adapter (`ClaudeCodeAdapter` and `ClaudeSpanMapper` via `OtlpSpanNative` over `OtlpSpan`, `OtlpLog`, `OtlpMetric`, and `RawSpan`).

In `ClaudeTranscriptMapper`, each JSONL line maps to `CollectedSession`, `CollectedRecord` (`llm.invoke` parent turns and paired `tool.invoke` child spans), `CollectedContentPart`, `CollectedToolCall`, and `CollectedRecordSignals`:

- `ClaudeTranscriptLine`: `SessionId` maps to `session.native_session_id` / `record_session_link.session_key` (falling back to transcript file stem); `SessionTitle` (`customTitle` / `aiTitle`, trimmed to 200 chars) maps to `session_title.title`; `Timestamp` maps to `record.started_at_utc` (or `record_end.ended_at_utc` when pairing `tool_result` timestamps); `TerminationReason` (`terminationReason`, `stop_reason`, or `exit`/`result`) and `ExitCode` (`exitCode`, `exit_code`, `code`) map to `record.status`; `AttributionMcpServer` maps to `content_part_server.server_name`; `RecordType` outside `exit`/`result` and malformed/non-object JSONL lines are dropped after grouping.
- `ClaudeTranscriptMessage`: `Id` seeds deterministic `record.span_id` (`synth:claude-code:<sessionId>:<id>`, or SHA-256 fallback digest when absent); `Role` maps to `content_part_role.role_name` and gates `CanonFindings.DetectUserCorrection` (`record_correction` / `record_correction_rule`); `Model` maps to `record_model.model_id` (omitted when absent); `ScalarContent` (including numeric and boolean values in text slots) maps through `ToolContentMeasurer` to `content_part_text.part_text` or `content_part_withheld`; `Blocks` unpacks `text`, `thinking`, `tool_use`, and `tool_result` entries (unknown block types are dropped).
- `ClaudeTranscriptUsage`: `InputTokens`, `OutputTokens`, `CacheReadInputTokens`, `CacheCreationInputTokens`, and `ReasoningTokens` map conditionally (absent JSON keys emit no `record_token_count` row) to `token_class` values `input`, `output`, `cache_read`, `cache_write`, and `reasoning` plus `record_reported_total` (`input` sum and `output`); streaming duplicate usage groups within 60 s are merged and usage-less tail lines fold into the preceding turn.
- `ClaudeTranscriptContentBlock`: `text` and `thinking` map to `content_part` (`conversation_history`, withholding JSON objects/arrays and nested JSON strings while preserving prose with JSON fragments and numeric/boolean scalars); `tool_use` emits child `tool.invoke` spans (`tool_call.call_id`, `tool_call.tool_name`, `mcp__<server>__<tool>` -> `tool_call_mcp`, and `RawInputText` measured with 64 KiB cap and SHA-256 fingerprint); `tool_result` pairs across turns by `ToolUseId`, maps `ResultContentText` through `ToolContentMeasurer` onto `tool_call_result_*` (without fingerprint) and `content_part` (`tool_result_content`), and maps `IsError` / `ExitCode` / `ErrorCode` onto `tool_call_outcome`, `tool_call_error`, `tool_call_error_code`, and `tool_call_exit_code`; `InputIsString` is a transient serialization discriminator dropped after hashing.

For OTLP span mapping into static collector DTOs, `OtlpSpanNative` extracts canonical `gen_ai.*`, `claude.*`, and `claude_code.*` attributes from `OtlpSpan` (model, session, cache-exclusive token usage, cost, content parts, tool calls, and `invoke_agent` invocations), and `ClaudeSpanMapper` maps model (`llm.invoke`), tool (`tool.invoke`), and agent (`agent.invoke`) spans with `client`/`internal` kinds into `CollectedRecord` DTOs while measuring tool inputs/results and content parts via `ToolContentMeasurer`.

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `ClaudeTurnGroup` | `SessionId` | mapped | `session.native_session_id`, `record_session_link.session_key`, `record_provenance_native_session.native_session_id` | Falls back to transcript file stem when absent. |
| `ClaudeTurnGroup` | `NativeRecordId` | mapped | `record_provenance_native_record.native_record_id`, `record.span_id` | Seeds deterministic record identity (`synth:<session>:<nativeRecordId>`). |
| `ClaudeTurnGroup` | `Timestamp` | mapped | `record.started_at_utc` | ISO-8601 UTC timestamp (`1970-01-01T00:00:00.000Z` fallback when absent). |
| `ClaudeTurnGroup` | `Model` | mapped | `record_model.model_id` | Extracted from assistant message metadata (`unknown` fallback). |
| `ClaudeTurnGroup` | `InputTokens` | mapped | `record_token_count.token_count` | Exclusive input tokens (`token_class = input`). |
| `ClaudeTurnGroup` | `OutputTokens` | mapped | `record_token_count.token_count` | Output tokens (`token_class = output`). |
| `ClaudeTurnGroup` | `CacheReadTokens` | mapped | `record_token_count.token_count` | Cache-read tokens (`token_class = cache_read`). |
| `ClaudeTurnGroup` | `CacheCreationTokens` | mapped | `record_token_count.token_count` | Cache-write tokens (`token_class = cache_write`). |
| `ClaudeTurnGroup` | `Turn` | mapped | nested: ReaderTurn | Carries extracted parts, tool calls, tool results, and turn signals. |
| `ClaudeTurnGroup` | `SessionTitle` | mapped | `session_title.title` | Populated from `customTitle` or `aiTitle` JSONL entries. |
| `ClaudeUsageLine` | `MessageId` | mapped | `record_provenance_native_record.native_record_id` | Propagated to `ClaudeTurnGroup.NativeRecordId`. |
| `ClaudeUsageLine` | `SessionId` | mapped | `session.native_session_id`, `record_session_link.session_key` | Propagated to `ClaudeTurnGroup.SessionId`. |
| `ClaudeUsageLine` | `Model` | mapped | `record_model.model_id` | Propagated to `ClaudeTurnGroup.Model`. |
| `ClaudeUsageLine` | `InputTokens` | mapped | `record_token_count.token_count` | Propagated to `ClaudeTurnGroup.InputTokens` (`token_class = input`). |
| `ClaudeUsageLine` | `OutputTokens` | mapped | `record_token_count.token_count` | Propagated to `ClaudeTurnGroup.OutputTokens` (`token_class = output`). |
| `ClaudeUsageLine` | `CacheReadTokens` | mapped | `record_token_count.token_count` | Propagated to `ClaudeTurnGroup.CacheReadTokens` (`token_class = cache_read`). |
| `ClaudeUsageLine` | `CacheCreationTokens` | mapped | `record_token_count.token_count` | Propagated to `ClaudeTurnGroup.CacheCreationTokens` (`token_class = cache_write`). |
| `ClaudeUsageLine` | `Timestamp` | mapped | `record.started_at_utc` | Propagated to `ClaudeTurnGroup.Timestamp`. |
| `SourceRecordEnvelope` | `HarnessId` | mapped | `harness.harness_id`, `source.harness_id`, `record.harness_id`, `session.harness_id`, `record_provenance.harness_id` | Canonical harness identifier (`claude-code`). |
| `SourceRecordEnvelope` | `SourceKey` | mapped | `source.locator`, `source_checkpoint.source_key`, `record_provenance.source_key` | Transcript file path or unit key. |
| `SourceRecordEnvelope` | `NativeSessionId` | mapped | `session.native_session_id`, `record_provenance_native_session.native_session_id` | Native harness session id. |
| `SourceRecordEnvelope` | `NativeRecordId` | mapped | `record_provenance_native_record.native_record_id` | Native message id within the transcript. |
| `SourceRecordEnvelope` | `SourceRevision` | mapped | `source_checkpoint.revision_token`, `record_provenance.source_revision` | File mtime/size revision fingerprint. |
| `SourceRecordEnvelope` | `Format` | mapped | `source_checkpoint.format` | Parser format identifier (`claude-jsonl`). |
| `SourceRecordEnvelope` | `ParserId` | mapped | `source_checkpoint.parser_id` | Reader identifier (`claude-transcript-reader`). |
| `SourceRecordEnvelope` | `ParserContractVersion` | mapped | `source_checkpoint.parser_contract_version`, `record_provenance.parser_version` | Contract version governing checkpoint invalidation. |
| `SourceRecordEnvelope` | `ImportedAt` | mapped | `record_provenance.imported_at_utc` | UTC import timestamp. |
| `SourceRecordEnvelope` | `LocationToken` | mapped | `record_provenance.location_token` | Optional offset locator inside the transcript unit. |
| `SourceRecordEnvelope` | `Call` | mapped | nested: ParsedProviderCall | Carries model usage, timing, and call-level metadata. |
| `SourceRecordEnvelope` | `ReaderTurn` | mapped | nested: ReaderTurn | Carries transcript parts, tool calls, tool results, and turn signals. |
| `SourceRecordProvenance` | `HarnessId` | mapped | `record_provenance.harness_id` | Attributed harness identifier. |
| `SourceRecordProvenance` | `SourceKey` | mapped | `record_provenance.source_key` | Source unit locator. |
| `SourceRecordProvenance` | `NativeSessionId` | mapped | `record_provenance_native_session.native_session_id` | Native session identifier when present. |
| `SourceRecordProvenance` | `NativeRecordId` | mapped | `record_provenance_native_record.native_record_id` | Native record identifier or fallback digest. |
| `SourceRecordProvenance` | `RecordDigest` | dropped | — | Fallback SHA-256 digest folds directly into `record.span_id` (`synth:<hash>`) when `NativeRecordId` is absent rather than occupying a separate provenance column. |
| `SourceRecordProvenance` | `SourceRevision` | mapped | `record_provenance.source_revision` | Revision fingerprint of the native file. |
| `SourceRecordProvenance` | `ParserContractVersion` | mapped | `record_provenance.parser_version` | Parser contract version stored on provenance. |
| `SourceRecordProvenance` | `ImportedAt` | mapped | `record_provenance.imported_at_utc` | UTC timestamp when imported. |
| `SourceRecordProvenance` | `LocationToken` | mapped | `record_provenance.location_token` | Byte/line offset inside source unit. |
| `SourceRecordProvenance` | `Format` | mapped | `source_checkpoint.format` | Source format recorded at checkpoint level rather than per-record provenance. |
| `SourceRecordProvenance` | `ParserId` | mapped | `source_checkpoint.parser_id` | Parser id recorded at checkpoint level rather than per-record provenance. |
| `ParsedProviderCall` | `Timestamp` | mapped | `record.started_at_utc` | Model call start timestamp. |
| `ParsedProviderCall` | `Provider` | mapped | `record.harness_id` | Normalized via `CanonSynth.HarnessIdFor` when `Envelope.HarnessId` is absent. |
| `ParsedProviderCall` | `Model` | mapped | `record_model.model_id` | Reported model identifier. |
| `ParsedProviderCall` | `InputTokens` | mapped | `record_token_count.token_count` | Exclusive input token count (`token_class = input`). |
| `ParsedProviderCall` | `OutputTokens` | mapped | `record_token_count.token_count` | Output token count (`token_class = output`). |
| `ParsedProviderCall` | `CacheReadInputTokens` | mapped | `record_token_count.token_count` | Authoritative cache-read count (`token_class = cache_read`). |
| `ParsedProviderCall` | `CacheCreationInputTokens` | mapped | `record_token_count.token_count` | Cache-creation count (`token_class = cache_write`). |
| `ParsedProviderCall` | `CachedInputTokens` | mapped | `record_token_count.token_count` | Legacy cache-read fallback (`token_class = cache_read`) used only when `CacheReadInputTokens` is zero. |
| `ParsedProviderCall` | `ReasoningTokens` | mapped | `record_token_count.token_count` | Reasoning token count (`token_class = reasoning`). |
| `ParsedProviderCall` | `CostUsd` | mapped | `record_reported_cost.amount` | Persisted only when harness-reported (`USD` currency); table-estimated costs are computed at read/projection time. |
| `ParsedProviderCall` | `CostIsEstimated` | dropped | — | Estimation status is derived from rate-table lookup vs. presence of a `record_reported_cost` row rather than persisted on `record`. |
| `ParsedProviderCall` | `CostHarnessReported` | dropped | — | Provenance flag gating `record_reported_cost` insertion; absence of a `record_reported_cost` row represents un-reported cost. |
| `ParsedProviderCall` | `SessionId` | mapped | `session.native_session_id`, `record_session_link.session_key` | Session correlation key. |
| `ParsedProviderCall` | `DeduplicationKey` | dropped | — | Transient upstream dedup string superseded by deterministic `record.span_id` and `record_provenance` keys. |
| `ParsedProviderCall` | `TurnId` | mapped | `record_provenance_native_record.native_record_id`, `record.span_id` | Native turn identifier used for span identity and deduplication. |
| `ParsedProviderCall` | `RecordIdentity` | mapped | `record_provenance_native_record.native_record_id`, `record.span_id` | Explicit native record identity overriding `TurnId` when supplied. |
| `ParsedProviderCall` | `Initiator` | dropped | — | Compaction/shutdown rollup tag evaluated during synthesis to determine output measurability; not stored as a relational column. |
| `ParsedProviderCall` | `ActiveDurationMs` | mapped | `record_reported_duration.duration_ms` | Reported active turn duration in milliseconds. |
| `ParsedProviderCall` | `SessionTitle` | mapped | `session_title.title` | Session title extracted from transcript metadata. |
| `ParsedProviderCall` | `Tools` | dropped | — | Legacy un-structured tool-name list (`raw["tools"]`); structured invocations map through `ReaderTurn.ToolCalls` into `tool_call`. |
| `ParsedProviderCall` | `BashCommands` | dropped | — | Legacy raw bash command summary (`raw["bashCommands"]`); superseded by structured `ReaderToolCall` inputs in `tool_call_input_*`. |
| `ParsedProviderCall` | `ToolSequence` | dropped | — | Legacy ordered tool-name array (`raw["toolSequence"]`); call ordering is derived from `record.started_at_utc` across child `tool_call` rows. |
| `ParsedProviderCall` | `WebSearchRequests` | dropped | — | Legacy provider counter (`raw["webSearchRequests"]`) with no normalized column; web search invocations are persisted as `tool_call` spans. |
| `ParsedProviderCall` | `Skills` | mapped | `record_skill_invocation.skill_name` | Invoked skill identifiers. |
| `ParsedProviderCall` | `SkillsListed` | dropped | — | Initial skill catalog listing (`kyber.skills.listed`) is omitted from `RelationalSchemaCatalog`; only invoked skills persist in `record_skill_invocation`. |
| `ListedSkillsPayload` | `IsInitial` | dropped | — | Skill catalog listing metadata (`kyber.skills.listed`) is omitted from `RelationalSchemaCatalog`. |
| `ListedSkillsPayload` | `Skills` | dropped | — | Uninvoked skill catalog entries (`kyber.skills.listed`) are omitted from `RelationalSchemaCatalog`. |
| `ListedSkillItem` | `Name` | dropped | — | Skill catalog item name (`kyber.skills.listed`) is omitted from `RelationalSchemaCatalog`; invoked skills map via `ParsedProviderCall.Skills`. |
| `ListedSkillItem` | `Description` | dropped | — | Skill catalog description (`kyber.skills.listed`) is omitted from `RelationalSchemaCatalog`. |
| `ReaderTurn` | `Parts` | mapped | nested: ContentPart | Ordered turn content parts (`content_part.*`). |
| `ReaderTurn` | `SessionId` | mapped | `session.native_session_id`, `record_session_link.session_key` | Session identifier discovered on turn lines. |
| `ReaderTurn` | `ContextWindow` | dropped | — | Runtime-reported model context window is resolved via model catalog onto `session_context.context_limit` during projection rather than stored per turn. |
| `ReaderTurn` | `DeclaredContextWindow` | mapped | `record_declared_context_window.declared_context_window` | Explicit harness-declared context limit. |
| `ReaderTurn` | `TerminationReason` | mapped | `record.status` | Normalized into `record.status` (`ok`, `error`, `unset`, `unspecified`). |
| `ReaderTurn` | `ExitCode` | mapped | `record.status` | Non-zero turn exit code maps `record.status` to `error`. |
| `ReaderTurn` | `IsCorrection` | mapped | `record_correction.span_id` | Emits a `record_correction` presence row when true. |
| `ReaderTurn` | `CorrectionRule` | mapped | `record_correction_rule.correction_rule` | Identifier of the matched user-correction rule. |
| `ReaderTurn` | `NativeRecordId` | mapped | `record_provenance_native_record.native_record_id` | Native message identifier. |
| `ReaderTurn` | `Timestamp` | mapped | `record.started_at_utc` | Turn UTC timestamp when present. |
| `ReaderTurn` | `ToolCalls` | mapped | nested: ReaderToolCall | Child `tool.invoke` call records. |
| `ReaderTurn` | `ToolResults` | mapped | nested: ReaderToolResult | Tool execution outcomes and result metrics paired to `ToolCalls`. |
| `ReaderTurn` | `ToolsOffered` | dropped | — | Bare offered tool name strings lack schema token counts and MCP server attribution required by `tool_definition`. |
| `ReaderToolCall` | `Id` | mapped | `tool_call.call_id` | Tool call correlation identifier. |
| `ReaderToolCall` | `Name` | mapped | `tool_call.tool_name` | Invoked tool name. |
| `ReaderToolCall` | `Arguments` | mapped | `tool_call_input_size.utf8_bytes`, `tool_call_input_size.characters`, `tool_call_input_fingerprint.sha256`, `tool_call_input_text.text` | Measured via `ToolContentMeasurer`; JSON object/array plain text is withheld (`tool_call_input_text` omitted). |
| `ReaderToolCall` | `RawArgumentsString` | mapped | `tool_call_input_size.utf8_bytes`, `tool_call_input_size.characters`, `tool_call_input_fingerprint.sha256`, `tool_call_input_text.text`, `tool_call_input_truncation.original_utf8_bytes` | Measured via `ToolContentMeasurer` with 64 KiB plain-text cap. |
| `ReaderToolCall` | `Timestamp` | mapped | `record.started_at_utc` | Timestamp on the synthesized child `tool.invoke` span. |
| `ReaderToolCall` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Reported tool duration in milliseconds. |
| `ReaderToolResult` | `ToolCallId` | mapped | `tool_call.call_id` | Pairs result to its `ReaderToolCall`. |
| `ReaderToolResult` | `Name` | mapped | `tool_call.tool_name` | Corroborates or supplies tool name when missing on call. |
| `ReaderToolResult` | `Content` | mapped | `tool_call_result_size.utf8_bytes`, `tool_call_result_size.characters`, `tool_call_result_text.text`, `tool_call_result_truncation.original_utf8_bytes`, `content_part_text.part_text` | Measured via `ToolContentMeasurer`; JSON object/array plain text is withheld. |
| `ReaderToolResult` | `IsError` | mapped | `tool_call_outcome.succeeded`, `record.status` | `false` yields `succeeded = 1` and `ok`; `true` yields `succeeded = 0` and `error`. |
| `ReaderToolResult` | `Timestamp` | mapped | `record_end.ended_at_utc`, `record_reported_duration.duration_ms` | Derives child span end time and duration when paired with call timestamp. |
| `ContentPart` | `Part` | mapped | `content_part.bucket` | Maps to canonical `ContentBucket` enum literal. |
| `ContentPart` | `Text` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `content_part_withheld.characters`, `content_part_withheld.measured_tokens` | Classified via `ToolContentMeasurer`; JSON object/array payloads populate `content_part_withheld` instead of `content_part_text`. |
| `ContentPart` | `Tokens` | mapped | `content_part_reported_tokens.token_count` | Harness-reported token count for the part. |
| `ContentPart` | `Server` | mapped | `content_part_server.server_name` | Ground-truth MCP server attribution. |
| `ContentPart` | `Role` | mapped | `content_part_role.role_name` | Message role (`system`, `user`, `assistant`, `tool`). |
| `ContentPart` | `Order` | mapped | `content_part.position` | Zero-based part ordinal within the turn. |
| `ContentPart` | `Resident` | mapped | `content_part_residency.is_resident` | Context residency indicator (`0` or `1`). |
| `ContentPart` | `Truncated` | dropped | — | Boolean truncation flag is replaced by the exact original length in `content_part_truncation.original_utf8_bytes`. |
| `RefreshSourceInput` | `HarnessId` | mapped | `source_checkpoint.harness_id`, `source.harness_id` | Harness owning the discovered source unit. |
| `RefreshSourceInput` | `SourceKey` | mapped | `source_checkpoint.source_key`, `source.locator` | Stable unit path or locator. |
| `RefreshSourceInput` | `ProviderId` | mapped | `source_checkpoint.provider_id` | Upstream provider identifier (`claude`). |
| `RefreshSourceInput` | `ParserId` | mapped | `source_checkpoint.parser_id` | Reader identifier (`claude-transcript-reader`). |
| `RefreshSourceInput` | `ParserContractVersion` | mapped | `source_checkpoint.parser_contract_version` | Contract version for checkpoint validity. |
| `RefreshSourceInput` | `Format` | mapped | `source_checkpoint.format` | Source format (`claude-jsonl`). |
| `RefreshSourceInput` | `SourceRootLabel` | mapped | `source_checkpoint.source_root_label` | Non-PII logical root label (`~/.claude/projects`). |
| `RefreshSourceInput` | `RevisionToken` | mapped | `source_checkpoint.revision_token` | Source revision token (`mtime:size`). |
| `RefreshSourceInput` | `LoadEnvelopes` | dropped | — | Deferred in-memory reader delegate (`Func`) invoked during refresh; not persistable data. |
| `OtlpSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace correlation identifier. |
| `OtlpSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span key (or quarantined span key). |
| `OtlpSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier for child spans. |
| `OtlpSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `OtlpSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind (`unspecified`, `internal`, `server`, `client`, `producer`, `consumer`). |
| `OtlpSpan` | `StartTimeUnixNano` | mapped | `record.started_at_utc` | Converted to ISO-8601 UTC `Timestamp`; raw nanosecond string is not persisted separately. |
| `OtlpSpan` | `EndTimeUnixNano` | mapped | `record_end.ended_at_utc` | End instant used for duration calculation and `record_end`. |
| `OtlpSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | ISO-8601 UTC start timestamp. |
| `OtlpSpan` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Rounded to non-negative integer milliseconds. |
| `OtlpSpan` | `Status` | mapped | nested: OtlpStatus | Normalized span status and error message. |
| `OtlpSpan` | `Attributes` | mapped | nested: RawSpan | Routed to `RawSpan.Attributes` and `OtlpSpanNative`; `ClaudeCodeAdapter` and `ClaudeSpanMapper` extract `gen_ai.*`/`claude*` usage, session, model, cost, tool calls, agent invocations, and content parts while unmapped/PII keys are dropped. |
| `OtlpSpan` | `Resource` | mapped | `source.locator`, `quarantined_span_source.source` | `service.name` populates `source.locator`; remaining resource attributes are dropped after attribution. |
| `OtlpSpan` | `Scope` | mapped | nested: OtlpScope | Decoded instrumentation scope; dropped from relational persistence. |
| `OtlpStatus` | `Code` | mapped | `record.status` | Normalized lowercase status (`unset`, `ok`, `error`). |
| `OtlpStatus` | `Message` | mapped | `record_error.message` | Persisted when status indicates an error. |
| `OtlpScope` | `Name` | dropped | — | Instrumentation scope name has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpScope` | `Version` | dropped | — | Instrumentation scope version has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpLog` | `LogId` | dropped | — | Legacy `pending_logs`/`quarantined_logs`/`enriched_logs` tables were removed per DD-10 (`LegacyTableReplacementMap`); no raw log id is persisted. |
| `OtlpLog` | `TraceId` | mapped | `record_trace.trace_id`, `ingest_problem_span.span_id` | Trace correlation when mapped to collector DTOs or diagnostics; raw log table removed per DD-10. |
| `OtlpLog` | `SpanId` | mapped | `record.span_id`, `ingest_problem_span.span_id` | Span correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SessionId` | mapped | `record_session_link.session_key`, `ingest_problem_session.session_id` | Session correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `Timestamp` | mapped | `record.started_at_utc`, `ingest_problem_timestamp.timestamp_utc` | Log event timestamp mapped on collector DTOs or diagnostics. |
| `OtlpLog` | `Body` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `ingest_problem.message` | Mapped on receipt into typed collector parts or `ingest_problem`; raw payload is discarded per DD-10. |
| `OtlpLog` | `Attributes` | dropped | — | Unstructured OTLP log `attributes_json` is dropped per DD-10 (`LegacyTableReplacementMap`); only typed fields are mapped. |
| `OtlpLog` | `Resource` | mapped | `source.locator` | `service.name` identifies source; remaining `resource_json` is dropped per DD-10. |
| `OtlpLog` | `Scope` | dropped | — | OTLP log instrumentation scope is not persisted in `RelationalSchemaCatalog` per DD-10. |
| `OtlpMetric` | `Name` | mapped | `metric_point.name` | Metric instrument name. |
| `OtlpMetric` | `Description` | dropped | — | Human-readable metric description has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `Unit` | mapped | `metric_point_unit.unit` | Metric unit string when present. |
| `OtlpMetric` | `Kind` | mapped | `metric_point.kind` | Metric kind (`gauge`, `sum`, `histogram`, etc.). |
| `OtlpMetric` | `AggregationTemporality` | mapped | `metric_point_temporality.aggregation_temporality` | OTLP temporality enum integer. |
| `OtlpMetric` | `IsMonotonic` | dropped | — | Sum monotonicity flag has no column in `metric_point` or its child tables in `RelationalSchemaCatalog`. |
| `OtlpMetric` | `DataPoints` | mapped | nested: OtlpMetricDataPoint | Normalized data points persisted under `metric_point.*`. |
| `OtlpMetric` | `Resource` | mapped | `metric_point_source.source` | `service.name` populates `metric_point_source.source`; `resource_json` was removed per `LegacyTableReplacementMap`. |
| `OtlpMetric` | `Scope` | dropped | — | `scope_json` on `metric_point` was removed per `LegacyTableReplacementMap` because it was written and never read. |
| `OtlpMetricDataPoint` | `Attributes` | dropped | — | `attributes_json` on `metric_point` was removed per `LegacyTableReplacementMap`; attributes fold into `metric_point.point_key`. |
| `OtlpMetricDataPoint` | `TimeUnixNano` | mapped | `metric_point_time.time_unix_nano`, `metric_point_timestamp.timestamp_utc` | Observation timestamp in nanoseconds and UTC ISO string. |
| `OtlpMetricDataPoint` | `StartTimeUnixNano` | mapped | `metric_point_start_time.start_time_unix_nano` | Cumulative window start timestamp in nanoseconds. |
| `OtlpMetricDataPoint` | `Value` | mapped | `metric_point_value.value` | Gauge/sum scalar value. |
| `OtlpMetricDataPoint` | `Count` | mapped | `metric_point_count.point_count` | Histogram observation count. |
| `OtlpMetricDataPoint` | `Sum` | mapped | `metric_point_sum.point_sum` | Histogram observation sum. |
| `RawSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span identifier. |
| `RawSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace identifier when present. |
| `RawSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier when present. |
| `RawSpan` | `Source` | mapped | `source.locator`, `quarantined_span_source.source` | Emitting `service.name` (or `otlp` fallback). |
| `RawSpan` | `Attributes` | mapped | `record.op`, `record_model.model_id`, `record_session_link.session_key`, `record_token_count.token_count`, `record_reported_total.token_count`, `record_reported_cost.amount`, `record_reported_cost.currency`, `record_skill_invocation.skill_name`, `record_agent_instance.agent_instance_id`, `record_declared_context_window.declared_context_window`, `record_edit_failure.failure_count`, `record_correction_rule.correction_rule`, `record_error.message`, `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `tool_call.call_id`, `tool_call.tool_name`, `tool_call_mcp.server_name`, `tool_call_mcp.server_tool_name`, `tool_call_input_size.utf8_bytes`, `tool_call_input_fingerprint.sha256`, `tool_call_input_text.text`, `tool_call_result_size.utf8_bytes`, `tool_call_result_text.text`, `tool_call_outcome.succeeded`, `tool_call_error.message`, `tool_call_error_code.code`, `tool_call_exit_code.exit_code`, `agent_invocation.agent_name`, `agent_invocation_spawn.spawning_record_id`, `agent_invocation_outcome.succeeded`, `agent_invocation_error.message` | `ClaudeCodeAdapter` and `ClaudeSpanMapper` (via `OtlpSpanNative`) extract `gen_ai.*`/`claude*` exclusive token usage, reported totals/cost, session, model, parts (withholding JSON payloads via `ToolContentMeasurer`), tool calls (`tool.invoke`), and `invoke_agent` (`agent.invoke`) fields; unmapped/PII keys are dropped without storing `raw`. |
| `RawSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `RawSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `RawSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | Optional ISO start timestamp. |

## codex

Codex ingests from two paths: the local rollout JSONL transcript reader (`TranscriptReaderRepository.ReadCodexTurns` / `ReadCodexEnvelopes` producing `SourceRecordEnvelope`, `ParsedProviderCall`, `ReaderTurn`, and `ContentPart`, plus `CodexRolloutMapper` over `CodexRolloutDocument`, `CodexRolloutTurn`, `CodexRolloutToolCall`, `CodexRolloutToolResult`, `CodexRolloutMessage`, and `CodexRolloutUsage` mapping directly to `CollectedBatch`) and the OTLP collector/adapter (`CodexAdapter` and `CodexSpanMapper` via `OtlpSpanNative` over `OtlpSpan`, `OtlpLog`, `OtlpMetric`, and `RawSpan`).

For OTLP span mapping into static collector DTOs, `CodexSpanMapper` filters `OtlpSpan` attributes to the `CodexAdapter` allowlist (`model`, `slug`, `conversation.id`, `gen_ai.session.id`, `session.id`, `event.timestamp`, `gen_ai.usage.*`, `codex.usage.*`) before `OtlpSpanNative` extraction, maps usage-bearing spans to `llm.invoke` `CollectedRecord`s with the cache-inclusive token convention (`gen_ai.usage.cache_write.input_tokens` and `codex.usage.reasoning_output_tokens`), prefers `conversation.id` for the session key, and drops prompts, tool payloads, `user.email`, and account identifiers without storing `raw`.

For rollout tool invocation pairing, `CodexRolloutMapper` correlates `function_call`, `custom_tool_call`, and `local_shell_call` items with `function_call_output`, `custom_tool_call_output`, and `local_shell_call_output` items by `call_id` (falling back to `id`) into child `tool.invoke` `CollectedRecord`s, measuring all payloads via `ToolContentMeasurer` so JSON `arguments`/`input`/`action` retain only byte/character measurements and canonical SHA-256 fingerprints while plain-text `custom_tool_call.input` and `local_shell_call_output.output` payloads are preserved (bounded to 64 KiB). Per OpenAI's [Local shell](https://developers.openai.com/api/docs/guides/tools-local-shell) reference (DD-17a), `local_shell_call` maps with `tool_name = local_shell`, retains only size and SHA-256 fingerprint for `action` (`PlainText` withheld), and maps `status` to `tool_call_outcome` (`completed` -> `succeeded = 1`, `incomplete` -> `succeeded = 0`, `in_progress` -> absent row).

For OTLP tool-result log mapping, `CodexToolResultMapper` maps Codex `codex.tool_result` log events to `tool.invoke` `CollectedRecord`s. Attribute names follow the authoritative `openai/codex` telemetry source ([`codex-rs/otel/src/tool_result.rs`](https://github.com/openai/codex/blob/main/codex-rs/otel/src/tool_result.rs), documented in [`codex-rs/otel/README.md`](https://github.com/openai/codex/blob/main/codex-rs/otel/README.md)) (DD-17b): `tool_name` maps to `tool_call.tool_name` and `record.name`, `call_id` maps to `tool_call.call_id`, `success` maps to `tool_call_outcome.succeeded` and `record.status` (`true` -> `ok`, `false` -> `error`, absent -> `unset`), and `duration_ms` maps to `record_reported_duration.duration_ms` (with `record_end.ended_at_utc` derived from the log timestamp plus the duration). The session key prefers the decoded log session id, falling back to the `conversation.id` event attribute, and `model` maps to `record_model.model_id`. The log-only `arguments` (a JSON document) and `output` excerpt (tool-specific text) are dropped without measurement or fingerprint per DD-3 and OD-6, so the tool record carries no `tool_call_input_*`, `tool_call_result_*`, or `content_part` rows; every other event attribute (`tool_namespace`, `tool_result_seq`, `mcp_server`, `mcp_server_origin`, `agent_name`, `product_sku`, `output_truncated`, length/line counters, `tool_origin`, `mcp_tool`, and session/account metadata) is dropped because it is not part of the canonical `tool.invoke` shape, and identity attributes are already stripped at OTLP decode. A `codex.tool_result` event missing `tool_name` or `call_id`, or carrying an unparseable timestamp, reports a payload-free `ingest_problem` (`CODEX_TOOL_RESULT_INVALID`, or `OTLP_LOG_INVALID_TIMESTAMP`) instead of a record.

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `SourceRecordEnvelope` | `HarnessId` | mapped | `harness.harness_id`, `source.harness_id`, `record.harness_id`, `session.harness_id`, `record_provenance.harness_id` | Canonical harness identifier (`codex-cli` / `codex`). |
| `SourceRecordEnvelope` | `SourceKey` | mapped | `source.locator`, `source_checkpoint.source_key`, `record_provenance.source_key` | Rollout JSONL file path. |
| `SourceRecordEnvelope` | `NativeSessionId` | mapped | `session.native_session_id`, `record_provenance_native_session.native_session_id` | Native Codex session id from `session_meta`. |
| `SourceRecordEnvelope` | `NativeRecordId` | mapped | `record_provenance_native_record.native_record_id` | Native turn identifier when supplied. |
| `SourceRecordEnvelope` | `SourceRevision` | mapped | `source_checkpoint.revision_token`, `record_provenance.source_revision` | File mtime/size revision fingerprint. |
| `SourceRecordEnvelope` | `Format` | mapped | `source_checkpoint.format` | Parser format identifier (`codex-jsonl`). |
| `SourceRecordEnvelope` | `ParserId` | mapped | `source_checkpoint.parser_id` | Reader identifier (`codex-transcript-reader`). |
| `SourceRecordEnvelope` | `ParserContractVersion` | mapped | `source_checkpoint.parser_contract_version`, `record_provenance.parser_version` | Contract version governing checkpoint invalidation. |
| `SourceRecordEnvelope` | `ImportedAt` | mapped | `record_provenance.imported_at_utc` | UTC import timestamp. |
| `SourceRecordEnvelope` | `LocationToken` | mapped | `record_provenance.location_token` | Optional offset locator inside rollout file. |
| `SourceRecordEnvelope` | `Call` | mapped | nested: ParsedProviderCall | Carries `token_count` metrics and model identity. |
| `SourceRecordEnvelope` | `ReaderTurn` | mapped | nested: ReaderTurn | Carries `system_prompt`, `instruction_context`, `conversation_history`, `tool_result_content`, and tool calls. |
| `SourceRecordProvenance` | `HarnessId` | mapped | `record_provenance.harness_id` | Attributed harness identifier. |
| `SourceRecordProvenance` | `SourceKey` | mapped | `record_provenance.source_key` | Rollout file path. |
| `SourceRecordProvenance` | `NativeSessionId` | mapped | `record_provenance_native_session.native_session_id` | Native session identifier. |
| `SourceRecordProvenance` | `NativeRecordId` | mapped | `record_provenance_native_record.native_record_id` | Native record identifier or fallback digest. |
| `SourceRecordProvenance` | `RecordDigest` | dropped | — | Deterministic SHA-256 fallback digest folds into `record.span_id` (`synth:<hash>`) when `NativeRecordId` is absent. |
| `SourceRecordProvenance` | `SourceRevision` | mapped | `record_provenance.source_revision` | Revision fingerprint of the native file. |
| `SourceRecordProvenance` | `ParserContractVersion` | mapped | `record_provenance.parser_version` | Parser contract version stored on provenance. |
| `SourceRecordProvenance` | `ImportedAt` | mapped | `record_provenance.imported_at_utc` | UTC import timestamp. |
| `SourceRecordProvenance` | `LocationToken` | mapped | `record_provenance.location_token` | Byte/line offset inside source unit. |
| `SourceRecordProvenance` | `Format` | mapped | `source_checkpoint.format` | Source format recorded at checkpoint level rather than per-record provenance. |
| `SourceRecordProvenance` | `ParserId` | mapped | `source_checkpoint.parser_id` | Parser id recorded at checkpoint level rather than per-record provenance. |
| `ParsedProviderCall` | `Timestamp` | mapped | `record.started_at_utc` | Turn `token_count` event timestamp. |
| `ParsedProviderCall` | `Provider` | mapped | `record.harness_id` | Normalized via `CanonSynth.HarnessIdFor` (`codex`). |
| `ParsedProviderCall` | `Model` | mapped | `record_model.model_id` | Model id from `session_meta` or `token_count.info.model`. |
| `ParsedProviderCall` | `InputTokens` | mapped | `record_token_count.token_count` | Exclusive uncached input (`input_tokens - cached_input_tokens`, `token_class = input`). |
| `ParsedProviderCall` | `OutputTokens` | mapped | `record_token_count.token_count` | Output tokens (`token_class = output`). |
| `ParsedProviderCall` | `CacheReadInputTokens` | mapped | `record_token_count.token_count` | Cached input tokens (`token_class = cache_read`). |
| `ParsedProviderCall` | `CacheCreationInputTokens` | mapped | `record_token_count.token_count` | Cache-write tokens (`token_class = cache_write`). |
| `ParsedProviderCall` | `CachedInputTokens` | mapped | `record_token_count.token_count` | Legacy cache-read fallback (`token_class = cache_read`). |
| `ParsedProviderCall` | `ReasoningTokens` | mapped | `record_token_count.token_count` | Reasoning output tokens (`token_class = reasoning`). |
| `ParsedProviderCall` | `CostUsd` | mapped | `record_reported_cost.amount` | Persisted only when harness-reported (`USD` currency); Codex file costs are estimated from published rates at read time. |
| `ParsedProviderCall` | `CostIsEstimated` | dropped | — | Estimation status is derived from rate-table lookup vs. presence of a `record_reported_cost` row rather than persisted on `record`. |
| `ParsedProviderCall` | `CostHarnessReported` | dropped | — | Provenance flag gating `record_reported_cost` insertion; absence of a `record_reported_cost` row represents un-reported cost. |
| `ParsedProviderCall` | `SessionId` | mapped | `session.native_session_id`, `record_session_link.session_key` | Session correlation key. |
| `ParsedProviderCall` | `DeduplicationKey` | dropped | — | Transient upstream dedup string superseded by deterministic `record.span_id` and `record_provenance` keys. |
| `ParsedProviderCall` | `TurnId` | mapped | `record_provenance_native_record.native_record_id`, `record.span_id` | Native turn identifier used for span identity and deduplication. |
| `ParsedProviderCall` | `RecordIdentity` | mapped | `record_provenance_native_record.native_record_id`, `record.span_id` | Explicit native record identity overriding `TurnId`. |
| `ParsedProviderCall` | `Initiator` | dropped | — | Rollup discriminator evaluated during synthesis; not stored as a relational column. |
| `ParsedProviderCall` | `ActiveDurationMs` | mapped | `record_reported_duration.duration_ms` | Reported active turn duration in milliseconds. |
| `ParsedProviderCall` | `SessionTitle` | mapped | `session_title.title` | Session title when supplied. |
| `ParsedProviderCall` | `Tools` | dropped | — | Legacy un-structured tool-name summary (`raw["tools"]`); structured tool calls map through `ReaderTurn.ToolCalls` into `tool_call`. |
| `ParsedProviderCall` | `BashCommands` | dropped | — | Legacy raw bash command preview list (`raw["bashCommands"]`); superseded by `tool_call_input_*`. |
| `ParsedProviderCall` | `ToolSequence` | dropped | — | Legacy ordered tool-name array (`raw["toolSequence"]`); call ordering derives from `record.started_at_utc` across child `tool_call` spans. |
| `ParsedProviderCall` | `WebSearchRequests` | dropped | — | Legacy provider counter (`raw["webSearchRequests"]`) with no normalized column; web search calls persist as `tool_call` spans. |
| `ParsedProviderCall` | `Skills` | mapped | `record_skill_invocation.skill_name` | Invoked skill identifiers. |
| `ParsedProviderCall` | `SkillsListed` | dropped | — | Initial skill listing (`kyber.skills.listed`) is omitted from `RelationalSchemaCatalog`; only invoked skills persist in `record_skill_invocation`. |
| `ListedSkillsPayload` | `IsInitial` | dropped | — | Skill catalog listing metadata (`kyber.skills.listed`) is omitted from `RelationalSchemaCatalog`. |
| `ListedSkillsPayload` | `Skills` | dropped | — | Uninvoked skill catalog entries (`kyber.skills.listed`) are omitted from `RelationalSchemaCatalog`. |
| `ListedSkillItem` | `Name` | dropped | — | Skill catalog item name (`kyber.skills.listed`) is omitted from `RelationalSchemaCatalog`. |
| `ListedSkillItem` | `Description` | dropped | — | Skill catalog description (`kyber.skills.listed`) is omitted from `RelationalSchemaCatalog`. |
| `ReaderTurn` | `Parts` | mapped | nested: ContentPart | Snapshot of `system_prompt`, `instruction_context` (`agents_md`), `conversation_history`, and `tool_result_content`. |
| `ReaderTurn` | `SessionId` | mapped | `session.native_session_id`, `record_session_link.session_key` | Session id from `session_meta`. |
| `ReaderTurn` | `ContextWindow` | dropped | — | Rollout `model_context_window` is resolved via `CanonPricing.ResolveContextWindow` onto `session_context.context_limit` during projection rather than stored per turn. |
| `ReaderTurn` | `DeclaredContextWindow` | mapped | `record_declared_context_window.declared_context_window` | Explicit declared context limit when supplied. |
| `ReaderTurn` | `TerminationReason` | mapped | `record.status` | Maps `task_complete` to `ok` and failure/abort events to `error`/`unspecified`. |
| `ReaderTurn` | `ExitCode` | mapped | `record.status` | Non-zero exit code from `task_failed`/`error` maps `record.status` to `error`. |
| `ReaderTurn` | `IsCorrection` | mapped | `record_correction.span_id` | Emits a `record_correction` presence row when true. |
| `ReaderTurn` | `CorrectionRule` | mapped | `record_correction_rule.correction_rule` | Identifier of the matched user-correction rule. |
| `ReaderTurn` | `NativeRecordId` | mapped | `record_provenance_native_record.native_record_id` | Native turn identifier when present. |
| `ReaderTurn` | `Timestamp` | mapped | `record.started_at_utc` | Turn UTC timestamp when present. |
| `ReaderTurn` | `ToolCalls` | mapped | nested: ReaderToolCall | Synthesized from `function_call`, `custom_tool_call`, and `local_shell_call` (OpenAI Local shell reference, DD-17a) items. |
| `ReaderTurn` | `ToolResults` | mapped | nested: ReaderToolResult | Synthesized from `function_call_output`, `custom_tool_call_output`, and `local_shell_call_output` (OpenAI Local shell reference, DD-17a) items. |
| `ReaderTurn` | `ToolsOffered` | dropped | — | Bare offered tool names lack schema token counts and MCP server attribution required by `tool_definition`. |
| `ReaderToolCall` | `Id` | mapped | `tool_call.call_id` | Extracted from `call_id` with `id` fallback across `function_call`, `custom_tool_call`, and `local_shell_call` (`CodexRolloutToolCall.CallId`). |
| `ReaderToolCall` | `Name` | mapped | `tool_call.tool_name` | Tool name from `function_call`/`custom_tool_call`, or `local_shell` for `local_shell_call` per OpenAI Local shell reference (`CodexRolloutToolCall.ToolName`). |
| `ReaderToolCall` | `Arguments` | mapped | `tool_call_input_size.utf8_bytes`, `tool_call_input_size.characters`, `tool_call_input_fingerprint.sha256`, `tool_call_input_text.text` | Measured via `ToolContentMeasurer`; JSON `function_call.arguments` and `local_shell_call.action` (OpenAI Local shell reference) retain measurements and SHA-256 fingerprint only (`PlainText` withheld). |
| `ReaderToolCall` | `RawArgumentsString` | mapped | `tool_call_input_size.utf8_bytes`, `tool_call_input_size.characters`, `tool_call_input_fingerprint.sha256`, `tool_call_input_text.text`, `tool_call_input_truncation.original_utf8_bytes` | Measured via `ToolContentMeasurer` from `function_call.arguments` (JSON withheld), `local_shell_call.action` (size and SHA-256 fingerprint only, `PlainText` withheld), or `custom_tool_call.input` (plain-text preserved up to 64 KiB). |
| `ReaderToolCall` | `Timestamp` | mapped | `record.started_at_utc` | Rollout entry timestamp on child `tool.invoke` span. |
| `ReaderToolCall` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Reported tool duration in milliseconds. |
| `ReaderToolResult` | `ToolCallId` | mapped | `tool_call.call_id` | Pairs result to its `ReaderToolCall` (including `local_shell_call_output` paired with `local_shell_call`). |
| `ReaderToolResult` | `Name` | mapped | `tool_call.tool_name` | Corroborates tool name when present. |
| `ReaderToolResult` | `Content` | mapped | `tool_call_result_size.utf8_bytes`, `tool_call_result_size.characters`, `tool_call_result_text.text`, `tool_call_result_truncation.original_utf8_bytes`, `content_part_text.part_text` | Measured via `ToolContentMeasurer`; plain `local_shell_call_output.output` (OpenAI Local shell reference) is kept as `tool_call_result_text.text` while JSON object/array plain text is withheld. |
| `ReaderToolResult` | `IsError` | mapped | `tool_call_outcome.succeeded`, `record.status` | Maps tool result outcome and `local_shell_call.status` (`completed` -> `succeeded = 1`, `incomplete` -> `succeeded = 0`, `in_progress` -> absent outcome row; DD-17a) to `succeeded` (`0`/`1`) and `record.status`. |
| `ReaderToolResult` | `Timestamp` | mapped | `record_end.ended_at_utc`, `record_reported_duration.duration_ms` | Computes child span end time and duration when paired with call timestamp. |
| `ContentPart` | `Part` | mapped | `content_part.bucket` | Maps to canonical `ContentBucket` enum literal. |
| `ContentPart` | `Text` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `content_part_withheld.characters`, `content_part_withheld.measured_tokens` | Classified via `ToolContentMeasurer`; JSON object/array payloads populate `content_part_withheld`. |
| `ContentPart` | `Tokens` | mapped | `content_part_reported_tokens.token_count` | Harness-reported token count for the part. |
| `ContentPart` | `Server` | mapped | `content_part_server.server_name` | Ground-truth MCP server attribution. |
| `ContentPart` | `Role` | mapped | `content_part_role.role_name` | Message role. |
| `ContentPart` | `Order` | mapped | `content_part.position` | Zero-based part ordinal within turn snapshot. |
| `ContentPart` | `Resident` | mapped | `content_part_residency.is_resident` | Context residency indicator (`0` or `1`). |
| `ContentPart` | `Truncated` | dropped | — | Boolean truncation flag is replaced by the measured original byte length in `content_part_truncation.original_utf8_bytes`. |
| `RefreshSourceInput` | `HarnessId` | mapped | `source_checkpoint.harness_id`, `source.harness_id` | Harness owning the discovered rollout (`codex-cli`). |
| `RefreshSourceInput` | `SourceKey` | mapped | `source_checkpoint.source_key`, `source.locator` | Rollout JSONL path. |
| `RefreshSourceInput` | `ProviderId` | mapped | `source_checkpoint.provider_id` | Upstream provider identifier (`codex`). |
| `RefreshSourceInput` | `ParserId` | mapped | `source_checkpoint.parser_id` | Reader identifier (`codex-transcript-reader`). |
| `RefreshSourceInput` | `ParserContractVersion` | mapped | `source_checkpoint.parser_contract_version` | Contract version for checkpoint validity. |
| `RefreshSourceInput` | `Format` | mapped | `source_checkpoint.format` | Source format (`codex-jsonl`). |
| `RefreshSourceInput` | `SourceRootLabel` | mapped | `source_checkpoint.source_root_label` | Non-PII logical root label (`~/.codex/sessions`). |
| `RefreshSourceInput` | `RevisionToken` | mapped | `source_checkpoint.revision_token` | Source revision token (`mtime:size`). |
| `RefreshSourceInput` | `LoadEnvelopes` | dropped | — | Deferred in-memory reader delegate (`Func`) invoked during refresh; not persistable data. |
| `OtlpSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace correlation identifier. |
| `OtlpSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span key (or quarantined span key). |
| `OtlpSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier. |
| `OtlpSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `OtlpSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `OtlpSpan` | `StartTimeUnixNano` | mapped | `record.started_at_utc` | Converted to ISO-8601 UTC `Timestamp`; raw nanosecond string is not persisted separately. |
| `OtlpSpan` | `EndTimeUnixNano` | mapped | `record_end.ended_at_utc` | End instant used for duration calculation and `record_end`. |
| `OtlpSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | ISO-8601 UTC start timestamp. |
| `OtlpSpan` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Rounded to non-negative integer milliseconds. |
| `OtlpSpan` | `Status` | mapped | nested: OtlpStatus | Normalized span status and error message. |
| `OtlpSpan` | `Attributes` | mapped | nested: RawSpan | `CodexAdapter` and `CodexSpanMapper` (via `OtlpSpanNative`) allowlist only `model`, `slug`, `conversation.id`, `gen_ai.session.id`, `session.id`, `event.timestamp`, `gen_ai.usage.*`, and `codex.usage.*`; prompts, tool args/results, `user.email`, and account ids are dropped without storing `raw`. |
| `OtlpSpan` | `Resource` | mapped | `source.locator`, `quarantined_span_source.source` | `service.name` populates `source.locator`; remaining resource attributes are dropped after attribution. |
| `OtlpSpan` | `Scope` | mapped | nested: OtlpScope | Decoded instrumentation scope; dropped from relational persistence. |
| `OtlpStatus` | `Code` | mapped | `record.status` | Normalized lowercase status (`unset`, `ok`, `error`). |
| `OtlpStatus` | `Message` | mapped | `record_error.message` | Persisted when status indicates an error. |
| `OtlpScope` | `Name` | dropped | — | Instrumentation scope name has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpScope` | `Version` | dropped | — | Instrumentation scope version has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpLog` | `LogId` | dropped | — | Legacy `pending_logs`/`quarantined_logs`/`enriched_logs` tables were removed per DD-10 (`LegacyTableReplacementMap`); no raw log id is persisted. |
| `OtlpLog` | `TraceId` | mapped | `record_trace.trace_id`, `ingest_problem_span.span_id` | Trace correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SpanId` | mapped | `record.span_id`, `ingest_problem_span.span_id` | Span correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SessionId` | mapped | `record_session_link.session_key`, `ingest_problem_session.session_id` | Session correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `Timestamp` | mapped | `record.started_at_utc`, `ingest_problem_timestamp.timestamp_utc` | Log event timestamp mapped on collector DTOs or diagnostics. |
| `OtlpLog` | `Body` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `ingest_problem.message` | Mapped on receipt into typed collector parts or `ingest_problem`; raw payload is discarded per DD-10. |
| `OtlpLog` | `Attributes` | dropped | — | Unstructured OTLP log `attributes_json` is dropped per DD-10 (`LegacyTableReplacementMap`); only typed fields are mapped. |
| `OtlpLog` | `Resource` | mapped | `source.locator` | `service.name` identifies source; remaining `resource_json` is dropped per DD-10. |
| `OtlpLog` | `Scope` | dropped | — | OTLP log instrumentation scope is not persisted in `RelationalSchemaCatalog` per DD-10. |
| `OtlpMetric` | `Name` | mapped | `metric_point.name` | Metric instrument name. |
| `OtlpMetric` | `Description` | dropped | — | Human-readable metric description has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `Unit` | mapped | `metric_point_unit.unit` | Metric unit string when present. |
| `OtlpMetric` | `Kind` | mapped | `metric_point.kind` | Metric kind (`gauge`, `sum`, `histogram`, etc.). |
| `OtlpMetric` | `AggregationTemporality` | mapped | `metric_point_temporality.aggregation_temporality` | OTLP temporality enum integer. |
| `OtlpMetric` | `IsMonotonic` | dropped | — | Sum monotonicity flag has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `DataPoints` | mapped | nested: OtlpMetricDataPoint | Normalized data points persisted under `metric_point.*`. |
| `OtlpMetric` | `Resource` | mapped | `metric_point_source.source` | `service.name` populates `metric_point_source.source`; `resource_json` was removed per `LegacyTableReplacementMap`. |
| `OtlpMetric` | `Scope` | dropped | — | `scope_json` on `metric_point` was removed per `LegacyTableReplacementMap` because it was written and never read. |
| `OtlpMetricDataPoint` | `Attributes` | dropped | — | `attributes_json` on `metric_point` was removed per `LegacyTableReplacementMap`; attributes fold into `metric_point.point_key`. |
| `OtlpMetricDataPoint` | `TimeUnixNano` | mapped | `metric_point_time.time_unix_nano`, `metric_point_timestamp.timestamp_utc` | Observation timestamp in nanoseconds and UTC ISO string. |
| `OtlpMetricDataPoint` | `StartTimeUnixNano` | mapped | `metric_point_start_time.start_time_unix_nano` | Cumulative window start timestamp in nanoseconds. |
| `OtlpMetricDataPoint` | `Value` | mapped | `metric_point_value.value` | Gauge/sum scalar value. |
| `OtlpMetricDataPoint` | `Count` | mapped | `metric_point_count.point_count` | Histogram observation count. |
| `OtlpMetricDataPoint` | `Sum` | mapped | `metric_point_sum.point_sum` | Histogram observation sum. |
| `RawSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span identifier. |
| `RawSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace identifier when present. |
| `RawSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier when present. |
| `RawSpan` | `Source` | mapped | `source.locator`, `quarantined_span_source.source` | Emitting `service.name` (or `otlp` fallback). |
| `RawSpan` | `Attributes` | mapped | `record_model.model_id`, `record_session_link.session_key`, `record_token_count.token_count` | `CodexAdapter` and `CodexSpanMapper` (via `OtlpSpanNative`) strip all keys outside the allowlist (`model`, `slug`, `conversation.id`, `gen_ai.session.id`, `session.id`, `event.timestamp`, `gen_ai.usage.*`, `codex.usage.*`) and drop prompts/tool payloads/PII without storing `raw`. |
| `RawSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `RawSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `RawSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | Optional ISO start timestamp. |

## droid

Factory (Droid) hooks emit contract-v1 `FactoryWireSpan` payloads via `FactoryHookMapper`, which arrive over OTLP as `OtlpSpan` / `RawSpan` and are normalized by `DroidAdapter`.

Droid OTLP spans are mapped to collector DTOs by `DroidSpanMapper` (`dash/server/KyberDash.Data/Harnesses/Otlp/DroidSpanMapper.cs`): span names drive the operation (`factory.session` -> `agent.invoke`, `factory.turn` -> `llm.invoke`, `factory.tool` -> `tool.invoke`); Droid keys win over canonical ones (`factory.session.id`, `factory.model.id`, `factory.tool.name`, `factory.tool.call_id`, `factory.tool.status`, `factory.tool.error_type`); `factory.turn.prompt.text` maps to a `user`-role `conversation_history` part; token classes follow the cache-inclusive convention; and Gemini-attributed spans (`gen_ai.system == "gemini"` or any `gemini.*` attribute) are excluded so they yield zero stored records.

Droid session files are read natively by `DroidSessionMapper` from `~/.factory/sessions/<dir>/*.jsonl` with the companion `<stem>.settings.json` settings file. Each JSONL line maps to a typed `DroidEntry` (`session_start` carrying `id`, `title`, and `cwd`; `message` carrying `role`, `timestamp`, and `content` blocks), and the settings file maps to `DroidSettings` (`model` plus session-level `tokenUsage` counters). `session_start.id` (falling back to the transcript file stem) maps to `session.native_session_id` / `record_session_link.session_key`; `session_start.title` (trimmed) maps to `session_title.title`; `session_start.cwd` maps to `session_workspace.working_directory` (sessions whose working directory is the Factory root itself are internal housekeeping and are dropped). Per-message `id` seeds deterministic `record.span_id` (`synth:droid:<sessionId>:<id>`, or a `msg-<n>` fallback); `timestamp` maps to `record.started_at_utc` with the import instant as fallback; `content` unpacks `text`, `thinking`, `tool_use`, and `tool_result` blocks (`tool_use` emits child `tool.invoke` spans with `tool_call.tool_name`, `tool_call.call_id`, `mcp__<server>__<tool>` server attribution, and input/result metrics; `Skill` inputs naming a skill emit `record_skill_invocation`; `tool_result` blocks pair outcomes onto their tool calls). Session-level `tokenUsage` counters distribute evenly across emitted assistant calls (remainder on the last call) into `record_token_count` (`input`, `output`, `cache_write`, `cache_read`, `reasoning`) and one `session_model_usage` snapshot per reported class; absent counters and the companion file itself emit no child rows. Stripped `model` (`custom:` prefixes, bracket segments, and trailing numeric suffixes removed) maps to `record_model.model_id` (absent when unknown); no cost is persisted because Droid reports none per call and estimation happens at read/projection time. Malformed JSONL lines and settings report `ingest_problem` warnings and never fail the pass.

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `FactoryWireSpan` | `TraceId` | mapped | `record_trace.trace_id` | Deterministic 32-hex trace id derived from `(sessionId, anchorId)`. |
| `FactoryWireSpan` | `SpanId` | mapped | `record.span_id` | Deterministic 16-hex span id derived from `(kind, sessionId, anchorId, role)`. |
| `FactoryWireSpan` | `Name` | mapped | `record.name`, `record.op` | `factory.session`, `factory.turn`, and `factory.tool` map to `agent.invoke`, `llm.invoke`, and `tool.invoke`. |
| `FactoryWireSpan` | `Kind` | mapped | `record.kind` | Wire `SPAN_KIND_INTERNAL` normalizes to `internal`. |
| `FactoryWireSpan` | `Attributes` | mapped | nested: FactoryWireEntry | Carries typed `factory.*` entries unpacked by `DroidAdapter`. |
| `FactoryWireEntry` | `Key` | mapped | `record_session_link.session_key`, `session_workspace.working_directory`, `record_model.model_id`, `content_part_text.part_text`, `tool_call.tool_name`, `tool_call.call_id`, `tool_call_outcome.succeeded`, `tool_call_error_code.code`, `tool_call_input_size.utf8_bytes`, `tool_call_result_size.utf8_bytes` | `factory.session.id`, `factory.cwd`, `factory.model.id`, `factory.turn.prompt.text`, `factory.tool.name`, `factory.tool.call_id`, `factory.tool.status`, `factory.tool.error_type`, `factory.tool.input_bytes`, and `factory.tool.result_bytes` map to their typed columns; `factory.contract_version`, `factory.message.id`, and `factory.turn.operation` are envelope-validation/correlation metadata dropped after attribution. |
| `FactoryWireEntry` | `Value` | mapped | nested: FactoryWireValue | Carries typed OTLP scalar value (`stringValue`, `intValue`, `doubleValue`). |
| `FactoryWireValue` | `StringValue` | mapped | `record_session_link.session_key`, `session_workspace.working_directory`, `record_model.model_id`, `content_part_text.part_text`, `tool_call.tool_name`, `tool_call.call_id`, `tool_call_outcome.succeeded`, `tool_call_error_code.code` | Decoded as string attribute value when present. |
| `FactoryWireValue` | `IntValue` | mapped | `tool_call_input_size.utf8_bytes`, `tool_call_result_size.utf8_bytes` | Decoded as 64-bit integer attribute value when present (`factory.tool.input_bytes`, `factory.tool.result_bytes`). |
| `FactoryWireValue` | `DoubleValue` | mapped | `record_reported_cost.amount` | Decoded as numeric attribute value when a floating-point `factory.*` attribute is emitted. |
| `FactoryMapOptions` | `ContentAllowed` | dropped | — | Consent gate flag controlling whether `FactoryHookMapper` emits `factory.turn.prompt.text`; not a persisted telemetry column. |
| `OtlpSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace correlation identifier. |
| `OtlpSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span key (or quarantined span key on invalid contract version). |
| `OtlpSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier. |
| `OtlpSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name (`factory.session`, `factory.turn`, `factory.tool`). |
| `OtlpSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `OtlpSpan` | `StartTimeUnixNano` | mapped | `record.started_at_utc` | Converted to ISO-8601 UTC `Timestamp`; raw nanosecond string is not persisted separately. |
| `OtlpSpan` | `EndTimeUnixNano` | mapped | `record_end.ended_at_utc` | End instant used for duration calculation and `record_end`. |
| `OtlpSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | ISO-8601 UTC start timestamp. |
| `OtlpSpan` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Rounded to non-negative integer milliseconds. |
| `OtlpSpan` | `Status` | mapped | nested: OtlpStatus | Normalized span status and error message. |
| `OtlpSpan` | `Attributes` | mapped | nested: RawSpan | Routed to `RawSpan.Attributes` for `DroidAdapter` extraction. |
| `OtlpSpan` | `Resource` | mapped | `source.locator`, `quarantined_span_source.source` | `service.name` populates `source.locator`; remaining resource attributes are dropped after attribution. |
| `OtlpSpan` | `Scope` | mapped | nested: OtlpScope | Decoded instrumentation scope; dropped from relational persistence. |
| `OtlpStatus` | `Code` | mapped | `record.status` | Normalized lowercase status (`unset`, `ok`, `error`). |
| `OtlpStatus` | `Message` | mapped | `record_error.message` | Persisted when status indicates an error. |
| `OtlpScope` | `Name` | dropped | — | Instrumentation scope name has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpScope` | `Version` | dropped | — | Instrumentation scope version has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpLog` | `LogId` | dropped | — | Legacy `pending_logs`/`quarantined_logs`/`enriched_logs` tables were removed per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `TraceId` | mapped | `record_trace.trace_id`, `ingest_problem_span.span_id` | Trace correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SpanId` | mapped | `record.span_id`, `ingest_problem_span.span_id` | Span correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SessionId` | mapped | `record_session_link.session_key`, `ingest_problem_session.session_id` | Session correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `Timestamp` | mapped | `record.started_at_utc`, `ingest_problem_timestamp.timestamp_utc` | Log event timestamp mapped on collector DTOs or diagnostics. |
| `OtlpLog` | `Body` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `ingest_problem.message` | Mapped on receipt into typed collector parts or `ingest_problem`; raw payload is discarded per DD-10. |
| `OtlpLog` | `Attributes` | dropped | — | Unstructured OTLP log `attributes_json` is dropped per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `Resource` | mapped | `source.locator` | `service.name` identifies source; remaining `resource_json` is dropped per DD-10. |
| `OtlpLog` | `Scope` | dropped | — | OTLP log instrumentation scope is not persisted in `RelationalSchemaCatalog` per DD-10. |
| `OtlpMetric` | `Name` | mapped | `metric_point.name` | Metric instrument name. |
| `OtlpMetric` | `Description` | dropped | — | Human-readable metric description has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `Unit` | mapped | `metric_point_unit.unit` | Metric unit string when present. |
| `OtlpMetric` | `Kind` | mapped | `metric_point.kind` | Metric kind (`gauge`, `sum`, `histogram`, etc.). |
| `OtlpMetric` | `AggregationTemporality` | mapped | `metric_point_temporality.aggregation_temporality` | OTLP temporality enum integer. |
| `OtlpMetric` | `IsMonotonic` | dropped | — | Sum monotonicity flag has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `DataPoints` | mapped | nested: OtlpMetricDataPoint | Normalized data points persisted under `metric_point.*`. |
| `OtlpMetric` | `Resource` | mapped | `metric_point_source.source` | `service.name` populates `metric_point_source.source`; `resource_json` was removed per `LegacyTableReplacementMap`. |
| `OtlpMetric` | `Scope` | dropped | — | `scope_json` on `metric_point` was removed per `LegacyTableReplacementMap` because it was written and never read. |
| `OtlpMetricDataPoint` | `Attributes` | dropped | — | `attributes_json` on `metric_point` was removed per `LegacyTableReplacementMap`; attributes fold into `metric_point.point_key`. |
| `OtlpMetricDataPoint` | `TimeUnixNano` | mapped | `metric_point_time.time_unix_nano`, `metric_point_timestamp.timestamp_utc` | Observation timestamp in nanoseconds and UTC ISO string. |
| `OtlpMetricDataPoint` | `StartTimeUnixNano` | mapped | `metric_point_start_time.start_time_unix_nano` | Cumulative window start timestamp in nanoseconds. |
| `OtlpMetricDataPoint` | `Value` | mapped | `metric_point_value.value` | Gauge/sum scalar value. |
| `OtlpMetricDataPoint` | `Count` | mapped | `metric_point_count.point_count` | Histogram observation count. |
| `OtlpMetricDataPoint` | `Sum` | mapped | `metric_point_sum.point_sum` | Histogram observation sum. |
| `RawSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span identifier. |
| `RawSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace identifier when present. |
| `RawSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier when present. |
| `RawSpan` | `Source` | mapped | `source.locator`, `quarantined_span_source.source` | Emitting `service.name` (or `otlp` fallback). |
| `RawSpan` | `Attributes` | mapped | `record_session_link.session_key`, `record_model.model_id`, `content_part_text.part_text`, `tool_call.tool_name`, `tool_call.call_id`, `tool_call_input_size.utf8_bytes`, `tool_call_result_size.utf8_bytes` | `DroidAdapter` extracts `factory.*` session, model, prompt, and tool telemetry; unmapped/PII keys are dropped. |
| `RawSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Maps `factory.session`, `factory.turn`, and `factory.tool` to `record.op`. |
| `RawSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `RawSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | Optional ISO start timestamp. |

## opencode

OpenCode V2 capture (`kyberdash-opencode`) posts contract-3 OTLP spans (`OtlpSpan`, `RawSpan`) claimed by `OpencodeAdapter`. Unrecognized contract versions are quarantined.

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `OtlpSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace correlation identifier. |
| `OtlpSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span key (or quarantined span key on unsupported contract version). |
| `OtlpSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier linking `step`/`tool` spans to `request`. |
| `OtlpSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name (`opencode.tool.name` overrides for `tool` spans). |
| `OtlpSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `OtlpSpan` | `StartTimeUnixNano` | mapped | `record.started_at_utc` | Converted to ISO-8601 UTC `Timestamp`; raw nanosecond string is not persisted separately. |
| `OtlpSpan` | `EndTimeUnixNano` | mapped | `record_end.ended_at_utc` | End instant used for duration calculation and `record_end`. |
| `OtlpSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | ISO-8601 UTC start timestamp. |
| `OtlpSpan` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Rounded to non-negative integer milliseconds. |
| `OtlpSpan` | `Status` | mapped | nested: OtlpStatus | Normalized span status and error message. |
| `OtlpSpan` | `Attributes` | mapped | nested: RawSpan | Routed to `RawSpan.Attributes` for `OpencodeAdapter` extraction. |
| `OtlpSpan` | `Resource` | mapped | `source.locator`, `quarantined_span_source.source` | `service.name` populates `source.locator`; remaining resource attributes are dropped after attribution. |
| `OtlpSpan` | `Scope` | mapped | nested: OtlpScope | Decoded instrumentation scope; dropped from relational persistence. |
| `OtlpStatus` | `Code` | mapped | `record.status` | Normalized lowercase status (`unset`, `ok`, `error`). |
| `OtlpStatus` | `Message` | mapped | `record_error.message` | Persisted when status indicates an error. |
| `OtlpScope` | `Name` | dropped | — | Instrumentation scope name has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpScope` | `Version` | dropped | — | Instrumentation scope version has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpLog` | `LogId` | dropped | — | Legacy `pending_logs`/`quarantined_logs`/`enriched_logs` tables were removed per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `TraceId` | mapped | `record_trace.trace_id`, `ingest_problem_span.span_id` | Trace correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SpanId` | mapped | `record.span_id`, `ingest_problem_span.span_id` | Span correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SessionId` | mapped | `record_session_link.session_key`, `ingest_problem_session.session_id` | Session correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `Timestamp` | mapped | `record.started_at_utc`, `ingest_problem_timestamp.timestamp_utc` | Log event timestamp mapped on collector DTOs or diagnostics. |
| `OtlpLog` | `Body` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `ingest_problem.message` | Mapped on receipt into typed collector parts or `ingest_problem`; raw payload is discarded per DD-10. |
| `OtlpLog` | `Attributes` | dropped | — | Unstructured OTLP log `attributes_json` is dropped per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `Resource` | mapped | `source.locator` | `service.name` identifies source; remaining `resource_json` is dropped per DD-10. |
| `OtlpLog` | `Scope` | dropped | — | OTLP log instrumentation scope is not persisted in `RelationalSchemaCatalog` per DD-10. |
| `OtlpMetric` | `Name` | mapped | `metric_point.name` | Metric instrument name. |
| `OtlpMetric` | `Description` | dropped | — | Human-readable metric description has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `Unit` | mapped | `metric_point_unit.unit` | Metric unit string when present. |
| `OtlpMetric` | `Kind` | mapped | `metric_point.kind` | Metric kind (`gauge`, `sum`, `histogram`, etc.). |
| `OtlpMetric` | `AggregationTemporality` | mapped | `metric_point_temporality.aggregation_temporality` | OTLP temporality enum integer. |
| `OtlpMetric` | `IsMonotonic` | dropped | — | Sum monotonicity flag has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `DataPoints` | mapped | nested: OtlpMetricDataPoint | Normalized data points persisted under `metric_point.*`. |
| `OtlpMetric` | `Resource` | mapped | `metric_point_source.source` | `service.name` populates `metric_point_source.source`; `resource_json` was removed per `LegacyTableReplacementMap`. |
| `OtlpMetric` | `Scope` | dropped | — | `scope_json` on `metric_point` was removed per `LegacyTableReplacementMap` because it was written and never read. |
| `OtlpMetricDataPoint` | `Attributes` | dropped | — | `attributes_json` on `metric_point` was removed per `LegacyTableReplacementMap`; attributes fold into `metric_point.point_key`. |
| `OtlpMetricDataPoint` | `TimeUnixNano` | mapped | `metric_point_time.time_unix_nano`, `metric_point_timestamp.timestamp_utc` | Observation timestamp in nanoseconds and UTC ISO string. |
| `OtlpMetricDataPoint` | `StartTimeUnixNano` | mapped | `metric_point_start_time.start_time_unix_nano` | Cumulative window start timestamp in nanoseconds. |
| `OtlpMetricDataPoint` | `Value` | mapped | `metric_point_value.value` | Gauge/sum scalar value. |
| `OtlpMetricDataPoint` | `Count` | mapped | `metric_point_count.point_count` | Histogram observation count. |
| `OtlpMetricDataPoint` | `Sum` | mapped | `metric_point_sum.point_sum` | Histogram observation sum. |
| `RawSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span identifier. |
| `RawSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace identifier when present. |
| `RawSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier when present. |
| `RawSpan` | `Source` | mapped | `source.locator`, `quarantined_span_source.source` | Emitting `service.name` (or `otlp` fallback). |
| `RawSpan` | `Attributes` | mapped | `record.op`, `record_session_link.session_key`, `record_model.model_id`, `record_token_count.token_count`, `record_reported_cost.amount`, `content_part_text.part_text`, `tool_definition.tool_name` | `OpencodeAdapter` maps `opencode.span.kind`, `opencode.session.id`, `opencode.model.id`, `opencode.usage.*`, `opencode.cost`, `opencode.prompt.text`, `opencode.context.*`, and `opencode.tool.name`, then strips content keys and drops unmapped/PII keys. |
| `RawSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name or `opencode.tool.name`. |
| `RawSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `RawSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | Optional ISO start timestamp. |

## copilot

Copilot ingests over OTLP through `CopilotAdapter` (`OtlpSpan`, `OtlpLog`, `OtlpMetric`, `RawSpan`), converting inclusive input counts into disjoint token classes and marking unexported `reasoning` or absent content buckets as unmeasurable.

For OTLP span mapping into static collector DTOs, `CopilotSpanMapper` maps `gen_ai.tool.name` spans to `tool.invoke` `CollectedRecord`s, `invoke_agent` operation or span-name spans to `agent.invoke` `CollectedRecord`s, and usage-bearing spans to `llm.invoke` `CollectedRecord`s with the cache-inclusive token convention, using the canonical session key order (`gen_ai.session.id`, `session.id`, then `copilot_chat.chat_session_id`), and drops unmapped attributes without storing `raw`.

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `OtlpSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace correlation identifier. |
| `OtlpSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span key (or quarantined span key). |
| `OtlpSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier. |
| `OtlpSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `OtlpSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `OtlpSpan` | `StartTimeUnixNano` | mapped | `record.started_at_utc` | Converted to ISO-8601 UTC `Timestamp`; raw nanosecond string is not persisted separately. |
| `OtlpSpan` | `EndTimeUnixNano` | mapped | `record_end.ended_at_utc` | End instant used for duration calculation and `record_end`. |
| `OtlpSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | ISO-8601 UTC start timestamp. |
| `OtlpSpan` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Rounded to non-negative integer milliseconds. |
| `OtlpSpan` | `Status` | mapped | nested: OtlpStatus | Normalized span status and error message. |
| `OtlpSpan` | `Attributes` | mapped | nested: RawSpan | Routed to `RawSpan.Attributes` for `CopilotAdapter` extraction. |
| `OtlpSpan` | `Resource` | mapped | `source.locator`, `quarantined_span_source.source` | `service.name` populates `source.locator`; remaining resource attributes are dropped after attribution. |
| `OtlpSpan` | `Scope` | mapped | nested: OtlpScope | Decoded instrumentation scope; dropped from relational persistence. |
| `OtlpStatus` | `Code` | mapped | `record.status` | Normalized lowercase status (`unset`, `ok`, `error`). |
| `OtlpStatus` | `Message` | mapped | `record_error.message` | Persisted when status indicates an error. |
| `OtlpScope` | `Name` | dropped | — | Instrumentation scope name has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpScope` | `Version` | dropped | — | Instrumentation scope version has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpLog` | `LogId` | dropped | — | Legacy `pending_logs`/`quarantined_logs`/`enriched_logs` tables were removed per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `TraceId` | mapped | `record_trace.trace_id`, `ingest_problem_span.span_id` | Trace correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SpanId` | mapped | `record.span_id`, `ingest_problem_span.span_id` | Span correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SessionId` | mapped | `record_session_link.session_key`, `ingest_problem_session.session_id` | Session correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `Timestamp` | mapped | `record.started_at_utc`, `ingest_problem_timestamp.timestamp_utc` | Log event timestamp mapped on collector DTOs or diagnostics. |
| `OtlpLog` | `Body` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `ingest_problem.message` | Mapped on receipt into typed collector parts or `ingest_problem`; raw payload is discarded per DD-10. |
| `OtlpLog` | `Attributes` | dropped | — | Unstructured OTLP log `attributes_json` is dropped per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `Resource` | mapped | `source.locator` | `service.name` identifies source; remaining `resource_json` is dropped per DD-10. |
| `OtlpLog` | `Scope` | dropped | — | OTLP log instrumentation scope is not persisted in `RelationalSchemaCatalog` per DD-10. |
| `OtlpMetric` | `Name` | mapped | `metric_point.name` | Metric instrument name. |
| `OtlpMetric` | `Description` | dropped | — | Human-readable metric description has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `Unit` | mapped | `metric_point_unit.unit` | Metric unit string when present. |
| `OtlpMetric` | `Kind` | mapped | `metric_point.kind` | Metric kind (`gauge`, `sum`, `histogram`, etc.). |
| `OtlpMetric` | `AggregationTemporality` | mapped | `metric_point_temporality.aggregation_temporality` | OTLP temporality enum integer. |
| `OtlpMetric` | `IsMonotonic` | dropped | — | Sum monotonicity flag has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `DataPoints` | mapped | nested: OtlpMetricDataPoint | Normalized data points persisted under `metric_point.*`. |
| `OtlpMetric` | `Resource` | mapped | `metric_point_source.source` | `service.name` populates `metric_point_source.source`; `resource_json` was removed per `LegacyTableReplacementMap`. |
| `OtlpMetric` | `Scope` | dropped | — | `scope_json` on `metric_point` was removed per `LegacyTableReplacementMap` because it was written and never read. |
| `OtlpMetricDataPoint` | `Attributes` | dropped | — | `attributes_json` on `metric_point` was removed per `LegacyTableReplacementMap`; attributes fold into `metric_point.point_key`. |
| `OtlpMetricDataPoint` | `TimeUnixNano` | mapped | `metric_point_time.time_unix_nano`, `metric_point_timestamp.timestamp_utc` | Observation timestamp in nanoseconds and UTC ISO string. |
| `OtlpMetricDataPoint` | `StartTimeUnixNano` | mapped | `metric_point_start_time.start_time_unix_nano` | Cumulative window start timestamp in nanoseconds. |
| `OtlpMetricDataPoint` | `Value` | mapped | `metric_point_value.value` | Gauge/sum scalar value. |
| `OtlpMetricDataPoint` | `Count` | mapped | `metric_point_count.point_count` | Histogram observation count. |
| `OtlpMetricDataPoint` | `Sum` | mapped | `metric_point_sum.point_sum` | Histogram observation sum. |
| `RawSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span identifier. |
| `RawSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace identifier when present. |
| `RawSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier when present. |
| `RawSpan` | `Source` | mapped | `source.locator`, `quarantined_span_source.source` | Emitting `service.name` (or `otlp` fallback). |
| `RawSpan` | `Attributes` | mapped | `record_model.model_id`, `record_session_link.session_key`, `record_token_count.token_count`, `content_part_text.part_text`, `content_part_server.server_name`, `content_part_reported_tokens.token_count` | `CopilotAdapter` extracts inclusive `gen_ai.*`/`copilot_chat.*` usage, messages, system instructions, rules, skills, and tool definitions; unmapped/PII keys are dropped. |
| `RawSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `RawSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `RawSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | Optional ISO start timestamp. |

## copilot-cli

Copilot CLI ingests local `~/.copilot/session-state/<id>/events.jsonl` session journals via `CopilotCliJournalReader` and `CopilotCliMapper` into `CollectedBatch` (`CopilotCliJournalDocument`, `CopilotCliJournalEvent`, `CopilotCliContextInfo`, `CopilotCliToolRequest`, `CopilotCliJournalProblem`). Draft `pending-session:*` journals are skipped, and every `assistant.message` emits an `llm.invoke` record even when `outputTokens` is absent (omitting the `TokenClass.Output` row rather than synthesizing zero).

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `CopilotCliJournalDocument` | `SessionId` | mapped | `session.session_id`, `session.native_session_id`, `record_session_link.session_key` | Native session id from `session.start` or parent directory name. |
| `CopilotCliJournalDocument` | `IsPendingSession` | dropped | — | Draft-session filter flag causing `pending-session:*` journals to be skipped without persistence. |
| `CopilotCliJournalDocument` | `InitialModel` | mapped | `record_model.model_id` | Initial model from `session.start` (`selectedModel`/`model`), active until `session.model_change`. |
| `CopilotCliJournalDocument` | `CopilotVersion` | dropped | — | Copilot CLI version string from `session.start` has no column on `session` or `record` in `RelationalSchemaCatalog`. |
| `CopilotCliJournalDocument` | `Title` | mapped | `session_title.title` | Optional session title extracted from session events. |
| `CopilotCliJournalDocument` | `InitialContext` | mapped | nested: CopilotCliContextInfo | Initial workspace snapshot from `session.start`. |
| `CopilotCliJournalDocument` | `Events` | mapped | nested: CopilotCliJournalEvent | Ordered journal events processed by `CopilotCliMapper`. |
| `CopilotCliJournalDocument` | `ParseProblems` | mapped | nested: CopilotCliJournalProblem | Non-fatal line-level JSONL parse diagnostics emitted as `ingest_problem` rows. |
| `CopilotCliJournalEvent` | `LineNumber` | mapped | `record.span_id`, `ingest_problem_location.location` | 1-based JSONL line number used for deterministic span disambiguation and diagnostic locators. |
| `CopilotCliJournalEvent` | `EventType` | mapped | `record.op`, `record.name`, `tool_call_outcome.succeeded`, `record_end.ended_at_utc`, `record_skill_invocation.skill_name` | Discriminates `session.start`, `system.message`, `user.message`, `assistant.reasoning`, `assistant.message` (`llm.invoke`), `tool.execution_start`, `tool.execution_complete` (`tool.invoke` with optional `record_end` and `tool_call_outcome`), `skill.invoked` (`record_skill_invocation`), `session.model_change`, and context changes. |
| `CopilotCliJournalEvent` | `EventId` | mapped | `record.span_id` | Fallback native event identifier when `MessageId` is absent. |
| `CopilotCliJournalEvent` | `ParentId` | dropped | — | Journal event parent UUID links raw JSONL events rather than normalized `record_parent` span relationships. |
| `CopilotCliJournalEvent` | `Timestamp` | mapped | `record.started_at_utc` | ISO-8601 UTC event timestamp. |
| `CopilotCliJournalEvent` | `AgentId` | mapped | `record_agent_instance.agent_instance_id` | Subagent identifier isolating subagent prompts, reasoning, and messages from the root agent. |
| `CopilotCliJournalEvent` | `SessionId` | mapped | `session.native_session_id`, `record_session_link.session_key` | Native session identifier carried on `session.start` or journal events. |
| `CopilotCliJournalEvent` | `MessageId` | mapped | `record.span_id` | Primary native anchor for deterministic `synth:copilot-cli:<session>:<messageId>` record identity. |
| `CopilotCliJournalEvent` | `Content` | mapped | `content_part.bucket`, `content_part.position`, `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `content_part_role.role_name`, `content_part_truncation.original_utf8_bytes` | Measured via `ToolContentMeasurer` into `system_prompt`, `user_message`, `reasoning`, or `conversation_history` parts (JSON documents withheld). |
| `CopilotCliJournalEvent` | `ReasoningText` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes` | Inline reasoning payload mapped to `reasoning` bucket (`role = assistant`). |
| `CopilotCliJournalEvent` | `Model` | mapped | `record_model.model_id` | Model identifier updated on `session.start`/`session.model_change` or overridden per `assistant.message`. |
| `CopilotCliJournalEvent` | `PreviousModel` | dropped | — | Transition metadata on `session.model_change`; only the active `Model` is stored on `record_model`. |
| `CopilotCliJournalEvent` | `InputTokens` | mapped | `record_token_count.token_count` | Fresh input token count when reported (`token_class = input`). |
| `CopilotCliJournalEvent` | `OutputTokens` | mapped | `record_token_count.token_count` | Output token count when present (`token_class = output`); omitted on Copilot CLI 1.0.81+ leaving no `output` token row. |
| `CopilotCliJournalEvent` | `CacheReadTokens` | mapped | `record_token_count.token_count` | Cache-read token count when reported (`token_class = cache_read`). |
| `CopilotCliJournalEvent` | `CacheWriteTokens` | mapped | `record_token_count.token_count` | Cache-write token count when reported (`token_class = cache_write`). |
| `CopilotCliJournalEvent` | `ReasoningTokens` | mapped | `record_token_count.token_count` | Reasoning token count when reported (`token_class = reasoning`). |
| `CopilotCliJournalEvent` | `DurationMs` | mapped | `record_reported_duration.duration_ms`, `record_end.ended_at_utc` | Non-negative duration in milliseconds when reported; derives `record_end`. |
| `CopilotCliJournalEvent` | `DeclaredContextWindow` | mapped | `record_declared_context_window.declared_context_window` | Explicit context window limit declared on session/context events. |
| `CopilotCliJournalEvent` | `Title` | mapped | `session_title.title` | Session title update carried on session/info events. |
| `CopilotCliJournalEvent` | `Context` | mapped | nested: CopilotCliContextInfo | Workspace context snapshot carried on `session.start` or context-change events. |
| `CopilotCliJournalEvent` | `ToolRequests` | mapped | nested: CopilotCliToolRequest | Correlates `toolRequests[]` with `tool.execution_start` and `tool.execution_complete` into child `tool.invoke` spans parented to the requesting `llm.invoke` (with JSON-document results retaining `tool_call_result_size` only, incomplete tools omitting `record_end` and `tool_call_outcome`, and MCP metadata populating `tool_call_mcp`) and extracts invoked skills into `record_skill_invocation`. |
| `CopilotCliContextInfo` | `WorkingDirectory` | mapped | `session_workspace.working_directory` | Active working directory (`cwd`) merged across context-change events. |
| `CopilotCliContextInfo` | `Repository` | mapped | `session_repository.repo_name` | Active repository name (`repository`, falling back to `GitRoot` folder name). |
| `CopilotCliContextInfo` | `Branch` | mapped | `session_branch.branch_name` | Active git branch merged across context-change events. |
| `CopilotCliContextInfo` | `GitRoot` | mapped | `session_repository.repo_name` | Supplies repository folder name fallback when `Repository` is absent. |
| `CopilotCliToolRequest` | `ToolCallId` | mapped | `tool_call.call_id`, `record_parent.parent_span_id` | Correlates `toolRequests[]` with `tool.execution_start` and `tool.execution_complete` into child `tool.invoke` spans linked to the requesting `assistant.message`. |
| `CopilotCliToolRequest` | `Name` | mapped | `tool_call.tool_name`, `record.name`, `tool_call_mcp.server_name`, `tool_call_mcp.server_tool_name` | Invoked tool name on `tool_call` and `tool.invoke` record (with `mcp__<server>__<tool>` or explicit MCP metadata populating `tool_call_mcp`; `skill` requests also populate `record_skill_invocation`). |
| `CopilotCliToolRequest` | `SkillName` | mapped | `record_skill_invocation.skill_name` | Invoked skill identifier extracted from `skill` tool requests and `skill.invoked` events (deduplicated per `llm.invoke` record). |
| `CopilotCliJournalProblem` | `LineNumber` | mapped | `ingest_problem_location.location`, `ingest_problem.problem_key` | 1-based line number appended to problem key and locator. |
| `CopilotCliJournalProblem` | `Code` | mapped | `ingest_problem.code` | Diagnostic code (`COPILOT_CLI_MALFORMED_LINE`). |
| `CopilotCliJournalProblem` | `Message` | mapped | `ingest_problem.message` | Sanitized parse failure message. |

## copilot-vscode

Copilot VS Code ingests local VS Code `chatSessions` append/edit journals (`kind: 0/1/2`) and JSON session snapshots via `CopilotVscodeMapper` into `CollectedBatch` (`CopilotVscodeJournalDocument`, `CopilotVscodeRequest`, `CopilotVscodeToolCall`, `CopilotVscodeJournalProblem`). Each request emits an `llm.invoke` record carrying cumulative prior user messages and assistant responses plus the current user message and `modeInstructions` (excluding the current request's own response), along with child `tool.invoke` records from `result.metadata.toolCallRounds`.

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `CopilotVscodeJournalDocument` | `SessionId` | mapped | `session.session_id`, `session.native_session_id`, `record_session_link.session_key` | Native session id from `sessionId`, falling back to journal file stem or source digest. |
| `CopilotVscodeJournalDocument` | `CreationDate` | mapped | `record.started_at_utc` | Session creation timestamp used as fallback when a request omits `timestamp`. |
| `CopilotVscodeJournalDocument` | `JournalVersion` | dropped | — | Journal format version integer (`version`) has no column on `session` or `record` in `RelationalSchemaCatalog`. |
| `CopilotVscodeJournalDocument` | `Requests` | mapped | nested: CopilotVscodeRequest | Reconstructed ordered requests from `requests[]`. |
| `CopilotVscodeJournalDocument` | `ParseProblems` | mapped | nested: CopilotVscodeJournalProblem | Non-fatal line-level JSONL parse diagnostics emitted as `ingest_problem` rows. |
| `CopilotVscodeRequest` | `RequestIndex` | mapped | `record.span_id` | Zero-based index used for fallback `request-<index>` identity and duplicate `RequestId` disambiguation. |
| `CopilotVscodeRequest` | `RequestId` | mapped | `record.span_id` | Primary native anchor for deterministic `synth:copilot-vscode:<session>:<requestId>` record identity. |
| `CopilotVscodeRequest` | `Timestamp` | mapped | `record.started_at_utc` | ISO-8601 UTC request start timestamp (epoch numbers and ISO strings normalized). |
| `CopilotVscodeRequest` | `ModelId` | mapped | `record_model.model_id` | Fallback model identifier after stripping leading `copilot/` prefix when `ResolvedModel` is absent. |
| `CopilotVscodeRequest` | `ResolvedModel` | mapped | `record_model.model_id` | Primary resolved model identifier from `result.metadata.resolvedModel`. |
| `CopilotVscodeRequest` | `AgentId` | mapped | `record_agent_instance.agent_instance_id` | Agent identifier from `result.metadata.agentId`. |
| `CopilotVscodeRequest` | `UserMessage` | mapped | `content_part.bucket`, `content_part.position`, `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `content_part_role.role_name`, `content_part_truncation.original_utf8_bytes`, `record_correction.span_id`, `record_correction_rule.correction_rule` | Measured via `ToolContentMeasurer` into `conversation_history` (`role = user`) and scanned for user-correction rules. |
| `CopilotVscodeRequest` | `ResponseText` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `content_part_role.role_name` | Assistant reply emitted into subsequent requests' `conversation_history` (`role = assistant`) and excluded from its own request. |
| `CopilotVscodeRequest` | `ModeInstructions` | mapped | `content_part.bucket`, `content_part.position`, `content_part_text.part_text`, `content_part_withheld.utf8_bytes` | Deduplicated `modeInstructions` measured via `ToolContentMeasurer` into `instruction_context`. |
| `CopilotVscodeRequest` | `PromptTokens` | mapped | `record_token_count.token_count`, `record_reported_total.token_count` | Input prompt token count (`token_class = input`, `direction = input`) from `result.metadata.promptTokens`. |
| `CopilotVscodeRequest` | `MetadataOutputTokens` | mapped | `record_token_count.token_count`, `record_reported_total.token_count` | Primary output token count (`token_class = output`, `direction = output`) from `result.metadata.outputTokens`. |
| `CopilotVscodeRequest` | `CompletionTokens` | mapped | `record_token_count.token_count`, `record_reported_total.token_count` | Fallback output token count (`token_class = output`, `direction = output`) when `MetadataOutputTokens` is absent or zero. |
| `CopilotVscodeRequest` | `ToolCalls` | mapped | nested: CopilotVscodeToolCall | Tool invocations extracted from `result.metadata.toolCallRounds` into child `tool.invoke` spans and `record_skill_invocation.skill_name`. |
| `CopilotVscodeToolCall` | `CallId` | mapped | `tool_call.call_id` | Native tool call correlation identifier (or `tool-<index>` fallback). |
| `CopilotVscodeToolCall` | `ToolName` | mapped | `tool_call.tool_name`, `record.name` | Invoked tool name on child `tool.invoke` span. |
| `CopilotVscodeToolCall` | `RawArgumentsText` | mapped | `tool_call_input_size.utf8_bytes`, `tool_call_input_size.characters`, `tool_call_input_fingerprint.sha256`, `tool_call_input_text.text`, `tool_call_input_truncation.original_utf8_bytes` | Measured via `ToolContentMeasurer` with JSON payloads withheld to size + SHA-256 fingerprint. |
| `CopilotVscodeJournalProblem` | `LineNumber` | mapped | `ingest_problem_location.location`, `ingest_problem.problem_key` | 1-based line number appended to problem key and locator. |
| `CopilotVscodeJournalProblem` | `Code` | mapped | `ingest_problem.code` | Diagnostic code (`COPILOT_VSCODE_MALFORMED_LINE`). |
| `CopilotVscodeJournalProblem` | `Message` | mapped | `ingest_problem.message` | Sanitized parse failure message. |

## antigravity

Antigravity ingests over OTLP through `AntigravityAdapter` (`OtlpSpan`, `OtlpLog`, `OtlpMetric`, `RawSpan`), claiming spans carrying `gen_ai.agent.name = "antigravity"` ahead of `GeminiAdapter`, converting inclusive counts, and mapping `gemini.usage.thoughts_tokens` to reasoning tokens while leaving `cache_creation` unexported. Local `.gemini/antigravity/brain/<conversationId>/.system_generated/logs/transcript.jsonl` transcripts are mapped by `AntigravityTranscriptMapper` (`PLANNER_RESPONSE.tool_calls` to child `tool.invoke` spans and `view_file` `SKILL.md` reads to `record_skill_invocation.skill_name`; native transcript DTOs are documented under `## antigravity-cli`).

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `OtlpSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace correlation identifier. |
| `OtlpSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span key (or quarantined span key). |
| `OtlpSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier. |
| `OtlpSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `OtlpSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `OtlpSpan` | `StartTimeUnixNano` | mapped | `record.started_at_utc` | Converted to ISO-8601 UTC `Timestamp`; raw nanosecond string is not persisted separately. |
| `OtlpSpan` | `EndTimeUnixNano` | mapped | `record_end.ended_at_utc` | End instant used for duration calculation and `record_end`. |
| `OtlpSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | ISO-8601 UTC start timestamp. |
| `OtlpSpan` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Rounded to non-negative integer milliseconds. |
| `OtlpSpan` | `Status` | mapped | nested: OtlpStatus | Normalized span status and error message. |
| `OtlpSpan` | `Attributes` | mapped | nested: RawSpan | Routed to `RawSpan.Attributes` for `AntigravityAdapter` extraction. |
| `OtlpSpan` | `Resource` | mapped | `source.locator`, `quarantined_span_source.source` | `service.name` populates `source.locator`; remaining resource attributes are dropped after attribution. |
| `OtlpSpan` | `Scope` | mapped | nested: OtlpScope | Decoded instrumentation scope; dropped from relational persistence. |
| `OtlpStatus` | `Code` | mapped | `record.status` | Normalized lowercase status (`unset`, `ok`, `error`). |
| `OtlpStatus` | `Message` | mapped | `record_error.message` | Persisted when status indicates an error. |
| `OtlpScope` | `Name` | dropped | — | Instrumentation scope name has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpScope` | `Version` | dropped | — | Instrumentation scope version has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpLog` | `LogId` | dropped | — | Legacy `pending_logs`/`quarantined_logs`/`enriched_logs` tables were removed per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `TraceId` | mapped | `record_trace.trace_id`, `ingest_problem_span.span_id` | Trace correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SpanId` | mapped | `record.span_id`, `ingest_problem_span.span_id` | Span correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SessionId` | mapped | `record_session_link.session_key`, `ingest_problem_session.session_id` | Session correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `Timestamp` | mapped | `record.started_at_utc`, `ingest_problem_timestamp.timestamp_utc` | Log event timestamp mapped on collector DTOs or diagnostics. |
| `OtlpLog` | `Body` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `ingest_problem.message` | Mapped on receipt into typed collector parts or `ingest_problem`; raw payload is discarded per DD-10. |
| `OtlpLog` | `Attributes` | dropped | — | Unstructured OTLP log `attributes_json` is dropped per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `Resource` | mapped | `source.locator` | `service.name` identifies source; remaining `resource_json` is dropped per DD-10. |
| `OtlpLog` | `Scope` | dropped | — | OTLP log instrumentation scope is not persisted in `RelationalSchemaCatalog` per DD-10. |
| `OtlpMetric` | `Name` | mapped | `metric_point.name` | Metric instrument name. |
| `OtlpMetric` | `Description` | dropped | — | Human-readable metric description has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `Unit` | mapped | `metric_point_unit.unit` | Metric unit string when present. |
| `OtlpMetric` | `Kind` | mapped | `metric_point.kind` | Metric kind (`gauge`, `sum`, `histogram`, etc.). |
| `OtlpMetric` | `AggregationTemporality` | mapped | `metric_point_temporality.aggregation_temporality` | OTLP temporality enum integer. |
| `OtlpMetric` | `IsMonotonic` | dropped | — | Sum monotonicity flag has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `DataPoints` | mapped | nested: OtlpMetricDataPoint | Normalized data points persisted under `metric_point.*`. |
| `OtlpMetric` | `Resource` | mapped | `metric_point_source.source` | `service.name` populates `metric_point_source.source`; `resource_json` was removed per `LegacyTableReplacementMap`. |
| `OtlpMetric` | `Scope` | dropped | — | `scope_json` on `metric_point` was removed per `LegacyTableReplacementMap` because it was written and never read. |
| `OtlpMetricDataPoint` | `Attributes` | dropped | — | `attributes_json` on `metric_point` was removed per `LegacyTableReplacementMap`; attributes fold into `metric_point.point_key`. |
| `OtlpMetricDataPoint` | `TimeUnixNano` | mapped | `metric_point_time.time_unix_nano`, `metric_point_timestamp.timestamp_utc` | Observation timestamp in nanoseconds and UTC ISO string. |
| `OtlpMetricDataPoint` | `StartTimeUnixNano` | mapped | `metric_point_start_time.start_time_unix_nano` | Cumulative window start timestamp in nanoseconds. |
| `OtlpMetricDataPoint` | `Value` | mapped | `metric_point_value.value` | Gauge/sum scalar value. |
| `OtlpMetricDataPoint` | `Count` | mapped | `metric_point_count.point_count` | Histogram observation count. |
| `OtlpMetricDataPoint` | `Sum` | mapped | `metric_point_sum.point_sum` | Histogram observation sum. |
| `RawSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span identifier. |
| `RawSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace identifier when present. |
| `RawSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier when present. |
| `RawSpan` | `Source` | mapped | `source.locator`, `quarantined_span_source.source` | Emitting `service.name` (or `otlp` fallback). |
| `RawSpan` | `Attributes` | mapped | `record_model.model_id`, `record_session_link.session_key`, `record_token_count.token_count`, `content_part_text.part_text` | `AntigravityAdapter` extracts inclusive `gen_ai.*` usage, `gemini.usage.thoughts_tokens`, and canonical parts while dropping unmapped/PII keys. |
| `RawSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `RawSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `RawSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | Optional ISO start timestamp. |

## antigravity-cli

Antigravity CLI ingests local `.gemini/antigravity-cli/brain/<conversationId>/.system_generated/logs/transcript.jsonl` transcripts (`AntigravityTranscriptStep`, `AntigravityTranscriptToolCall`, `AntigravityTranscriptSegment`) and `cache/antigravity-statusline.jsonl` snapshots (`AntigravityStatusLineEntry`) via `AntigravityTranscriptMapper` (`AntigravityParseProblem` diagnostics). `PLANNER_RESPONSE` steps emit `llm.invoke` records with child `tool.invoke` spans for `tool_calls`, inferring `SKILL.md` reads from `view_file` only and withholding JSON object/array payloads; statusline snapshots collapse consecutive duplicate usage runs, skip non-final singletons, delta monotonic cumulative counters, reset on non-monotonic snapshots, and drop unmapped `cwd`, `session_id`, `display_name`, and `context_window_size` fields.

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `AntigravityTranscriptStep` | `StepIndex` | mapped | `record.span_id` | Transcript `step_index` anchoring deterministic `synth:<harness>:<session>:step-<stepIndex>` record identity and `agy-step:` segmentation bounds. |
| `AntigravityTranscriptStep` | `StepType` | mapped | `record.op`, `record.name` | Discriminates `USER_INPUT`, `PLANNER_RESPONSE` (`llm.invoke`), and `GENERIC` tool result steps. |
| `AntigravityTranscriptStep` | `Source` | dropped | — | Native step source label (`USER_EXPLICIT`, `MODEL`, `SYSTEM`) has no column in `RelationalSchemaCatalog`; role is normalized onto `content_part_role.role_name`. |
| `AntigravityTranscriptStep` | `Status` | mapped | `record.status` | Maps `DONE`/`OK` to `ok`, `ERROR`/`FAILED` to `error`, and other states to `unspecified`. |
| `AntigravityTranscriptStep` | `CreatedAt` | mapped | `record.started_at_utc`, `record_end.ended_at_utc`, `record_reported_duration.duration_ms` | ISO-8601 UTC step timestamp used for parent start and paired single-call `GENERIC` end/duration. |
| `AntigravityTranscriptStep` | `Content` | mapped | `content_part.bucket`, `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `tool_call_result_text.text`, `tool_call_result_size.utf8_bytes`, `tool_call_result_truncation.original_utf8_bytes` | Measured via `ToolContentMeasurer` into `user_message`, `conversation_history`, or paired single-call `tool_result_content` (JSON documents withheld). |
| `AntigravityTranscriptStep` | `ToolCalls` | mapped | nested: AntigravityTranscriptToolCall | Tool invocations emitted as child `tool.invoke` records under the `PLANNER_RESPONSE` parent. |
| `AntigravityTranscriptToolCall` | `CallId` | mapped | `tool_call.call_id` | Explicit `call_id`/`id` or deterministic `step-<index>-call-<ordinal>` correlation identifier. |
| `AntigravityTranscriptToolCall` | `ToolName` | mapped | `tool_call.tool_name`, `tool_call_mcp.server_name`, `tool_call_mcp.server_tool_name` | Invoked tool name (`mcp__<server>__<tool>` splits into `tool_call_mcp`). |
| `AntigravityTranscriptToolCall` | `RawArgumentsJson` | mapped | `tool_call_input_size.utf8_bytes`, `tool_call_input_size.characters`, `tool_call_input_fingerprint.sha256` | Decoded arguments JSON object measured via `ToolContentMeasurer` with raw JSON withheld from `tool_call_input_text`. |
| `AntigravityTranscriptToolCall` | `InferredSkillName` | mapped | `record_skill_invocation.skill_name` | Parent directory skill name inferred exclusively from `view_file` targeting `SKILL.md`. |
| `AntigravityTranscriptSegment` | `Steps` | mapped | nested: AntigravityTranscriptStep | Transcript steps owned by one `agy-step:<last_step_index>` call segment. |
| `AntigravityTranscriptSegment` | `Covered` | dropped | — | Transient segment-coverage boolean gating whether transcript activity is attributed (`SegmentTranscript`) rather than stored in SQLite. |
| `AntigravityTranscriptSegment` | `Result` | mapped | `tool_call_result_text.text`, `tool_call_outcome.succeeded` | Paired `GENERIC` output attached when a segment holds exactly one tool call. |
| `AntigravityStatusLineEntry` | `Timestamp` | mapped | `record.started_at_utc` | Snapshot timestamp (`at` or `timestamp`, falling back to file mtime or import UTC). |
| `AntigravityStatusLineEntry` | `ConversationId` | mapped | `session.session_id`, `session.native_session_id`, `record_session_link.session_key`, `record.span_id` | Primary session and deterministic `synth:antigravity-statusline:<conversationId>:...` record anchor (JSON objects withheld). |
| `AntigravityStatusLineEntry` | `SessionId` | dropped | — | Optional secondary `session_id` / `sessionId` is redundant with `ConversationId` and has no separate column in `RelationalSchemaCatalog`. |
| `AntigravityStatusLineEntry` | `Model` | mapped | `record_model.model_id`, `record.name` | Resolved model identifier from scalar `model` or `model.id`/`model.display_name` (rejecting `"unknown"` and JSON objects). |
| `AntigravityStatusLineEntry` | `ModelDisplayName` | dropped | — | Human-readable `model.display_name` is used only as fallback when `model.id` is absent and is not persisted separately. |
| `AntigravityStatusLineEntry` | `InputTokens` | mapped | `record_token_count.token_count`, `record_reported_total.token_count` | Billable or delta input token count (`token_class = input`). |
| `AntigravityStatusLineEntry` | `OutputTokens` | mapped | `record_token_count.token_count`, `record_reported_total.token_count` | Aggregate billable or delta output token count (`token_class = output`). |
| `AntigravityStatusLineEntry` | `CacheCreationInputTokens` | mapped | `record_token_count.token_count`, `record_reported_total.token_count` | Billable or delta cache-write token count (`token_class = cache_write`). |
| `AntigravityStatusLineEntry` | `CacheReadInputTokens` | mapped | `record_token_count.token_count`, `record_reported_total.token_count` | Billable or delta cache-read token count (`token_class = cache_read`). |
| `AntigravityStatusLineEntry` | `ContextWindowSize` | dropped | — | Unmapped statusline `context_window.context_window_size` telemetry; context limits are resolved from model pricing metadata. |
| `AntigravityStatusLineEntry` | `Cwd` | dropped | — | Unmapped workspace path `cwd` on raw statusline payloads is intentionally withheld and never persisted. |
| `AntigravityParseProblem` | `LineNumber` | mapped | `ingest_problem_location.location`, `ingest_problem.problem_key` | 1-based JSONL line number recorded in non-fatal parse diagnostics. |
| `AntigravityParseProblem` | `Code` | mapped | `ingest_problem.code` | Diagnostic code (`ANTIGRAVITY_MALFORMED_LINE`). |
| `AntigravityParseProblem` | `Message` | mapped | `ingest_problem.message` | Sanitized parse failure message containing no raw JSON payload. |

## gemini

`GeminiAdapter` decodes `OtlpSpan` / `RawSpan` payloads carrying `gen_ai.system = "gemini"` (without Antigravity or Copilot markers) and maps `gemini.usage.thoughts_tokens`, though `CanonSynth.ExcludedHarnessIds` and `CanonPricing.IsExcludedHarnessIdentity` exclude `gemini` from persisted coding-harness storage.

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `OtlpSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace correlation identifier on decoded spans. |
| `OtlpSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span key (or quarantined when excluded as `excluded_harness`). |
| `OtlpSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier. |
| `OtlpSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `OtlpSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `OtlpSpan` | `StartTimeUnixNano` | mapped | `record.started_at_utc` | Converted to ISO-8601 UTC `Timestamp`; raw nanosecond string is not persisted separately. |
| `OtlpSpan` | `EndTimeUnixNano` | mapped | `record_end.ended_at_utc` | End instant used for duration calculation and `record_end`. |
| `OtlpSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | ISO-8601 UTC start timestamp. |
| `OtlpSpan` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Rounded to non-negative integer milliseconds. |
| `OtlpSpan` | `Status` | mapped | nested: OtlpStatus | Normalized span status and error message. |
| `OtlpSpan` | `Attributes` | mapped | nested: RawSpan | Routed to `RawSpan.Attributes` for `GeminiAdapter` extraction. |
| `OtlpSpan` | `Resource` | mapped | `source.locator`, `quarantined_span_source.source` | `service.name` populates `source.locator`; remaining resource attributes are dropped after attribution. |
| `OtlpSpan` | `Scope` | mapped | nested: OtlpScope | Decoded instrumentation scope; dropped from relational persistence. |
| `OtlpStatus` | `Code` | mapped | `record.status` | Normalized lowercase status (`unset`, `ok`, `error`). |
| `OtlpStatus` | `Message` | mapped | `record_error.message` | Persisted when status indicates an error. |
| `OtlpScope` | `Name` | dropped | — | Instrumentation scope name has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpScope` | `Version` | dropped | — | Instrumentation scope version has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpLog` | `LogId` | dropped | — | Legacy `pending_logs`/`quarantined_logs`/`enriched_logs` tables were removed per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `TraceId` | mapped | `record_trace.trace_id`, `ingest_problem_span.span_id` | Trace correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SpanId` | mapped | `record.span_id`, `ingest_problem_span.span_id` | Span correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SessionId` | mapped | `record_session_link.session_key`, `ingest_problem_session.session_id` | Session correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `Timestamp` | mapped | `record.started_at_utc`, `ingest_problem_timestamp.timestamp_utc` | Log event timestamp mapped on collector DTOs or diagnostics. |
| `OtlpLog` | `Body` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `ingest_problem.message` | Mapped on receipt into typed collector parts or `ingest_problem`; raw payload is discarded per DD-10. |
| `OtlpLog` | `Attributes` | dropped | — | Unstructured OTLP log `attributes_json` is dropped per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `Resource` | mapped | `source.locator` | `service.name` identifies source; remaining `resource_json` is dropped per DD-10. |
| `OtlpLog` | `Scope` | dropped | — | OTLP log instrumentation scope is not persisted in `RelationalSchemaCatalog` per DD-10. |
| `OtlpMetric` | `Name` | mapped | `metric_point.name` | Metric instrument name. |
| `OtlpMetric` | `Description` | dropped | — | Human-readable metric description has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `Unit` | mapped | `metric_point_unit.unit` | Metric unit string when present. |
| `OtlpMetric` | `Kind` | mapped | `metric_point.kind` | Metric kind (`gauge`, `sum`, `histogram`, etc.). |
| `OtlpMetric` | `AggregationTemporality` | mapped | `metric_point_temporality.aggregation_temporality` | OTLP temporality enum integer. |
| `OtlpMetric` | `IsMonotonic` | dropped | — | Sum monotonicity flag has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `DataPoints` | mapped | nested: OtlpMetricDataPoint | Normalized data points persisted under `metric_point.*`. |
| `OtlpMetric` | `Resource` | mapped | `metric_point_source.source` | `service.name` populates `metric_point_source.source`; `resource_json` was removed per `LegacyTableReplacementMap`. |
| `OtlpMetric` | `Scope` | dropped | — | `scope_json` on `metric_point` was removed per `LegacyTableReplacementMap` because it was written and never read. |
| `OtlpMetricDataPoint` | `Attributes` | dropped | — | `attributes_json` on `metric_point` was removed per `LegacyTableReplacementMap`; attributes fold into `metric_point.point_key`. |
| `OtlpMetricDataPoint` | `TimeUnixNano` | mapped | `metric_point_time.time_unix_nano`, `metric_point_timestamp.timestamp_utc` | Observation timestamp in nanoseconds and UTC ISO string. |
| `OtlpMetricDataPoint` | `StartTimeUnixNano` | mapped | `metric_point_start_time.start_time_unix_nano` | Cumulative window start timestamp in nanoseconds. |
| `OtlpMetricDataPoint` | `Value` | mapped | `metric_point_value.value` | Gauge/sum scalar value. |
| `OtlpMetricDataPoint` | `Count` | mapped | `metric_point_count.point_count` | Histogram observation count. |
| `OtlpMetricDataPoint` | `Sum` | mapped | `metric_point_sum.point_sum` | Histogram observation sum. |
| `RawSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span identifier. |
| `RawSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace identifier when present. |
| `RawSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier when present. |
| `RawSpan` | `Source` | mapped | `source.locator`, `quarantined_span_source.source` | Emitting `service.name` (or `otlp` fallback). |
| `RawSpan` | `Attributes` | mapped | `record_model.model_id`, `record_session_link.session_key`, `record_token_count.token_count`, `content_part_text.part_text` | `GeminiAdapter` extracts inclusive `gen_ai.*` usage, `gemini.usage.thoughts_tokens`, and canonical parts; `gemini` records are excluded from coding-harness storage. |
| `RawSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `RawSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `RawSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | Optional ISO start timestamp. |

## pi

Pi ingests from two paths: local Pi (`.pi/agent/sessions`) and Oh My Pi (`.omp/agent/sessions`) JSONL session transcripts via `PiMapper` (mapping `PiDocument`, `PiEntry`, `PiMessage`, `PiUsage`, `PiContentBlock`, `PiToolCall`, and `PiParseProblem` into `CollectedBatch`) and over OTLP through `PiAdapter` (`OtlpSpan`, `OtlpLog`, `OtlpMetric`, `RawSpan`), extracting `pi.tools.selected`, `pi.tools.snippets`, `pi.skills`, and `pi.context_files` into content buckets alongside exclusive usage counters. Pi OTLP spans are mapped to collector DTOs by `PiSpanMapper` (`dash/server/KyberDash.Data/Harnesses/Otlp/PiSpanMapper.cs`): usage-bearing spans map to `llm.invoke` with the cache-exclusive convention, `gen_ai.tool.name` spans map to `tool.invoke`, and `invoke_agent` spans map to `agent.invoke`; `pi.tools.selected`/`pi.tools.snippets` map to `tool_definitions` parts and `pi.skills`/`pi.context_files` map to `instruction_context` parts; and Gemini-attributed spans (`gen_ai.system == "gemini"` or any `gemini.*` attribute) are excluded so they yield zero stored records.

In `PiMapper`, each JSONL line maps to `CollectedSession`, `CollectedRecord` (`llm.invoke` parent turns and child `tool.invoke` spans), `CollectedContentPart`, `CollectedToolCall`, `CollectedCost`, `CollectedRecordSignals`, and `CollectedProblem`:

- `PiDocument` / `PiEntry`: `SessionId` (`session.id`, falling back to transcript file stem) maps to `session.session_id`, `session.native_session_id`, and `record_session_link.session_key`; `WorkingDirectory` (`session.cwd`) maps to `session_workspace.working_directory`; `Title` (OMP `title` entry) maps to `session_title.title`; `Timestamp` (`entry.timestamp`, falling back to pending user `entry.timestamp` / `attribution.timestamp`, then `session.timestamp`) maps to `record.started_at_utc` (assistant turns without any timestamp are dropped); `Id` and `EntryType` govern anchor identity (`record.span_id`) and dispatch (`session`, `title`, `model_change`, `message`), while unrecognized JSONL entry types are ignored per the Pi/OMP JSONL format.
- `PiMessage`: `ResponseId` (with `PiEntry.Id`, `PiEntry.Timestamp`, and line number fallbacks) seeds deterministic `record.span_id` (`synth:<harness>:<session>:<anchor>`) and per-file deduplication; `Role` maps to `content_part_role.role_name` (`user` / `assistant`) and gates `CanonFindings.DetectUserCorrection` (`record_correction_rule.correction_rule`); `Model` resolves against `model_change` (`gpt-5` fallback) into `record_model.model_id` and `record.name`; `AttributionTimestampMs` supplies Unix-millisecond timestamp fallback on `user` messages.
- `PiUsage`: `InputTokens` (`usage.input`), `OutputTokens` (`usage.output`), `CacheReadTokens` (`usage.cacheRead`), and `CacheWriteTokens` (`usage.cacheWrite`) map conditionally to `record_token_count.token_count` (`input`, `output`, `cache_read`, `cache_write`) and `record_reported_total.token_count` (`input` sum and `output`), skipping zero-token turns (`input == 0 && output == 0`); `ReportedCostUsd` (`usage.cost.total`) maps to `record_reported_cost.amount` (`currency = "USD"`) when strictly positive and finite, treating zero/absent costs as unreported so rate-table estimation applies.
- `PiContentBlock` / `PiToolCall`: `text` blocks (and string `content`) map through `ToolContentMeasurer` to `content_part_text.part_text` or `content_part_withheld` (`user_message` and `conversation_history` buckets); `toolCall` blocks normalize tool names (`bash -> Bash`, `read -> Read`, `edit -> Edit`, `write -> Write`, `glob -> Glob`, `grep -> Grep`, `task`/`dispatch_agent -> Agent`, `fetch -> WebFetch`, `search -> WebSearch`, `todo -> TodoWrite`, `patch -> Patch`), reclassify `read` calls whose `Path` or `FilePath` targets `SKILL.md` or `skill://<name>` into `record_skill_invocation.skill_name` and `tool_call.tool_name = "Skill"`, and measure `RawInputText` via `ToolContentMeasurer` into child `tool.invoke` spans (`tool_call.call_id`, `tool_call_input_*`); `Command` is parsed on `PiToolCall` DTOs and measured inside `RawInputText` without a separate column.
- `PiParseProblem`: `LineNumber`, `Code` (`PI_MALFORMED_LINE`), and `Message` map to `ingest_problem.problem_key`, `ingest_problem_location.location`, `ingest_problem.code`, and `ingest_problem.message` (`warning`).

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `OtlpSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace correlation identifier. |
| `OtlpSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span key (or quarantined span key). |
| `OtlpSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier. |
| `OtlpSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `OtlpSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `OtlpSpan` | `StartTimeUnixNano` | mapped | `record.started_at_utc` | Converted to ISO-8601 UTC `Timestamp`; raw nanosecond string is not persisted separately. |
| `OtlpSpan` | `EndTimeUnixNano` | mapped | `record_end.ended_at_utc` | End instant used for duration calculation and `record_end`. |
| `OtlpSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | ISO-8601 UTC start timestamp. |
| `OtlpSpan` | `DurationMs` | mapped | `record_reported_duration.duration_ms` | Rounded to non-negative integer milliseconds. |
| `OtlpSpan` | `Status` | mapped | nested: OtlpStatus | Normalized span status and error message. |
| `OtlpSpan` | `Attributes` | mapped | nested: RawSpan | Routed to `RawSpan.Attributes` for `PiAdapter` extraction. |
| `OtlpSpan` | `Resource` | mapped | `source.locator`, `quarantined_span_source.source` | `service.name` populates `source.locator`; remaining resource attributes are dropped after attribution. |
| `OtlpSpan` | `Scope` | mapped | nested: OtlpScope | Decoded instrumentation scope; dropped from relational persistence. |
| `OtlpStatus` | `Code` | mapped | `record.status` | Normalized lowercase status (`unset`, `ok`, `error`). |
| `OtlpStatus` | `Message` | mapped | `record_error.message` | Persisted when status indicates an error. |
| `OtlpScope` | `Name` | dropped | — | Instrumentation scope name has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpScope` | `Version` | dropped | — | Instrumentation scope version has no normalized column in `RelationalSchemaCatalog`. |
| `OtlpLog` | `LogId` | dropped | — | Legacy `pending_logs`/`quarantined_logs`/`enriched_logs` tables were removed per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `TraceId` | mapped | `record_trace.trace_id`, `ingest_problem_span.span_id` | Trace correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SpanId` | mapped | `record.span_id`, `ingest_problem_span.span_id` | Span correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `SessionId` | mapped | `record_session_link.session_key`, `ingest_problem_session.session_id` | Session correlation when mapped to collector DTOs or diagnostics. |
| `OtlpLog` | `Timestamp` | mapped | `record.started_at_utc`, `ingest_problem_timestamp.timestamp_utc` | Log event timestamp mapped on collector DTOs or diagnostics. |
| `OtlpLog` | `Body` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `ingest_problem.message` | Mapped on receipt into typed collector parts or `ingest_problem`; raw payload is discarded per DD-10. |
| `OtlpLog` | `Attributes` | dropped | — | Unstructured OTLP log `attributes_json` is dropped per DD-10 (`LegacyTableReplacementMap`). |
| `OtlpLog` | `Resource` | mapped | `source.locator` | `service.name` identifies source; remaining `resource_json` is dropped per DD-10. |
| `OtlpLog` | `Scope` | dropped | — | OTLP log instrumentation scope is not persisted in `RelationalSchemaCatalog` per DD-10. |
| `OtlpMetric` | `Name` | mapped | `metric_point.name` | Metric instrument name. |
| `OtlpMetric` | `Description` | dropped | — | Human-readable metric description has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `Unit` | mapped | `metric_point_unit.unit` | Metric unit string when present. |
| `OtlpMetric` | `Kind` | mapped | `metric_point.kind` | Metric kind (`gauge`, `sum`, `histogram`, etc.). |
| `OtlpMetric` | `AggregationTemporality` | mapped | `metric_point_temporality.aggregation_temporality` | OTLP temporality enum integer. |
| `OtlpMetric` | `IsMonotonic` | dropped | — | Sum monotonicity flag has no column in `metric_point` or its child tables. |
| `OtlpMetric` | `DataPoints` | mapped | nested: OtlpMetricDataPoint | Normalized data points persisted under `metric_point.*`. |
| `OtlpMetric` | `Resource` | mapped | `metric_point_source.source` | `service.name` populates `metric_point_source.source`; `resource_json` was removed per `LegacyTableReplacementMap`. |
| `OtlpMetric` | `Scope` | dropped | — | `scope_json` on `metric_point` was removed per `LegacyTableReplacementMap` because it was written and never read. |
| `OtlpMetricDataPoint` | `Attributes` | dropped | — | `attributes_json` on `metric_point` was removed per `LegacyTableReplacementMap`; attributes fold into `metric_point.point_key`. |
| `OtlpMetricDataPoint` | `TimeUnixNano` | mapped | `metric_point_time.time_unix_nano`, `metric_point_timestamp.timestamp_utc` | Observation timestamp in nanoseconds and UTC ISO string. |
| `OtlpMetricDataPoint` | `StartTimeUnixNano` | mapped | `metric_point_start_time.start_time_unix_nano` | Cumulative window start timestamp in nanoseconds. |
| `OtlpMetricDataPoint` | `Value` | mapped | `metric_point_value.value` | Gauge/sum scalar value. |
| `OtlpMetricDataPoint` | `Count` | mapped | `metric_point_count.point_count` | Histogram observation count. |
| `OtlpMetricDataPoint` | `Sum` | mapped | `metric_point_sum.point_sum` | Histogram observation sum. |
| `RawSpan` | `SpanId` | mapped | `record.span_id`, `quarantined_span.span_id` | Primary span identifier. |
| `RawSpan` | `TraceId` | mapped | `record_trace.trace_id` | Trace identifier when present. |
| `RawSpan` | `ParentSpanId` | mapped | `record_parent.parent_span_id` | Parent span identifier when present. |
| `RawSpan` | `Source` | mapped | `source.locator`, `quarantined_span_source.source` | Emitting `service.name` (or `otlp` fallback). |
| `RawSpan` | `Attributes` | mapped | `record_model.model_id`, `record_session_link.session_key`, `record_token_count.token_count`, `content_part_text.part_text` | `PiAdapter` extracts `pi.tools.selected`, `pi.tools.snippets`, `pi.skills`, `pi.context_files`, and exclusive `gen_ai.usage.*` counters; unmapped/PII keys are dropped. |
| `RawSpan` | `Name` | mapped | `record.name`, `quarantined_span_name.name` | Original span name. |
| `RawSpan` | `Kind` | mapped | `record.kind` | Normalized OTLP span kind. |
| `RawSpan` | `Timestamp` | mapped | `record.started_at_utc`, `quarantined_span_timestamp.timestamp_utc` | Optional ISO start timestamp. |

## cursor

Cursor ingests from two offline surfaces via `CursorMapper` into `CollectedBatch`: a JSON export of `cursorDiskKV` bubble rows (`CursorBubbleDocument` over `CursorBubbleRow`, mirroring the `BUBBLE_QUERY_BASE` projection in `cursor.ts`) and `cursor-agent` transcripts (`CursorTranscriptDocument` over `CursorTranscriptTurn`, covering both the JSONL and legacy text grammars). Every bubble row and transcript turn carrying usage emits one usage-bearing `llm.invoke` record (`RecordKind.Internal`); units attributed to the `cursor-agent` alias normalize to `cursor` via `NormalizeHarnessName`. Live-SQLite-only derivations from `cursor.ts` (workspace attribution map, composer meter and stream input, agent-stream tool attachment, results cache, paged scans, and cost estimation) have no offline input and are not persisted; neither surface reports cost, so published rates apply at read time.

In `CursorMapper`, each bubble row maps to a `CollectedRecord` with composer session linkage, per-side usage (harness-reported counts, estimated from text length at four characters per token when both are zero), and paired user text; zero-usage rows, timestamp-less rows, and multiline tool-call sub-composer keys are skipped. Each transcript turn maps to a `CollectedRecord` with estimated input, output, and reasoning tokens and measured content parts; transcripts with no recognizable turns emit an `ingest_problem` row instead of records.

| Native type | Property | Disposition | Target mapping | Reason |
|---|---|---|---|---|
| `CursorBubbleRow` | `BubbleKey` | mapped | `record.span_id` | Row key seeds deterministic `synth` record identity under the composer session. |
| `CursorBubbleRow` | `InputTokens` | mapped | `record_token_count.token_count`, `record_reported_total.token_count` | Harness-reported input tokens; absent or zero falls back to text-length estimation for `record_token_count` only. |
| `CursorBubbleRow` | `OutputTokens` | mapped | `record_token_count.token_count`, `record_reported_total.token_count` | Harness-reported output tokens; absent or zero falls back to text-length estimation for `record_token_count` only. |
| `CursorBubbleRow` | `Model` | mapped | `record_model.model_id` | Raw model name with a `cursor-auto` display fallback when null or `default`; cost-model resolution is read-time. |
| `CursorBubbleRow` | `CreatedAt` | mapped | `record.started_at_utc` | ISO-8601 UTC bubble timestamp; rows without a parseable timestamp are skipped. |
| `CursorBubbleRow` | `RequestId` | mapped | `record.span_id` | Request anchor for deterministic record identity. |
| `CursorBubbleRow` | `UserText` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `content_part_role.role_name`, `record_correction.span_id`, `record_correction_rule.correction_rule` | Leading text excerpt measured through `ToolContentMeasurer` and scanned for user-correction rules. |
| `CursorBubbleRow` | `TextLength` | mapped | `record_token_count.token_count` | Full text length driving the token estimate when both native counts are zero; zero-length rows emit no record. |
| `CursorBubbleRow` | `BubbleType` | mapped | `content_part.bucket` | Value `1` selects the user-turn pairing path; all other values select the assistant path. |
| `CursorBubbleRow` | `ContextWindow` | dropped | — | Runtime-reported context window resolves through the model catalog at projection time rather than per-turn storage. |
| `CursorBubbleRow` | `CodeBlocks` | dropped | — | Language tags fold into the unstructured per-call tool list, which is dropped with `Tools` for lack of call ids. |
| `CursorBubbleDocument` | `Rows` | mapped | nested: CursorBubbleRow | Bubble rows in export order. |
| `CursorBubbleDocument` | `ParseProblems` | mapped | nested: CursorParseProblem | Non-fatal row-level parse diagnostics emitted as `ingest_problem` rows. |
| `CursorParseProblem` | `LineNumber` | mapped | `ingest_problem_location.location`, `ingest_problem.problem_key` | 1-based row number, or 0 for document-level failures, appended to problem key and locator. |
| `CursorParseProblem` | `Code` | mapped | `ingest_problem.code` | Diagnostic code for the failure. |
| `CursorParseProblem` | `Message` | mapped | `ingest_problem.message` | Sanitized parse failure message. |
| `CursorTranscriptTurn` | `UserMessage` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `content_part_role.role_name`, `record_correction.span_id`, `record_correction_rule.correction_rule` | User prompt measured through `ToolContentMeasurer` and scanned for user-correction rules. |
| `CursorTranscriptTurn` | `Body` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `content_part_role.role_name` | Assistant reply measured through `ToolContentMeasurer`. |
| `CursorTranscriptTurn` | `Reasoning` | mapped | `content_part_text.part_text`, `content_part_withheld.utf8_bytes`, `content_part_role.role_name` | Thinking lines measured through `ToolContentMeasurer` into the reasoning bucket. |
| `CursorTranscriptTurn` | `Tools` | dropped | — | Unstructured per-turn tool names carry no call ids, inputs, or results, so no `tool_call` rows are emitted. |
| `CursorTranscriptDocument` | `ConversationId` | mapped | `session.session_id`, `session.native_session_id`, `record_session_link.session_key` | UUID transcript stem, or a 16-character digest of the transcript path. |
| `CursorTranscriptDocument` | `Turns` | mapped | nested: CursorTranscriptTurn | Parsed user and assistant turns in transcript order. |
| `CursorTranscriptDocument` | `Recognized` | dropped | — | Transient parse-success flag; unrecognized transcripts emit an `ingest_problem` row instead of records. |
| `CursorTranscriptDocument` | `ParseProblems` | mapped | nested: CursorParseProblem | Non-fatal line-level parse diagnostics emitted as `ingest_problem` rows. |

## zcode

`zcode` is **unevidenced** in the C# KyberDash codebase: no native reader type, `TranscriptReaderRepository` parser, OTLP `IHarnessAdapter`, or hook wire mapper exists for ZCode (only the `"zcode"` string appears in `CanonSynth.BuildProviderConventions`, and ZCode collection remains deferred in [Telemetry inventory](telemetry-inventory.md) under issue #214). There are no native reader properties to map or drop until a native ZCode collector is implemented.

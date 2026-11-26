using KyberDash.Data.Collector;
using KyberDash.Domain;
using KyberDash.Domain.Schema;

namespace KyberDash.Contracts.Repositories;

/// <summary>Direction of a harness-reported token total (<c>record_reported_total.direction</c>).</summary>
public enum ReportedTotalDirection
{
    /// <summary>Input token total (<c>input</c>).</summary>
    Input,

    /// <summary>Output token total (<c>output</c>).</summary>
    Output,
}

/// <summary>Persisted telemetry source row (<c>source</c> table).</summary>
/// <param name="SourceId">Primary key of the source (<c>source.source_id</c>).</param>
/// <param name="Source">Static source metadata.</param>
public record StoredSource(string SourceId, CollectedSource Source);

/// <summary>Persisted record token count entry (<c>record_token_count</c>).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="TokenClass">Token accounting bucket.</param>
/// <param name="Count">Token count.</param>
public record StoredTokenCount(string SpanId, TokenClass TokenClass, long Count);

/// <summary>Persisted harness-reported token total (<c>record_reported_total</c>).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="Direction">Reported direction (<c>input</c> or <c>output</c>).</param>
/// <param name="Count">Reported token count.</param>
public record StoredReportedTotal(string SpanId, ReportedTotalDirection Direction, long Count);

/// <summary>Persisted harness-reported monetary cost (<c>record_reported_cost</c>).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="Amount">Monetary amount.</param>
/// <param name="Currency">ISO currency code.</param>
public record StoredReportedCost(string SpanId, double Amount, string Currency);

/// <summary>Persisted skill invocation row (<c>record_skill_invocation</c>).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="SkillName">Invoked skill name.</param>
public record StoredSkillInvocation(string SpanId, string SkillName);

/// <summary>Persisted skill listing row (<c>record_skill_listing</c>) with the session turn that carried it.</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="SkillName">Listed skill name.</param>
/// <param name="TurnIndex">
/// Zero-based turn position of the record in the session's projection (<c>session_turn.position</c>), or
/// <see langword="null"/> when the record is not one of the session's projected turns.
/// </param>
public record StoredSkillListing(string SpanId, string SkillName, int? TurnIndex);

/// <summary>Persisted tool call attached to a record (<c>tool_call</c> and child extension tables).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="Call">Static tool call details.</param>
public record StoredToolCall(string SpanId, CollectedToolCall Call);

/// <summary>
/// A tool call that may be a KW skill load (<c>tool_call</c>, its MCP split, the first 1024
/// characters of its reply and its record's start time). The reader only narrows to candidate
/// names; the application decides which candidates are loads and what skill each names.
/// </summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="ToolName">Tool name as the harness recorded it (<c>tool_call.tool_name</c>).</param>
/// <param name="McpServerName">MCP server name when the call was split (<c>tool_call_mcp.server_name</c>).</param>
/// <param name="McpToolName">MCP tool name when the call was split (<c>tool_call_mcp.server_tool_name</c>).</param>
/// <param name="ReplyHead">The reply's first 1024 characters (<c>tool_call_result_text.text</c>), or <see langword="null"/> when no reply was captured. The call's input is never read.</param>
/// <param name="StartedAtUtc">The owning record's start time (<c>record.started_at_utc</c>).</param>
public record StoredKwSkillLoad(
    string SpanId,
    string ToolName,
    string? McpServerName,
    string? McpToolName,
    string? ReplyHead,
    DateTimeOffset StartedAtUtc);

/// <summary>Persisted content part attached to a record (<c>content_part</c> and child extension tables).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="Part">Static content part details.</param>
public record StoredContentPart(string SpanId, CollectedContentPart Part);

/// <summary>Persisted tool definition attached to a record (<c>tool_definition</c> and <c>tool_definition_server</c>).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="Definition">Static tool definition details.</param>
public record StoredToolDefinition(string SpanId, CollectedToolDefinition Definition);

/// <summary>Persisted agent invocation attached to a record (<c>agent_invocation</c> and child extension tables).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="Invocation">Static agent invocation details.</param>
public record StoredAgentInvocation(string SpanId, CollectedAgentInvocation Invocation);

/// <summary>Persisted diagnostic signals attached to a record (<c>record_error</c>, <c>record_edit_failure</c>, etc.).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="Signals">Static record signals.</param>
public record StoredRecordSignals(string SpanId, CollectedRecordSignals Signals);

/// <summary>Persisted per-model token usage snapshot attached to a session (<c>session_model_usage</c>).</summary>
/// <param name="SessionId">Owning session identifier.</param>
/// <param name="Usage">Static model usage snapshot.</param>
public record StoredSessionModelUsage(string SessionId, CollectedModelUsage Usage);

/// <summary>Per-session-key, per-harness record attribution over <c>record</c> joined to <c>record_session_link</c>.</summary>
/// <param name="SessionKey">Linked canonical session key (<c>record_session_link.session_key</c>).</param>
/// <param name="HarnessId">Raw harness identifier of the attributed records (<c>record.harness_id</c>).</param>
/// <param name="RecordCount">Number of records attributed to the session key and harness.</param>
public record StoredSessionAttribution(string SessionKey, string HarnessId, int RecordCount);

/// <summary>One request's prompt preview, as <see cref="IRelationalStoreReader.RequestPromptPreviews"/> returns it.</summary>
/// <param name="RequestSpanId">Request span identifier the preview answers.</param>
/// <param name="Prompt">First 200 characters of the request's plain prompt content, or <see langword="null"/> when the span carries none.</param>
public record StoredRequestPromptPreview(string RequestSpanId, string? Prompt);

/// <summary>Strongly typed cell value from a catalog-driven stored-fields inspection.</summary>
/// <param name="ColumnName">Exact catalog column name.</param>
/// <param name="ColumnType">SQLite storage affinity of the column.</param>
/// <param name="TextValue">Text value when <paramref name="ColumnType"/> is <see cref="SqliteColumnType.SqliteText"/>.</param>
/// <param name="IntegerValue">Integer value when <paramref name="ColumnType"/> is <see cref="SqliteColumnType.SqliteInteger"/>.</param>
/// <param name="RealValue">Real value when <paramref name="ColumnType"/> is <see cref="SqliteColumnType.SqliteReal"/>.</param>
public record StoredFieldCell(
    string ColumnName,
    SqliteColumnType ColumnType,
    string? TextValue = null,
    long? IntegerValue = null,
    double? RealValue = null);

/// <summary>One row of typed cells returned in <see cref="StoredTableRows"/>.</summary>
/// <param name="Cells">Ordered cells matching the owning table's <see cref="StoredTableRows.ColumnNames"/>.</param>
public record StoredFieldRow(IReadOnlyList<StoredFieldCell> Cells);

/// <summary>
/// Content-free inventory entry for one catalog table scoped to a session: the table's name, its
/// catalog columns, the count of the session's scoped rows, and the parent keys absent from an
/// optional child table — never a row value (decision D15).
/// </summary>
/// <param name="TableName">Exact table name from <see cref="RelationalSchemaCatalog"/>.</param>
/// <param name="ColumnNames">Ordered column names declared by the catalog table.</param>
/// <param name="RowCount">Rows belonging to the inspected session.</param>
/// <param name="MissingParentKeys">
/// Parent primary keys present in the session that have no row in this optional child table.
/// </param>
public record StoredTableInventory(
    string TableName,
    IReadOnlyList<string> ColumnNames,
    long RowCount,
    IReadOnlyList<string> MissingParentKeys);

/// <summary>
/// Content-free stored-fields inventory across every catalog table for a single session. The
/// stored-fields section loads it at mount; a table's rows load through
/// <see cref="IRelationalStoreReader.StoredFieldsRows"/> when the viewer opens that table
/// (decision D15).
/// </summary>
/// <param name="SessionKey">Inspected session key.</param>
/// <param name="Tables">Per-table inventory entries in catalog order.</param>
public record StoredFieldsInventoryView(
    string SessionKey,
    IReadOnlyList<StoredTableInventory> Tables);

/// <summary>
/// One page of a single catalog table's stored rows for a session, loaded when the viewer opens
/// that table and limited in SQL (decision D15).
/// </summary>
/// <param name="TableName">Exact table name from <see cref="RelationalSchemaCatalog"/>.</param>
/// <param name="ColumnNames">Ordered column names declared by the catalog table.</param>
/// <param name="TotalRows">Rows belonging to the inspected session, across every page.</param>
/// <param name="Page">One-based page number.</param>
/// <param name="Limit">Rows on the page.</param>
/// <param name="Offset">Zero-based row offset of this page.</param>
/// <param name="Rows">The page's rows, each carrying the table's typed cells in catalog order.</param>
public record StoredTableRows(
    string TableName,
    IReadOnlyList<string> ColumnNames,
    long TotalRows,
    int Page,
    int Limit,
    int Offset,
    IReadOnlyList<StoredFieldRow> Rows);

/// <summary>Per-model monetary subtotal inside a <see cref="ReadTimeCostSummary"/>.</summary>
/// <param name="Model">Canonical or reported model identifier.</param>
/// <param name="Value">Monetary subtotal attributed to <paramref name="Model"/>.</param>
public record ReadTimeModelCost(string Model, double Value);

/// <summary>Read-time monetary cost summary grouped by billing basis and currency (<c>DD-13</c>).</summary>
/// <param name="Basis">Cost provenance basis (never blended across bases).</param>
/// <param name="Status">Pricing completeness status.</param>
/// <param name="Value">Total monetary cost when finite, or <see langword="null"/> when unavailable.</param>
/// <param name="Currency">ISO currency code when unambiguous, or <see langword="null"/>.</param>
/// <param name="ByModel">Per-model cost breakdown ordered deterministically.</param>
public record ReadTimeCostSummary(
    CostBasis Basis,
    CostStatus Status,
    double? Value,
    string? Currency,
    IReadOnlyList<ReadTimeModelCost> ByModel);

/// <summary>Total monetary amount observed for a single billing basis (<c>DD-13</c>).</summary>
/// <param name="Basis">Wire cost basis name (such as <c>"harness"</c> or <c>"published"</c>).</param>
/// <param name="Total">Summed finite monetary total under <paramref name="Basis"/>, or <see langword="null"/>.</param>
public record ReadTimeBasisTotal(string Basis, double? Total);

/// <summary>Structured verdict emitted when valued turn costs span incompatible billing bases.</summary>
/// <param name="Code">Machine-readable mismatch code (<c>COST_BASIS_MISMATCH</c>).</param>
/// <param name="Message">Human-readable mismatch summary.</param>
/// <param name="Bases">Sorted distinct cost basis wire identifiers observed in the session.</param>
/// <param name="TotalsByBasis">Non-blended monetary totals partitioned by basis.</param>
public record ReadTimeCostBasisMismatch(
    string Code,
    string Message,
    IReadOnlyList<string> Bases,
    IReadOnlyList<ReadTimeBasisTotal> TotalsByBasis);

/// <summary>Read-time tool residency and invocation rollup for one session tool (<c>DD-13</c>).</summary>
/// <param name="Name">Tool name.</param>
/// <param name="Server">Optional owning MCP server name.</param>
/// <param name="SchemaTokens">Per-turn schema token footprint.</param>
/// <param name="Invocations">Number of tool invocations in the session.</param>
/// <param name="TurnsResident">Number of turns in which this tool schema was resident.</param>
/// <param name="Errors">Count of failed tool invocations.</param>
/// <param name="TotalSchemaCost">Cumulative schema token-turns cost.</param>
/// <param name="Invoked">Whether the tool was invoked at least once.</param>
public record ReadTimeSessionToolSummary(
    string Name,
    string? Server,
    long SchemaTokens,
    int Invocations,
    int TurnsResident,
    int Errors,
    long TotalSchemaCost,
    bool Invoked);

/// <summary>Read-time MCP server schema and unused-tool rollup for a session (<c>DD-13</c>).</summary>
/// <param name="Server">Server identifier.</param>
/// <param name="IsMcp">Whether this server is an MCP server.</param>
/// <param name="Tools">Total advertised tools attributed to the server.</param>
/// <param name="SchemaTokens">Single-turn sum of tool schema tokens for this server.</param>
/// <param name="Invocations">Total invocations across this server's tools.</param>
/// <param name="UnusedTools">Count of advertised tools with zero invocations.</param>
/// <param name="UnusedCost">Total schema residency cost across uninvoked tools on this server.</param>
public record ReadTimeSessionServerSummary(
    string Server,
    bool IsMcp,
    int Tools,
    long SchemaTokens,
    int Invocations,
    int UnusedTools,
    long UnusedCost);

/// <summary>Read-time request summary derived from request/turn structure and plain-text content (<c>DD-13</c>).</summary>
/// <param name="RequestSpanId">Root request span identifier.</param>
/// <param name="TimestampUtc">UTC start timestamp of the request span.</param>
/// <param name="Turns">Number of turns belonging to this request.</param>
/// <param name="Model">Resolved model identifier for the request, or <see langword="null"/>.</param>
/// <param name="PromptPreview">First 200 characters of plain user prompt content, or <see langword="null"/> when absent or withheld.</param>
public record ReadTimeSessionRequestSummary(
    string RequestSpanId,
    string TimestampUtc,
    int Turns,
    string? Model,
    string? PromptPreview);

/// <summary>Read-time availability flags indicating whether schema and context projections exist (<c>DD-13</c>).</summary>
/// <param name="SchemaMeasurable">Whether <c>session_schema</c> is persisted for the session.</param>
/// <param name="ContextMeasurable">Whether <c>session_context</c> is persisted for the session.</param>
public record ReadTimeSessionCoverage(bool SchemaMeasurable, bool ContextMeasurable);

/// <summary>Read-time comparison between harness-reported totals and summed per-class token counts (<c>DD-13</c>).</summary>
/// <param name="RequestSpanId">Turn span identifier being reconciled.</param>
/// <param name="RootInput">Reported input total (<c>record_reported_total</c>), or <see langword="null"/> when unreported.</param>
/// <param name="SumChatInput">Sum of per-class input tokens (<c>input + cache_read + cache_write</c>), or <see langword="null"/>.</param>
/// <param name="InputMatch">Whether <paramref name="RootInput"/> equals <paramref name="SumChatInput"/>, or <see langword="null"/> when either is absent.</param>
/// <param name="RootOutput">Reported output total (<c>record_reported_total</c>), or <see langword="null"/> when unreported.</param>
/// <param name="SumChatOutput">Output token count (<c>output</c>), or <see langword="null"/> when unreported.</param>
/// <param name="OutputMatch">Whether <paramref name="RootOutput"/> equals <paramref name="SumChatOutput"/>, or <see langword="null"/> when either is absent.</param>
public record ReadTimeTurnReconciliation(
    string RequestSpanId,
    long? RootInput,
    long? SumChatInput,
    bool? InputMatch,
    long? RootOutput,
    long? SumChatOutput,
    bool? OutputMatch);

/// <summary>Token count attributed to a named context bucket on a turn (<c>DD-13</c>).</summary>
/// <param name="Bucket">Canonical context bucket name.</param>
/// <param name="Tokens">Attributed token count.</param>
public record ReadTimeContextBucketTokens(string Bucket, long Tokens);

/// <summary>First or last turn bucket breakdown alongside optional reported input total (<c>DD-13</c>).</summary>
/// <param name="Buckets">Ordered per-bucket token counts.</param>
/// <param name="ReportedInput">Harness-reported input total for the turn when present.</param>
public record ReadTimeContextBucketSummary(
    IReadOnlyList<ReadTimeContextBucketTokens> Buckets,
    long? ReportedInput);

/// <summary>Read-time rollup of auxiliary spans in a session with non-blended reported vs. published costs (<c>DD-13</c>).</summary>
/// <param name="SpanIds">Ordered auxiliary span identifiers.</param>
/// <param name="ReportedCost">Harness-reported auxiliary cost when recorded, or <see langword="null"/>.</param>
/// <param name="PublishedCost">Published-table cost computed across auxiliary spans.</param>
public record ReadTimeAuxiliarySummary(
    IReadOnlyList<string> SpanIds,
    ReadTimeCostSummary? ReportedCost,
    ReadTimeCostSummary PublishedCost);

/// <summary>Read-time per-turn telemetry and pricing summary (<c>DD-13</c>, <c>OD-3</c>).</summary>
/// <param name="Position">Zero-based turn index within the session.</param>
/// <param name="SpanId">Turn span identifier.</param>
/// <param name="TimestampUtc">UTC start timestamp of the turn.</param>
/// <param name="Model">Model identifier reported for the turn, or <see langword="null"/>.</param>
/// <param name="InputTokens">Combined input tokens (<c>input + cache_read + cache_write</c>), or <see langword="null"/> when unrecorded.</param>
/// <param name="OutputTokens">Generated output tokens, or <see langword="null"/> when unrecorded.</param>
/// <param name="FreshInputTokens">Fresh non-cached input tokens, or <see langword="null"/> when unrecorded.</param>
/// <param name="CacheReadTokens">Cache-read input tokens, or <see langword="null"/> when unrecorded.</param>
/// <param name="CacheCreationTokens">Cache-creation input tokens, or <see langword="null"/> when unrecorded.</param>
/// <param name="ReasoningTokens">Reasoning tokens, or <see langword="null"/> when unrecorded.</param>
/// <param name="DurationMs">Turn duration in milliseconds, or <see langword="null"/> when absent.</param>
/// <param name="ReportedCost">Harness-reported cost for the turn when present, or <see langword="null"/>.</param>
/// <param name="PublishedCost">Published-rate cost calculated for the turn.</param>
public record ReadTimeTurnSummary(
    int Position,
    string SpanId,
    string TimestampUtc,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    long? FreshInputTokens,
    long? CacheReadTokens,
    long? CacheCreationTokens,
    long? ReasoningTokens,
    long? DurationMs,
    ReadTimeCostSummary? ReportedCost,
    ReadTimeCostSummary PublishedCost);

/// <summary>Full read-time session aggregate computed from normalized relational tables (<c>DD-13</c>, <c>OD-3</c>).</summary>
/// <param name="SessionKey">Canonical session key.</param>
/// <param name="HarnessId">Attributed harness identifier.</param>
/// <param name="TurnCount">Number of turns in the session.</param>
/// <param name="RequestCount">Number of top-level requests in the session.</param>
/// <param name="TotalInput">Sum of combined input tokens across turns, or <see langword="null"/> when absent.</param>
/// <param name="TotalOutput">Sum of output tokens across turns, or <see langword="null"/> when absent.</param>
/// <param name="TotalCacheRead">Sum of cache-read tokens across turns, or <see langword="null"/> when absent.</param>
/// <param name="TotalCacheCreation">Sum of cache-creation tokens across turns, or <see langword="null"/> when absent.</param>
/// <param name="DurationMs">Session duration in milliseconds, or <see langword="null"/> when unavailable.</param>
/// <param name="Models">Distinct models observed across session turns in turn order.</param>
/// <param name="ReportedCost">Non-blended harness-reported cost summary when present, or <see langword="null"/>.</param>
/// <param name="PublishedCost">Non-blended published-rate cost summary computed across turns.</param>
/// <param name="CostBasisMismatch">Structured basis mismatch verdict when both valued harness and published costs occur.</param>
/// <param name="CacheHitRatio">Cache-read share of total input (<c>TotalCacheRead / TotalInput</c>), or <see langword="null"/>.</param>
/// <param name="CacheCreationCoverage">Count of turns writing prompt cache when applicable, or <see langword="null"/>.</param>
/// <param name="ToolCalls">Count of tool calls, or <see langword="null"/> when zero/absent per <c>OD-3</c>.</param>
/// <param name="ToolsInvoked">Distinct invoked tool names in insertion order.</param>
/// <param name="HasToolDefinitions">Whether at least one tool definition is persisted for the session.</param>
/// <param name="ToolsOffered">Distinct advertised tool names in insertion order.</param>
/// <param name="Tools">Per-tool residency and invocation summaries.</param>
/// <param name="Servers">Per-server MCP schema summaries.</param>
/// <param name="Requests">Ordered request summaries.</param>
/// <param name="Coverage">Schema and context projection measurability flags.</param>
/// <param name="Problems">Ingest and pricing diagnostics associated with the session.</param>
/// <param name="Reconciliation">Per-turn reported vs. tokenized input/output reconciliation entries.</param>
/// <param name="Subagents">Subagent invocations associated with the session.</param>
/// <param name="Auxiliary">Auxiliary span rollup and non-blended cost summaries.</param>
/// <param name="FirstContextBucket">First projected context turn bucket summary when present.</param>
/// <param name="LastContextBucket">Last projected context turn bucket summary when present.</param>
/// <param name="UnusedRange">Cost or residency bounds for uninvoked schemas when measurable, or <see langword="null"/>.</param>
public record SessionAggregateView(
    string SessionKey,
    string HarnessId,
    int TurnCount,
    int RequestCount,
    long? TotalInput,
    long? TotalOutput,
    long? TotalCacheRead,
    long? TotalCacheCreation,
    long? DurationMs,
    IReadOnlyList<string> Models,
    ReadTimeCostSummary? ReportedCost,
    ReadTimeCostSummary PublishedCost,
    ReadTimeCostBasisMismatch? CostBasisMismatch,
    double? CacheHitRatio,
    int? CacheCreationCoverage,
    int? ToolCalls,
    IReadOnlyList<string> ToolsInvoked,
    bool HasToolDefinitions,
    IReadOnlyList<string> ToolsOffered,
    IReadOnlyList<ReadTimeSessionToolSummary> Tools,
    IReadOnlyList<ReadTimeSessionServerSummary> Servers,
    IReadOnlyList<ReadTimeSessionRequestSummary> Requests,
    ReadTimeSessionCoverage Coverage,
    IReadOnlyList<Problem> Problems,
    IReadOnlyList<ReadTimeTurnReconciliation> Reconciliation,
    IReadOnlyList<SubagentSession> Subagents,
    ReadTimeAuxiliarySummary Auxiliary,
    ReadTimeContextBucketSummary? FirstContextBucket,
    ReadTimeContextBucketSummary? LastContextBucket,
    UnusedSchemaRange? UnusedRange);

/// <summary>Count of persisted records attributed to one telemetry source.</summary>
/// <param name="SourceId">Registered source identifier (<c>record.source_id</c>).</param>
/// <param name="Count">Number of records that source contributed.</param>
public record StoredSourceCount(string SourceId, int Count);

/// <summary>
/// Read-time figures the sessions list renders, derived in one content-free pass over the
/// same scalar columns the retired broad session-aggregate read priced (<c>DD-13</c>).
/// </summary>
/// <param name="SessionKey">Canonical session key.</param>
/// <param name="HarnessId">Harness identifier of the session row.</param>
/// <param name="Repo">Workspace repository name, or <see langword="null"/> when unrecorded.</param>
/// <param name="Branch">Workspace branch name, or <see langword="null"/> when unrecorded.</param>
/// <param name="TurnCount">Number of turns in the session.</param>
/// <param name="RequestCount">Number of top-level requests in the session.</param>
/// <param name="TotalInput">Sum of combined input tokens across turns, or <see langword="null"/> when no turn reported tokens — absent, never zero.</param>
/// <param name="TotalOutput">Sum of output tokens across turns, or <see langword="null"/> when absent.</param>
/// <param name="ReportedCost">Non-blended harness-reported cost summary when present, or <see langword="null"/>.</param>
/// <param name="PublishedCost">Non-blended published-rate cost summary computed across turns.</param>
/// <param name="CostBasisMismatch">Structured basis mismatch verdict when both valued harness and published costs occur.</param>
/// <param name="Models">Distinct models observed across session turns in turn order.</param>
/// <param name="ProblemsCount">Count of ingest and pricing diagnostics associated with the session.</param>
/// <param name="ReportedTotalTokens">One session total the harness reported with no input/output split (for example Warp), or <see langword="null"/>. When set, <paramref name="TotalInput"/> and <paramref name="TotalOutput"/> stay <see langword="null"/>: they are not measured, never derived from this total.</param>
/// <remarks>
/// Carries no content: no <c>content_part</c>, tool-call text, tool-definition, or
/// agent-invocation column is read to build it, so a sessions list never materializes
/// conversation text regardless of store size.
/// </remarks>
public record SessionListFigure(
    string SessionKey,
    string HarnessId,
    string? Repo,
    string? Branch,
    int TurnCount,
    int RequestCount,
    long? TotalInput,
    long? TotalOutput,
    ReadTimeCostSummary? ReportedCost,
    ReadTimeCostSummary PublishedCost,
    ReadTimeCostBasisMismatch? CostBasisMismatch,
    IReadOnlyList<string> Models,
    int ProblemsCount,
    long? ReportedTotalTokens = null);

/// <summary>
/// Read-time figures the bounded session summary page renders, derived from content-free record
/// scalars, the stored session projection, and ingest diagnostics (<c>DD-13</c>).
/// </summary>
/// <param name="SessionKey">Canonical session key.</param>
/// <param name="HarnessId">Harness identifier of the session row.</param>
/// <param name="TurnCount">Number of turns in the session.</param>
/// <param name="RequestCount">Number of top-level requests in the session.</param>
/// <param name="TotalInput">Sum of combined input tokens across turns, or <see langword="null"/> when no turn reported tokens.</param>
/// <param name="TotalOutput">Sum of output tokens across turns, or <see langword="null"/> when absent.</param>
/// <param name="TotalCacheRead">Sum of cache-read tokens across turns, or <see langword="null"/> when absent.</param>
/// <param name="TotalCacheCreation">Sum of cache-creation tokens across turns, or <see langword="null"/> when absent.</param>
/// <param name="DurationMs">Session duration in milliseconds, or <see langword="null"/> when unavailable.</param>
/// <param name="Models">Distinct models observed across session turns in turn order.</param>
/// <param name="CacheHitRatio">Cache-read share of total input, or <see langword="null"/>.</param>
/// <param name="CacheCreationCoverage">Count of turns writing prompt cache, or <see langword="null"/>.</param>
/// <param name="ToolCalls">Count of tool calls, or <see langword="null"/> when zero or unrecorded.</param>
/// <param name="ToolsInvoked">Distinct invoked tool names in projection order.</param>
/// <param name="HasToolDefinitions">Whether the session projection ranked at least one tool schema.</param>
/// <param name="ToolsOffered">Distinct advertised tool names in projection order.</param>
/// <param name="ReportedCost">Non-blended harness-reported cost summary when present, or <see langword="null"/>.</param>
/// <param name="PublishedCost">Non-blended published-rate cost summary computed across turns.</param>
/// <param name="CostBasisMismatch">Structured basis mismatch verdict when both valued harness and published costs occur.</param>
/// <param name="Coverage">Schema and context projection measurability flags.</param>
/// <param name="Problems">Ingest and pricing diagnostics associated with the session.</param>
/// <param name="SkillInvocations">Skill names the session's records invoked, in record order — names only, never conversation text.</param>
/// <param name="ToolRowsCount">Count of per-tool residency and invocation rows the tool sections render.</param>
/// <param name="UnmeasuredInputTurns">Count of turns that carry no combined input token count.</param>
/// <param name="ReconciliationTotal">Count of turns whose reported and tokenized input and output totals are both measurable.</param>
/// <param name="ReconciliationMismatched">Count of measurable reconciliation turns where either reported total disagrees with the tokenized one.</param>
/// <param name="ReportedTotalTokens">One session total the harness reported with no input/output split (for example Warp), or <see langword="null"/>. When set, the input, output and cache totals stay <see langword="null"/>: they are not measured, never derived from this total.</param>
/// <remarks>
/// Carries no content: no <c>content_part</c>, tool-call text, tool-definition, or
/// agent-invocation column is read to build it. The figures that would need those columns —
/// per-tool schema residency and request prompt previews — belong to the sections that read
/// those columns, not this summary.
/// </remarks>
public record SessionSummaryFigure(
    string SessionKey,
    string HarnessId,
    int TurnCount,
    int RequestCount,
    long? TotalInput,
    long? TotalOutput,
    long? TotalCacheRead,
    long? TotalCacheCreation,
    long? DurationMs,
    IReadOnlyList<string> Models,
    double? CacheHitRatio,
    int? CacheCreationCoverage,
    int? ToolCalls,
    IReadOnlyList<string> ToolsInvoked,
    bool HasToolDefinitions,
    IReadOnlyList<string> ToolsOffered,
    ReadTimeCostSummary? ReportedCost,
    ReadTimeCostSummary PublishedCost,
    ReadTimeCostBasisMismatch? CostBasisMismatch,
    ReadTimeSessionCoverage Coverage,
    IReadOnlyList<Problem> Problems,
    IReadOnlyList<StoredSkillInvocation> SkillInvocations,
    int ToolRowsCount = 0,
    int UnmeasuredInputTurns = 0,
    int ReconciliationTotal = 0,
    int ReconciliationMismatched = 0,
    long? ReportedTotalTokens = null);

/// <summary>
/// Read-time figures the report prices one scoped session with: exactly the fields the report
/// reads from <see cref="SessionSummaryFigure"/>, and no more (<c>DD-13</c>, decision D13).
/// </summary>
/// <param name="SessionKey">Canonical session key.</param>
/// <param name="TotalInput">Sum of combined input tokens across turns, or <see langword="null"/> when no turn reported tokens.</param>
/// <param name="TotalCacheRead">Sum of cache-read tokens across turns, or <see langword="null"/> when absent.</param>
/// <param name="ToolsOffered">Distinct advertised tool names, in record order.</param>
/// <param name="ToolsInvoked">Distinct invoked tool names, in record order.</param>
/// <param name="ReportedCost">Non-blended harness-reported cost summary when present, or <see langword="null"/>.</param>
/// <param name="PublishedCost">Non-blended published-rate cost summary computed across turns.</param>
/// <remarks>
/// Carries no content, like the summary figures it projects: the report window prices stored
/// token, tool, and cost columns only.
/// </remarks>
public record SessionReportFigure(
    string SessionKey,
    long? TotalInput,
    long? TotalCacheRead,
    IReadOnlyList<string> ToolsOffered,
    IReadOnlyList<string> ToolsInvoked,
    ReadTimeCostSummary? ReportedCost,
    ReadTimeCostSummary PublishedCost)
{
    /// <summary>Projects the report figures a summary already carries.</summary>
    /// <param name="summary">The summary figures to project from.</param>
    public SessionReportFigure(SessionSummaryFigure summary)
        : this(
            summary.SessionKey,
            summary.TotalInput,
            summary.TotalCacheRead,
            summary.ToolsOffered,
            summary.ToolsInvoked,
            summary.ReportedCost,
            summary.PublishedCost)
    {
    }
}

/// <summary>
/// The per-turn figures of one session together with its first and last context edges
/// (<c>DD-13</c>).
/// </summary>
/// <param name="Turns">Per-turn figures in turn order.</param>
/// <param name="FirstContextBucket">First projected context turn bucket summary when present.</param>
/// <param name="LastContextBucket">Last projected context turn bucket summary when present.</param>
/// <remarks>
/// Content-free like the figures it groups: the turn list prices the records' token and cost
/// columns, and the context edges price the stored projection's first and last context turn plus
/// the matching record's reported input. The charts section and the run page build their payloads
/// from this set, so neither pays the tools and requests sections' rows.
/// </remarks>
public record SessionTurnFigureSet(
    IReadOnlyList<ReadTimeTurnSummary> Turns,
    ReadTimeContextBucketSummary? FirstContextBucket,
    ReadTimeContextBucketSummary? LastContextBucket);

/// <summary>One page of a session's request rows, read over <c>session_request</c> and turn ownership only.</summary>
/// <param name="Items">The page's request rows, in the requested request order. Prompt previews are always <see langword="null"/> here; the scoped <see cref="IRelationalStoreReader.RequestPromptPreviews"/> read supplies them.</param>
/// <param name="Total">Number of request rows the session holds, before paging.</param>
public record SessionRequestFigurePageResult(IReadOnlyList<ReadTimeSessionRequestSummary> Items, int Total);

/// <summary>One page of a session's reconciliation rows, read over the turns' reported and tokenized totals only.</summary>
/// <param name="Items">The page's measurable rows — both matches present — in the requested turn order.</param>
/// <param name="Total">Number of measurable reconciliation rows the session holds, before paging.</param>
public record SessionReconciliationFigurePageResult(IReadOnlyList<ReadTimeTurnReconciliation> Items, int Total);

/// <summary>One page of a session's stored ingest problems (<c>ingest_problem</c>), in the requested order with timestamped problems first.</summary>
/// <param name="Items">The page's problems, in the requested order.</param>
/// <param name="Total">Number of stored ingest problems attached to the session, before paging.</param>
public record SessionProblemPageResult(IReadOnlyList<Problem> Items, int Total);

/// <summary>One page of a session's KW skill loads (D17), in the requested call order.</summary>
/// <param name="Items">The page's load rows, in the requested order.</param>
/// <param name="Total">Number of KW skill loads the session holds, before paging.</param>
public record SessionKwSkillLoadPageResult(IReadOnlyList<StoredKwSkillLoad> Items, int Total);

/// <summary>
/// One session's tool and server section figures with the aggregates the tools section carries
/// page-independently (<c>DD-13</c>).
/// </summary>
/// <param name="Tools">Per-tool residency and invocation rows in ranking order — ranked schema rows first, never-invoked rows included.</param>
/// <param name="Servers">Per-server schema and invocation rollups in projection order.</param>
/// <param name="ToolsOffered">Distinct advertised tool names in definition order — the rows' <c>in_definitions</c> input.</param>
/// <param name="UnusedRange">Cost or residency bounds for uninvoked schemas when measurable, or <see langword="null"/>.</param>
/// <param name="ByServer">
/// Per-server resident cost totals — the stored schema's rollup when measurable, else the tool
/// rows' own rollup priced per server — one row per server, largest total first.
/// </param>/// <param name="MaxCost">Largest measured resident cost over the tool rows, or <see langword="null"/> when no row measured one.</param>
/// <param name="ResidentCostTotal">Resident cost summed over every tool row, page-independently.</param>
/// <param name="InvocationCount">Tool invocations the session's records carry.</param>
/// <param name="UnusedCount">Tool rows that were never invoked.</param>
/// <param name="DefinitionsSpanId">
/// The first span, in record order, whose record carries a tool definition — the span the tool
/// inspector opens its definition content at — or <see langword="null"/> when the session
/// exported none.
/// </param>
/// <param name="SchemaMeasurable">
/// Whether the stored session projection carries a schema — the measurability verdict the
/// summary payload states. A session can hold invocation rows and still not be measurable when
/// the harness exported no tool definitions.
/// </param>
/// <param name="SchemaUnmeasurableReason">
/// The stored projection's reason schema could not be measured, or <see langword="null"/> when
/// the schema is measurable.
/// </param>
/// <remarks>
/// Prices tool-call and tool-definition metadata and the stored schema projection only: no
/// content part, no content text, no timeline, and no context read. The session harness and the
/// session's first turn model are the two identity columns the unused-range pricing resolves.
/// </remarks>
public record SessionToolFigureSet(
    IReadOnlyList<ReadTimeSessionToolSummary> Tools,
    IReadOnlyList<ReadTimeSessionServerSummary> Servers,
    IReadOnlyList<string> ToolsOffered,
    UnusedSchemaRange? UnusedRange,
    IReadOnlyList<StoredSchemaServerTokens> ByServer,
    long? MaxCost,
    long ResidentCostTotal,
    int InvocationCount,
    int UnusedCount,
    string? DefinitionsSpanId,
    bool SchemaMeasurable = false,
    string? SchemaUnmeasurableReason = null);

/// <summary>
/// Typed read-only query contract over normalized telemetry, session, and record tables in
/// the relational KyberDash store.
/// </summary>
public interface IRelationalStoreReader
{
    /// <summary>Lists observed harness identifiers (<c>harness</c>).</summary>
    IReadOnlyList<string> Harnesses();

    /// <summary>Lists registered telemetry sources (<c>source</c>).</summary>
    /// <param name="harnessId">Optional harness filter.</param>
    IReadOnlyList<StoredSource> Sources(string? harnessId = null);

    /// <summary>Counts persisted telemetry records (<c>record</c>).</summary>
    /// <param name="harnessId">Optional harness filter.</param>
    int CountRecords(string? harnessId = null);

    /// <summary>
    /// Counts persisted telemetry records per source (<c>record</c>).
    /// </summary>
    /// <remarks>
    /// The coverage page shows a record count per source. Counting must never materialize the
    /// records, so this answers the count in the store rather than reading them back.
    /// </remarks>
    IReadOnlyList<StoredSourceCount> SourceRecordCounts();

    /// <summary>
    /// Lists the distinct session keys of <paramref name="harnessId"/> that hold at least one
    /// record whose stored source is named <paramref name="sourceName"/> — for an OTLP source,
    /// its locator (the service name).
    /// </summary>
    /// <remarks>
    /// The plugin-captured counter pass asks this once per scheduler tick for the sessions the
    /// Factory plugin captured, then reads only those sessions' transcript files. Ordering is
    /// newest record timestamp first, then session key, so the limit keeps the sessions a user
    /// most recently worked in. Existing indexes serve the query; the limit bounds every pass.
    /// </remarks>
    /// <param name="harnessId">Harness whose records are matched.</param>
    /// <param name="sourceName">Stored source locator to match.</param>
    /// <param name="limit">Positive maximum number of session keys returned.</param>
    /// <returns>Distinct session keys, newest record first, at most <paramref name="limit"/> rows.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="limit"/> is below 1.</exception>
    IReadOnlyList<string> SessionKeysWithRecordSource(string harnessId, string sourceName, int limit);

    /// <summary>
    /// Gets one record's metadata with no content-text column, the shape the span inspector reads.
    /// </summary>
    /// <param name="spanId">Record span identifier.</param>
    /// <returns>The record's content-free metadata, or <see langword="null"/> when absent.</returns>
    /// <remarks>
    /// The inspector prices attributes, tokens, cost, and signals, none of which is content. A
    /// captured prompt the inspector shows comes from <see cref="SpanPromptText"/>, so the
    /// attribute read itself never materializes a conversation. The read is scoped to the span:
    /// an adapter filters the record tables by that span, not by its session's records.
    /// </remarks>
    CollectedRecord? RecordContentFree(string spanId);

    /// <summary>
    /// Reads that span's first captured prompt part text — the consent-allowed prompt the
    /// span inspector shows — or <see langword="null"/> when the span carries none.
    /// </summary>
    /// <param name="spanId">Record span identifier.</param>
    /// <returns>
    /// The span's first non-empty <c>user_prompt</c>, <c>user_message</c>, or
    /// <c>conversation_history</c> part text in position order, or <see langword="null"/>
    /// when no such part text was captured. A prompt withheld for consent never becomes a
    /// part, so a consent-off span answers <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// One query scoped to the span over the content-part tables, so the inspector pays for
    /// one span's prompt text rather than its session's whole conversation.
    /// </remarks>
    string? SpanPromptText(string spanId);

    /// <summary>
    /// Lists per-session-key, per-harness record attributions — one grouped query over
    /// <c>record</c> joined to <c>record_session_link</c> — so a projection pass can plan
    /// identities, skip decisions, and per-session record budgets without materializing any
    /// record content.
    /// </summary>
    /// <remarks>
    /// Rows are ordered by session key then harness identifier (ordinal), the order
    /// attribution-driven session-id claiming depends on to keep assigned ids stable.
    /// </remarks>
    IReadOnlyList<StoredSessionAttribution> SessionAttributions();

    /// <summary>
    /// Queries the full records (<c>record</c> and child tables) linked to
    /// <paramref name="sessionKey"/>, the read a projection pass uses to rebuild that one
    /// session.
    /// </summary>
    /// <param name="sessionKey">Canonical session key to rebuild.</param>
    IReadOnlyList<CollectedRecord> SessionRecordsForRebuild(string sessionKey);

    /// <summary>
    /// Reads every stored record and child row in one whole-store query.
    /// </summary>
    /// <remarks>
    /// Reserved for maintenance paths only — the <c>kyber build</c> command, a wildcard
    /// projection rebuild, and doctor — and must never be called from a request path: on a
    /// large store this read is the very stall the purpose-named reads exist to remove.
    /// </remarks>
    IReadOnlyList<CollectedRecord> AllSessionRecordsForMaintenance();

    /// <summary>Queries per-class token counts (<c>record_token_count</c>).</summary>
    /// <param name="spanId">Optional record span identifier filter.</param>
    /// <param name="sessionKey">Optional session key filter.</param>
    IReadOnlyList<StoredTokenCount> TokenCounts(string? spanId = null, string? sessionKey = null);

    /// <summary>Queries harness-reported input/output token totals (<c>record_reported_total</c>).</summary>
    /// <param name="spanId">Optional record span identifier filter.</param>
    /// <param name="sessionKey">Optional session key filter.</param>
    IReadOnlyList<StoredReportedTotal> ReportedTotals(string? spanId = null, string? sessionKey = null);

    /// <summary>Queries harness-reported monetary costs (<c>record_reported_cost</c>).</summary>
    /// <param name="spanId">Optional record span identifier filter.</param>
    /// <param name="sessionKey">Optional session key filter.</param>
    IReadOnlyList<StoredReportedCost> ReportedCosts(string? spanId = null, string? sessionKey = null);

    /// <summary>Queries skill invocations (<c>record_skill_invocation</c>).</summary>
    /// <param name="spanId">Optional record span identifier filter.</param>
    /// <param name="sessionKey">Optional session key filter.</param>
    IReadOnlyList<StoredSkillInvocation> SkillsInvoked(string? spanId = null, string? sessionKey = null);

    /// <summary>Queries tool invocations (<c>tool_call</c> and child extension tables).</summary>
    /// <param name="spanId">Optional record span identifier filter.</param>
    /// <param name="sessionKey">Optional session key filter.</param>
    IReadOnlyList<StoredToolCall> ToolCalls(string? spanId = null, string? sessionKey = null);

    /// <summary>
    /// Reads the captured content parts of one turn or one request — the content view's single
    /// scoped read, and the only <c>content_part_text</c> read allowed on a request path.
    /// </summary>
    /// <param name="sessionKey">Canonical session key the turn belongs to.</param>
    /// <param name="turnIndex">
    /// Zero-based turn index to scope to, resolved exactly as <see cref="TurnFigures"/> resolves it;
    /// <see langword="null"/> reads every turn in the session.
    /// </param>
    /// <param name="spanId">Span identifier to scope to; it wins over <paramref name="turnIndex"/> when both are given.</param>
    /// <param name="part">Canonical part key to scope to — the stored bucket name — or <see langword="null"/> for every part.</param>
    /// <param name="budget">
    /// Maximum UTF-8 bytes of part text to materialize when <paramref name="full"/> is
    /// <see langword="false"/>; zero or less reads the whole scope.
    /// </param>
    /// <param name="full">Whether to read every part of the scope untruncated, ignoring <paramref name="budget"/>.</param>
    /// <returns>The scoped parts in span then position order.</returns>
    /// <remarks>
    /// Scoped by <c>(session_key, span, position)</c>, so the content view pays for the parts it
    /// renders and never for the session's whole conversation. Every other read model in this
    /// contract is content-free: list, summary, turn figures, timeline, coverage,
    /// findings, problems, quarantine, harnesses, runs, report, and analytics must not reach a
    /// content-text column, and none of them calls this member. The span inspector's
    /// <see cref="SpanPromptText"/> is the only other content-text read on a request path.
    /// </remarks>
    IReadOnlyList<StoredContentPart> TurnContentParts(
        string sessionKey,
        int? turnIndex = null,
        string? spanId = null,
        string? part = null,
        int budget = 0,
        bool full = false);

    /// <summary>
    /// Counts the scoped content parts without reading any part text — a content-free
    /// <c>COUNT(*)</c> over the scope <see cref="TurnContentParts"/> would materialize.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="turnIndex">Zero-based turn index scoping the read; it resolves through the turn figures the way the parts read's does.</param>
    /// <param name="spanId">Span identifier to scope to; it wins over <paramref name="turnIndex"/> when both are given.</param>
    /// <param name="part">Canonical part key to scope to — the stored bucket name — or <see langword="null"/> for every part.</param>
    /// <returns>The number of parts in the scope, or zero when the scope resolves to nothing.</returns>
    /// <remarks>
    /// The turn content route reads this beside its budgeted <see cref="TurnContentParts"/> call
    /// to decide its <c>truncated</c> flag: the budgeted read left content behind exactly when it
    /// returned fewer parts than the scope holds. Counting in SQL keeps that verdict free of any
    /// text transfer.
    /// </remarks>
    int TurnContentPartCount(
        string sessionKey,
        int? turnIndex = null,
        string? spanId = null,
        string? part = null);

    /// <summary>Queries advertised tool definitions (<c>tool_definition</c> and <c>tool_definition_server</c>).</summary>
    /// <param name="spanId">Optional record span identifier filter.</param>
    /// <param name="sessionKey">Optional session key filter.</param>
    IReadOnlyList<StoredToolDefinition> ToolDefinitions(string? spanId = null, string? sessionKey = null);

    /// <summary>Queries agent/subagent invocations (<c>agent_invocation</c> and child extension tables).</summary>
    /// <param name="spanId">Optional record span identifier filter.</param>
    /// <param name="sessionKey">Optional session key filter.</param>
    IReadOnlyList<StoredAgentInvocation> AgentInvocations(string? spanId = null, string? sessionKey = null);

    /// <summary>Queries diagnostic signals attached to records (<c>record_error</c>, <c>record_edit_failure</c>, <c>record_correction*</c>, <c>record_declared_context_window</c>).</summary>
    /// <param name="spanId">Optional record span identifier filter.</param>
    /// <param name="sessionKey">Optional session key filter.</param>
    IReadOnlyList<StoredRecordSignals> RecordSignals(string? spanId = null, string? sessionKey = null);

    /// <summary>Gets one normalized session by <paramref name="sessionKey"/>.</summary>
    /// <param name="sessionKey">Canonical session identifier.</param>
    CollectedSession? GetSession(string sessionKey);

    /// <summary>Queries normalized sessions (<c>session</c> and child tables).</summary>
    /// <param name="harnessId">Optional harness identifier filter.</param>
    IReadOnlyList<CollectedSession> Sessions(string? harnessId = null);

    /// <summary>Queries per-model session usage snapshots (<c>session_model_usage</c>).</summary>
    /// <param name="sessionKey">Optional session identifier filter.</param>
    IReadOnlyList<StoredSessionModelUsage> SessionModelUsages(string? sessionKey = null);

    /// <summary>
    /// Reads the content-free stored-fields inventory for a session: every catalog table's name,
    /// columns, scoped row count and missing parent keys, and never a row value (decision D15).
    /// </summary>
    /// <param name="sessionKey">Canonical session key to inspect.</param>
    /// <remarks>
    /// The stored-fields section loads this inventory at mount. Rows hold stored column values,
    /// including content text, so they load through <see cref="StoredFieldsRows"/> only when the
    /// viewer opens a table — the inventory itself reads counts and key columns alone.
    /// </remarks>
    StoredFieldsInventoryView StoredFieldsInventory(string sessionKey);

    /// <summary>
    /// Reads one page of a single catalog table's stored rows for a session, limited in SQL
    /// (decision D15).
    /// </summary>
    /// <param name="sessionKey">Canonical session key to inspect.</param>
    /// <param name="table">Exact catalog table name; the allowlist is the catalog itself.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>The page, or <see langword="null"/> when <paramref name="table"/> names no catalog table.</returns>
    /// <remarks>
    /// Rows hold stored column values, including content text, so this read answers only the
    /// named table's page — the cost is the page, never the session's store.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="page"/> or <paramref name="limit"/> is less than 1.
    /// </exception>
    StoredTableRows? StoredFieldsRows(string sessionKey, string table, int page, int limit);

    /// <summary>
    /// Lists the sessions-list read model, optionally filtered by <paramref name="harnessId"/>,
    /// reading no content column (<c>DD-13</c>).
    /// </summary>
    /// <param name="harnessId">Optional harness identifier filter over the session rows.</param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <remarks>
    /// One batched pass over the session rows and each session's linked record scalars, stored
    /// turns, and ingest problems — the figures the retired session-aggregate read answered
    /// per session, minus every content-bearing column. A session row with no linked records
    /// answers zeroed figures with absent token totals.
    /// </remarks>
    IReadOnlyList<SessionListFigure> SessionListFigures(string? harnessId = null, IModelPricingLookup? pricing = null);

    /// <summary>
    /// Reads the sessions-list figures for exactly <paramref name="sessionKeys"/>: the figures
    /// <see cref="SessionListFigures(string?, IModelPricingLookup?)"/> answers for those sessions,
    /// reading no content column (<c>DD-13</c>).
    /// </summary>
    /// <param name="sessionKeys">
    /// Canonical session keys to price. Order and duplicates are ignored; a key with no session row
    /// answers no figure.
    /// </param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>One figure per named session that has a session row, in session-row order.</returns>
    /// <remarks>
    /// A paged list prices only the keys on its page. Every statement this read issues filters by
    /// those keys — it never lists the store's sessions or projections — so its cost follows the
    /// page, not the store.
    /// </remarks>
    IReadOnlyList<SessionListFigure> SessionListFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null);

    /// <summary>
    /// Reads the bounded summary figures for one session over content-free record scalars only
    /// (<c>DD-13</c>).
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>The summary figures, or <see langword="null"/> when the session row is absent.</returns>
    SessionSummaryFigure? SessionSummaryFigures(string sessionKey, IModelPricingLookup? pricing = null);

    /// <summary>
    /// Reads the report figures for exactly <paramref name="sessionKeys"/>: the fields the report
    /// prices each scoped session with, from the same content-free summary computation as
    /// <see cref="SessionSummaryFigures(string, IModelPricingLookup?)"/> (<c>DD-13</c>, decision D13).
    /// </summary>
    /// <param name="sessionKeys">
    /// Canonical session keys to price. Duplicates are ignored; a key with no session row answers
    /// no figure.
    /// </param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>One figure per named session that has a session row, in session-row order.</returns>
    /// <remarks>
    /// One batched pass over only the named keys — the session rows, their content-free record
    /// scalars, and their stored turns — so the report's statement count never scales with its
    /// window. Every statement this read issues filters by those keys; it never lists the
    /// store's sessions or projections. No content column is read.
    /// </remarks>
    IReadOnlyList<SessionReportFigure> SessionReportFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null);

    /// <summary>
    /// Reads one turn's figures, scoped to <paramref name="turnIndex"/>, over content-free
    /// record scalars only (<c>DD-13</c>).
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="turnIndex">Zero-based turn index.</param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>The turn's figures, or <see langword="null"/> when the index is outside the session.</returns>
    ReadTimeTurnSummary? TurnFigures(string sessionKey, int turnIndex, IModelPricingLookup? pricing = null);

    /// <summary>
    /// Reads every turn's figures with the session's first and last context edges, over
    /// content-free record scalars and the stored context projection only (<c>DD-13</c>).
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>The turn figure set, or <see langword="null"/> when the session row is absent and no record is linked.</returns>
    /// <remarks>
    /// The charts section answers its whole payload from this read plus the stored context
    /// projection, and the run page prices a run's turns from it — so neither pays the tools and
    /// requests sections' rows, and neither materializes content.
    /// </remarks>
    SessionTurnFigureSet? SessionTurnFigures(string sessionKey, IModelPricingLookup? pricing = null);

    /// <summary>
    /// Pages one session's request rows in the requested order — the <c>session_request</c> rows
    /// when the session has them, else its root records — over request metadata and turn ownership
    /// only (<c>DD-13</c>).
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <param name="order">Sort order (<c>desc</c> newest first or <c>asc</c> oldest first).</param>
    /// <returns>The page's request rows and the session's request-row total.</returns>
    /// <remarks>
    /// The requests section answers its rows from this read plus
    /// <see cref="RequestPromptPreviews"/> for the page's spans, so the section never reads the
    /// tool figures' cost or schema rows, and never materializes content on its own.
    /// A session without a session row answers an empty page, as the figures the summary
    /// payload builds from do.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="order"/> is not <c>desc</c> or <c>asc</c>.</exception>
    SessionRequestFigurePageResult SessionRequestFigures(string sessionKey, int page, int limit, string order = "desc");

    /// <summary>
    /// Pages one session's measurable reconciliation rows — the turns whose reported and tokenized
    /// input and output totals are both present — over the turns' reported and tokenized totals
    /// only (<c>DD-13</c>), in requested turn order.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <param name="order">Sort order (<c>desc</c> newest first or <c>asc</c> oldest first).</param>
    /// <returns>The page's reconciliation rows and the session's measurable-row total.</returns>
    /// <remarks>
    /// The reconciliation section answers its rows from this read alone, so it never reads the
    /// tool figures' cost or schema rows. Unmeasurable rows — either match absent —
    /// are not part of the set, as today's section payload omits them.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="order"/> is not <c>desc</c> or <c>asc</c>.</exception>
    SessionReconciliationFigurePageResult SessionReconciliationFigures(string sessionKey, int page, int limit, string order = "desc");

    /// <summary>
    /// Pages one session's stored ingest problems (<c>DD-13</c>), attached by session or by one of
    /// the session's spans, ordering non-null timestamps first then null timestamps.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <param name="order">Sort order (<c>desc</c> newest first or <c>asc</c> oldest first).</param>
    /// <returns>The page's problems and the session's stored-problem total.</returns>
    /// <remarks>
    /// The problems section answers its rows from this read alone. A problem attached both by
    /// session and by span appears once, as today's figures deduplicate it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> or <paramref name="limit"/> is less than 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="order"/> is not <c>desc</c> or <c>asc</c>.</exception>
    SessionProblemPageResult SessionProblems(string sessionKey, int page, int limit, string order = "desc");

    /// <summary>
    /// Reads one session's tool and server section figures with the tools section's
    /// page-independent aggregates, over tool-call and tool-definition metadata and the stored
    /// schema projection only (<c>DD-13</c>).
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>
    /// The tool figures — ranked rows first, never-invoked rows included — or an empty set when
    /// the session has no linked records.
    /// </returns>
    /// <remarks>
    /// The tools section answers its rows and its aggregate from this read alone, so paging never
    /// re-reads: the aggregate fields (largest resident cost, resident cost total, invocation
    /// count, unused count, unused range, per-server rollup, and the definitions span) price every
    /// row before the engine pages. No content column is read and no turn, context, or projection
    /// row beyond the stored schema is touched; the session harness and first turn model are the
    /// two identity columns the unused-range pricing resolves.
    /// </remarks>
    SessionToolFigureSet SessionToolFigures(string sessionKey, IModelPricingLookup? pricing = null);

    /// <summary>
    /// Reads one session's stored skill invocations (<c>record_skill_invocation</c>) in record
    /// order — names only, never conversation text (<c>DD-13</c>).
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <returns>The skill invocation rows in record order; empty when the session invoked none.</returns>
    /// <remarks>
    /// The skills section pages the distinct rows this read returns, so a page of 25 skills never
    /// costs more than the session's stored skill rows. The statement scopes to the session's
    /// spans and touches the skill table alone.
    /// </remarks>
    IReadOnlyList<StoredSkillInvocation> SessionSkillFigures(string sessionKey);

    /// <summary>
    /// Reads the skills the harness listed to the agent across one session (<c>record_skill_listing</c>),
    /// in record and listing order, each with the projected turn that carried it — names only, never
    /// listing text.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <returns>The listing rows; empty when the harness recorded no listing for the session.</returns>
    IReadOnlyList<StoredSkillListing> SessionSkillListings(string sessionKey);

    /// <summary>
    /// Reads one page of the session's KW skill loads (D17), in requested timestamp/call order, with the total.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <param name="order">Sort order (<c>desc</c> newest first or <c>asc</c> oldest first).</param>
    /// <returns>
    /// The page's load rows — every tool call of the session whose MCP server, normalized (lower
    /// case, <c>_</c> to <c>-</c>), is <c>kw-docs</c> and whose MCP tool is <c>load_skill</c>, or
    /// whose tool name is one of the unsplit D17 spellings — with the owning record's start time,
    /// and the load count before paging. An empty page with a zero total when the session has none.
    /// </returns>
    /// <remarks>
    /// The statement scopes to the session's spans, carries the D17 load rule in SQL, and pages
    /// with <c>LIMIT</c>/<c>OFFSET</c>, so one page never reads the reply head of a load outside
    /// it. The recognizer in the application layer stays the final authority on the load and its
    /// skill; this read stays a plain row shape.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="order"/> is not <c>desc</c> or <c>asc</c>.</exception>
    SessionKwSkillLoadPageResult SessionKwSkillLoads(string sessionKey, int page, int limit, string order = "desc");

    /// <summary>
    /// Reads the prompt previews of exactly the named request spans, and no other span's parts.
    /// </summary>
    /// <param name="sessionKey">Canonical session key the spans belong to.</param>
    /// <param name="requestSpanIds">The page's request span ids, from <see cref="SessionRequestFigures"/>.</param>
    /// <returns>
    /// One entry per requested span id, in the order requested: the preview, or <see langword="null"/>
    /// when the span carries none.
    /// </returns>
    /// <remarks>
    /// The requests section's one sanctioned content read: a preview is the span's, or one of its
    /// owned turns', first non-empty prompt-bucket part, truncated the way the detail figures'
    /// preview rule truncates. Only the given spans' parts are read, so a page of 25 requests
    /// never materializes the session's whole conversation.
    /// </remarks>
    IReadOnlyList<StoredRequestPromptPreview> RequestPromptPreviews(string sessionKey, IReadOnlyCollection<string> requestSpanIds);

    /// <summary>
    /// Reads one session's record metadata with no content column, the shape the detail
    /// timeline hydrates from.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <returns>
    /// Linked records carrying token counts, models, timestamps, signals, tool-call metadata, and
    /// tool definitions, with no content part, tool input text, or tool result text.
    /// </returns>
    IReadOnlyList<CollectedRecord> SessionRecordsContentFree(string sessionKey);

    /// <summary>
    /// Reads one session's record metadata with no content column, shaped to the timeline tree
    /// <see cref="CanonContextAnalysis.BuildTimeline"/> builds: the span, parent, harness,
    /// timestamp, duration, kind, name, status, cost, model and declared-window columns the
    /// tree's nodes read, and no other child row.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <returns>
    /// Linked records carrying the columns the timeline tree's nodes read, with no token count,
    /// tool metadata, trace, end time, reported total, or content part.
    /// </returns>
    /// <remarks>
    /// The timeline section route builds the session's metadata tree once per page, so a page's
    /// cost is that tree, never the session's content (decision D17). The statement scopes to
    /// the session's spans and touches the record header tables alone.
    /// </remarks>
    IReadOnlyList<CollectedRecord> SessionTimelineRecords(string sessionKey);
}

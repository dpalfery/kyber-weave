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

/// <summary>Persisted tool call attached to a record (<c>tool_call</c> and child extension tables).</summary>
/// <param name="SpanId">Owning record span identifier.</param>
/// <param name="Call">Static tool call details.</param>
public record StoredToolCall(string SpanId, CollectedToolCall Call);

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

/// <summary>One row of typed cells returned in <see cref="StoredTableFields"/>.</summary>
/// <param name="Cells">Ordered cells matching the owning table's <see cref="StoredTableFields.ColumnNames"/>.</param>
public record StoredFieldRow(IReadOnlyList<StoredFieldCell> Cells);

/// <summary>
/// Catalog-driven inspection entry for a single relational table scoped to a session, including
/// column names, persisted rows, and parent keys absent from an optional child table.
/// </summary>
/// <param name="TableName">Exact table name from <see cref="RelationalSchemaCatalog"/>.</param>
/// <param name="ColumnNames">Ordered column names declared by the catalog table.</param>
/// <param name="Rows">Rows belonging to the inspected session.</param>
/// <param name="MissingParentKeys">
/// Parent primary keys present in the session that have no row in this optional child table.
/// </param>
public record StoredTableFields(
    string TableName,
    IReadOnlyList<string> ColumnNames,
    IReadOnlyList<StoredFieldRow> Rows,
    IReadOnlyList<string> MissingParentKeys);

/// <summary>
/// Catalog-driven relational field view across every applicable table for a single session.
/// </summary>
/// <param name="SessionKey">Inspected session key.</param>
/// <param name="Tables">Per-table field entries in catalog order.</param>
public record StoredFieldsView(
    string SessionKey,
    IReadOnlyList<StoredTableFields> Tables);

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
    int ProblemsCount);

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
/// <remarks>
/// Carries no content: no <c>content_part</c>, tool-call text, tool-definition, or
/// agent-invocation column is read to build it. The figures that would need those columns —
/// per-tool schema residency and request prompt previews — belong to the detail route's
/// content-bearing sections, not this summary.
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
    IReadOnlyList<StoredSkillInvocation> SkillInvocations);

/// <summary>
/// Read-time figures the session detail header renders, with the session's per-turn figures,
/// tool residency rows, and request metadata (<c>DD-13</c>).
/// </summary>
/// <param name="Summary">Bounded summary figures the header shows.</param>
/// <param name="Turns">Per-turn figures in turn order.</param>
/// <param name="Tools">Per-tool residency and invocation summaries in ranking order.</param>
/// <param name="Requests">Ordered request summaries. A request's prompt preview is content, so it stays absent here.</param>
/// <param name="Servers">Per-server schema and invocation rollups in projection order.</param>
/// <param name="Reconciliation">Per-turn reported vs. tokenized input/output reconciliation entries.</param>
/// <param name="FirstContextBucket">First projected context turn bucket summary when present.</param>
/// <param name="LastContextBucket">Last projected context turn bucket summary when present.</param>
/// <param name="UnusedRange">Cost or residency bounds for uninvoked schemas when measurable, or <see langword="null"/>.</param>
/// <remarks>
/// Content-free for the same reason as <see cref="SessionSummaryFigure"/>: the detail header, the
/// turn list, and the tool and request rows price stored metadata only.
/// </remarks>
public record SessionDetailFigure(
    SessionSummaryFigure Summary,
    IReadOnlyList<ReadTimeTurnSummary> Turns,
    IReadOnlyList<ReadTimeSessionToolSummary> Tools,
    IReadOnlyList<ReadTimeSessionRequestSummary> Requests,
    IReadOnlyList<ReadTimeSessionServerSummary> Servers,
    IReadOnlyList<ReadTimeTurnReconciliation> Reconciliation,
    ReadTimeContextBucketSummary? FirstContextBucket,
    ReadTimeContextBucketSummary? LastContextBucket,
    UnusedSchemaRange? UnusedRange);

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
    /// Gets one record's metadata with no content-text column, the shape the span inspector reads.
    /// </summary>
    /// <param name="spanId">Record span identifier.</param>
    /// <returns>The record's content-free metadata, or <see langword="null"/> when absent.</returns>
    /// <remarks>
    /// The inspector prices attributes, tokens, cost, and signals, none of which is content. A
    /// captured prompt the inspector shows comes from <see cref="TurnContentParts"/>, so reading
    /// a span attribute never materializes a conversation. The read is scoped to the span: an
    /// adapter filters the record tables by that span, not by its session's records.
    /// </remarks>
    CollectedRecord? RecordContentFree(string spanId);

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
    /// contract is content-free: list, detail, summary, turn figures, timeline, coverage,
    /// findings, problems, quarantine, harnesses, runs, report, and analytics must not reach a
    /// content-text column, and none of them calls this member.
    /// </remarks>
    IReadOnlyList<StoredContentPart> TurnContentParts(
        string sessionKey,
        int? turnIndex = null,
        string? spanId = null,
        string? part = null,
        int budget = 0,
        bool full = false);

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
    /// Returns a catalog-driven view of all persisted table columns, rows, and missing parent keys
    /// for <paramref name="sessionKey"/>.
    /// </summary>
    /// <param name="sessionKey">Canonical session key to inspect.</param>
    StoredFieldsView StoredFields(string sessionKey);

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
    /// Reads the bounded summary figures for one session over content-free record scalars only
    /// (<c>DD-13</c>).
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>The summary figures, or <see langword="null"/> when the session row is absent.</returns>
    SessionSummaryFigure? SessionSummaryFigures(string sessionKey, IModelPricingLookup? pricing = null);

    /// <summary>
    /// Reads the session detail header figures together with every turn's figures, over
    /// content-free record scalars only (<c>DD-13</c>).
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>The detail figures, or <see langword="null"/> when the session row is absent.</returns>
    SessionDetailFigure? SessionDetailFigures(string sessionKey, IModelPricingLookup? pricing = null);

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
    /// Reads one session's record metadata with no content column, the shape the detail
    /// timeline hydrates from.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <returns>
    /// Linked records carrying token counts, models, timestamps, signals, tool-call metadata, and
    /// tool definitions, with no content part, tool input text, or tool result text.
    /// </returns>
    IReadOnlyList<CollectedRecord> SessionRecordsContentFree(string sessionKey);
}

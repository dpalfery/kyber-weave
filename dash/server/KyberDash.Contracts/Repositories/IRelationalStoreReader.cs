using KyberDash.Data.Collector;
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

    /// <summary>Gets one normalized telemetry record by <paramref name="spanId"/>.</summary>
    /// <param name="spanId">Record span identifier.</param>
    CollectedRecord? GetRecord(string spanId);

    /// <summary>Queries normalized telemetry records (<c>record</c> and child tables).</summary>
    /// <param name="sessionKey">Optional session key filter.</param>
    /// <param name="harnessId">Optional harness identifier filter.</param>
    /// <param name="sourceId">Optional source identifier filter.</param>
    IReadOnlyList<CollectedRecord> Records(
        string? sessionKey = null,
        string? harnessId = null,
        string? sourceId = null);

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

    /// <summary>Queries ordered content parts (<c>content_part</c> and child extension tables).</summary>
    /// <param name="spanId">Optional record span identifier filter.</param>
    /// <param name="sessionKey">Optional session key filter.</param>
    IReadOnlyList<StoredContentPart> ContentParts(string? spanId = null, string? sessionKey = null);

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
}

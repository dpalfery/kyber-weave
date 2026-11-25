namespace KyberDash.Data.Collector;

/// <summary>Operation classification for a collected telemetry record (<c>record.op</c>).</summary>
public enum RecordOp
{
    /// <summary>Model / LLM invocation (<c>llm.invoke</c>).</summary>
    LlmInvoke,

    /// <summary>Tool invocation (<c>tool.invoke</c>).</summary>
    ToolInvoke,

    /// <summary>Subagent or agent invocation (<c>agent.invoke</c>).</summary>
    AgentInvoke,

    /// <summary>Unspecified operation (<c>unspecified</c>).</summary>
    Unspecified,
}

/// <summary>Span kind for a collected telemetry record (<c>record.kind</c>).</summary>
public enum RecordKind
{
    /// <summary>Unspecified span kind (<c>unspecified</c>).</summary>
    Unspecified,

    /// <summary>Internal span (<c>internal</c>).</summary>
    Internal,

    /// <summary>Server span (<c>server</c>).</summary>
    Server,

    /// <summary>Client span (<c>client</c>).</summary>
    Client,

    /// <summary>Producer span (<c>producer</c>).</summary>
    Producer,

    /// <summary>Consumer span (<c>consumer</c>).</summary>
    Consumer,
}

/// <summary>Completion status of a collected telemetry record (<c>record.status</c>).</summary>
public enum RecordStatus
{
    /// <summary>Status unset (<c>unset</c>).</summary>
    Unset,

    /// <summary>Operation succeeded (<c>ok</c>).</summary>
    Ok,

    /// <summary>Operation failed with an error (<c>error</c>).</summary>
    Error,

    /// <summary>Status unspecified (<c>unspecified</c>).</summary>
    Unspecified,
}

/// <summary>Token accounting bucket (<c>record_token_count.token_class</c>, <c>session_model_usage.token_class</c>).</summary>
public enum TokenClass
{
    /// <summary>Fresh input tokens (<c>input</c>).</summary>
    Input,

    /// <summary>Prompt-cache read tokens (<c>cache_read</c>).</summary>
    CacheRead,

    /// <summary>Prompt-cache creation/write tokens (<c>cache_write</c>).</summary>
    CacheWrite,

    /// <summary>Generated output tokens (<c>output</c>).</summary>
    Output,

    /// <summary>Reasoning / thinking tokens (<c>reasoning</c>).</summary>
    Reasoning,

    /// <summary>
    /// One reported total with no input/output split (<c>total</c>), stored for a session's reported
    /// usage only (<c>session_model_usage</c>): a harness that reports no split must never have its
    /// total recorded as input or output.
    /// </summary>
    Total,
}

/// <summary>Origin mechanism of a collected telemetry source (<c>source.source_kind</c>).</summary>
public enum SourceKind
{
    /// <summary>Transcript or session file on disk (<c>file</c>).</summary>
    File,

    /// <summary>OpenTelemetry OTLP payload (<c>otlp</c>).</summary>
    Otlp,
}

/// <summary>Context bucket classification for a collected content part (<c>content_part.bucket</c>).</summary>
public enum ContentBucket
{
    /// <summary>System prompt (<c>system_prompt</c>).</summary>
    SystemPrompt,

    /// <summary>Tool definitions bundle (<c>tool_definitions</c>).</summary>
    ToolDefinitions,

    /// <summary>Individual tool definition (<c>tool_definition</c>).</summary>
    ToolDefinition,

    /// <summary>Injected instruction context (<c>instruction_context</c>).</summary>
    InstructionContext,

    /// <summary>Conversation history (<c>conversation_history</c>).</summary>
    ConversationHistory,

    /// <summary>Tool result content (<c>tool_result_content</c>).</summary>
    ToolResultContent,

    /// <summary>User prompt (<c>user_prompt</c>).</summary>
    UserPrompt,

    /// <summary>User message (<c>user_message</c>).</summary>
    UserMessage,

    /// <summary>Model reasoning trace (<c>reasoning</c>).</summary>
    Reasoning,
}

/// <summary>Identifies a telemetry source (<c>source</c> table).</summary>
/// <param name="HarnessId">Attributed harness identifier (<c>source.harness_id</c>).</param>
/// <param name="Kind">Source origin kind (<c>source.source_kind</c>).</param>
/// <param name="Locator">Source locator such as file path or OTLP endpoint key (<c>source.locator</c>).</param>
public record CollectedSource(
    string HarnessId,
    SourceKind Kind,
    string Locator);

/// <summary>Top-level static collector output batch for a single <see cref="CollectedSource"/>.</summary>
/// <remarks>
/// Placed in <c>KyberDash.Domain</c> while preserving <c>namespace KyberDash.Data.Collector;</c>
/// (Decision D11) so <c>KyberDash.Contracts</c> can reference collector DTOs without depending
/// on <c>KyberDash.Data</c>.
/// </remarks>
/// <param name="Source">Source metadata for the batch.</param>
/// <param name="Sessions">Sessions observed or updated in this batch.</param>
/// <param name="Records">Telemetry records emitted in this batch.</param>
/// <param name="Problems">Ingest diagnostics recorded during collection.</param>
public record CollectedBatch(
    CollectedSource Source,
    IReadOnlyList<CollectedSession> Sessions,
    IReadOnlyList<CollectedRecord> Records,
    IReadOnlyList<CollectedProblem> Problems);

/// <summary>Diagnostic problem emitted during collection (<c>ingest_problem</c> and optional child tables).</summary>
/// <param name="ProblemKey">Idempotent problem key (<c>ingest_problem.problem_key</c>).</param>
/// <param name="Severity">Problem severity (<c>ingest_problem.severity</c>).</param>
/// <param name="Code">Machine-readable problem code (<c>ingest_problem.code</c>).</param>
/// <param name="Message">Human-readable diagnostic message (<c>ingest_problem.message</c>).</param>
/// <param name="SpanId">Optional related span identifier (<c>ingest_problem_span.span_id</c>).</param>
/// <param name="Location">Optional source location (<c>ingest_problem_location.location</c>).</param>
/// <param name="SessionKey">Optional related session key (<c>ingest_problem_session.session_id</c>).</param>
/// <param name="HarnessId">Optional harness identifier (<c>ingest_problem_harness.harness_id</c>).</param>
/// <param name="TimestampUtc">Optional UTC timestamp (<c>ingest_problem_timestamp.timestamp_utc</c>).</param>
/// <param name="Stack">Optional stack trace (<c>ingest_problem_stack.stack</c>).</param>
public record CollectedProblem(
    string ProblemKey,
    string Severity,
    string Code,
    string Message,
    string? SpanId = null,
    string? Location = null,
    string? SessionKey = null,
    string? HarnessId = null,
    string? TimestampUtc = null,
    string? Stack = null);

/// <summary>Collected session metadata (<c>session</c> and <c>session_title</c>).</summary>
/// <param name="SessionKey">Canonical session key (<c>session.session_id</c>).</param>
/// <param name="HarnessId">Attributed harness identifier (<c>session.harness_id</c>).</param>
/// <param name="NativeSessionId">Harness-native session identifier (<c>session.native_session_id</c>).</param>
/// <param name="Workspace">Optional workspace metadata.</param>
/// <param name="Title">Optional session title (<c>session_title.title</c>).</param>
/// <param name="ReportedUsage">Harness-reported per-model token usage snapshots.</param>
public record CollectedSession(
    string SessionKey,
    string HarnessId,
    string NativeSessionId,
    CollectedWorkspace? Workspace,
    string? Title,
    IReadOnlyList<CollectedModelUsage> ReportedUsage);

/// <summary>Collected workspace attributes (<c>session_workspace</c>, <c>session_repository</c>, <c>session_branch</c>).</summary>
/// <param name="WorkingDirectory">Optional working directory (<c>session_workspace.working_directory</c>).</param>
/// <param name="Repository">Optional repository name (<c>session_repository.repo_name</c>).</param>
/// <param name="Branch">Optional branch name (<c>session_branch.branch_name</c>).</param>
public record CollectedWorkspace(
    string? WorkingDirectory,
    string? Repository,
    string? Branch);

/// <summary>Per-model token usage snapshot for a session (<c>session_model_usage</c>).</summary>
/// <param name="SnapshotPosition">Zero-based snapshot index (<c>session_model_usage.snapshot_position</c>).</param>
/// <param name="Model">Model identifier (<c>session_model_usage.model_id</c>).</param>
/// <param name="TokenClass">Token class (<c>session_model_usage.token_class</c>).</param>
/// <param name="Count">Token count (<c>session_model_usage.token_count</c>).</param>
public record CollectedModelUsage(
    int SnapshotPosition,
    string Model,
    TokenClass TokenClass,
    long Count);

/// <summary>Token count entry for a record (<c>record_token_count</c>).</summary>
/// <param name="TokenClass">Token class (<c>record_token_count.token_class</c>).</param>
/// <param name="Count">Token count (<c>record_token_count.token_count</c>).</param>
public record CollectedTokenCount(
    TokenClass TokenClass,
    long Count);

/// <summary>Reported monetary cost for a record (<c>record_reported_cost</c>).</summary>
/// <param name="Amount">Monetary amount (<c>record_reported_cost.amount</c>).</param>
/// <param name="Currency">ISO currency code (<c>record_reported_cost.currency</c>).</param>
public record CollectedCost(
    double Amount,
    string Currency);

/// <summary>Structured error details for a tool call (<c>tool_call_error</c>, <c>tool_call_error_code</c>).</summary>
/// <param name="Message">Error message (<c>tool_call_error.message</c>).</param>
/// <param name="Code">Optional error code (<c>tool_call_error_code.code</c>).</param>
public record CollectedError(
    string Message,
    string? Code);

/// <summary>Size and measured token metrics for a withheld content part (<c>content_part_withheld</c>).</summary>
/// <param name="Utf8Bytes">Original UTF-8 byte length (<c>content_part_withheld.utf8_bytes</c>).</param>
/// <param name="Characters">Original character count (<c>content_part_withheld.characters</c>).</param>
/// <param name="MeasuredTokens">Measured token count (<c>content_part_withheld.measured_tokens</c>).</param>
public record CollectedWithheldText(
    long Utf8Bytes,
    long Characters,
    long MeasuredTokens);

/// <summary>
/// Ordered content part of a record (<c>content_part</c> and child extension tables).
/// Enforces mutual exclusion between <see cref="PlainText"/> and <see cref="Withheld"/>.
/// </summary>
/// <param name="Position">Zero-based position (<c>content_part.position</c>).</param>
/// <param name="Bucket">Content bucket (<c>content_part.bucket</c>).</param>
/// <param name="PlainText">Plain text payload (<c>content_part_text.part_text</c>), mutually exclusive with <paramref name="Withheld"/>.</param>
/// <param name="Withheld">Withheld text metrics (<c>content_part_withheld</c>), mutually exclusive with <paramref name="PlainText"/>.</param>
/// <param name="ReportedTokens">Optional harness-reported token count (<c>content_part_reported_tokens.token_count</c>).</param>
/// <param name="Server">Optional MCP server name (<c>content_part_server.server_name</c>).</param>
/// <param name="Role">Optional message role (<c>content_part_role.role_name</c>).</param>
/// <param name="Resident">Optional context residency flag (<c>content_part_residency.is_resident</c>).</param>
/// <param name="OriginalUtf8BytesWhenTruncated">Original byte length when truncated (<c>content_part_truncation.original_utf8_bytes</c>).</param>
/// <exception cref="ArgumentException">Thrown when both <paramref name="PlainText"/> and <paramref name="Withheld"/> are non-null or both are null.</exception>
public record CollectedContentPart(
    int Position,
    ContentBucket Bucket,
    string? PlainText,
    CollectedWithheldText? Withheld,
    long? ReportedTokens,
    string? Server,
    string? Role,
    bool? Resident,
    long? OriginalUtf8BytesWhenTruncated)
{
    /// <summary>Plain text payload (<c>content_part_text.part_text</c>), mutually exclusive with <see cref="Withheld"/>.</summary>
    public string? PlainText { get; init; } = ValidatePlainText(PlainText, Withheld);

    private static string? ValidatePlainText(string? plainText, CollectedWithheldText? withheld)
    {
        if (!((plainText is not null) ^ (withheld is not null)))
        {
            throw new ArgumentException(
                "Exactly one of PlainText or Withheld must be non-null.",
                plainText is null && withheld is null ? nameof(PlainText) : nameof(Withheld));
        }

        return plainText;
    }
}

/// <summary>Ordered tool schema definition advertised on a record (<c>tool_definition</c>, <c>tool_definition_server</c>).</summary>
/// <param name="Position">Zero-based position (<c>tool_definition.position</c>).</param>
/// <param name="ToolName">Tool name (<c>tool_definition.tool_name</c>).</param>
/// <param name="Server">Optional MCP server name (<c>tool_definition_server.server_name</c>).</param>
/// <param name="SchemaTokens">Token count of the tool schema (<c>tool_definition.schema_tokens</c>).</param>
public record CollectedToolDefinition(
    int Position,
    string ToolName,
    string? Server,
    long SchemaTokens);

/// <summary>Input or result payload metrics and optional text for a tool call (<c>tool_call_input_*</c> or <c>tool_call_result_*</c>).</summary>
/// <param name="PlainText">Optional plain text (<c>tool_call_input_text.text</c> / <c>tool_call_result_text.text</c>).</param>
/// <param name="Utf8Bytes">UTF-8 byte length (<c>tool_call_input_size.utf8_bytes</c> / <c>tool_call_result_size.utf8_bytes</c>).</param>
/// <param name="Characters">Character count (<c>tool_call_input_size.characters</c> / <c>tool_call_result_size.characters</c>).</param>
/// <param name="Sha256Fingerprint">Optional 64-character hex SHA-256 fingerprint for inputs (<c>tool_call_input_fingerprint.sha256</c>).</param>
/// <param name="OriginalUtf8BytesWhenTruncated">Optional original byte count when truncated (<c>tool_call_input_truncation.original_utf8_bytes</c> / <c>tool_call_result_truncation.original_utf8_bytes</c>).</param>
public record CollectedToolContent(
    string? PlainText,
    long Utf8Bytes,
    long Characters,
    string? Sha256Fingerprint,
    long? OriginalUtf8BytesWhenTruncated);

/// <summary>Tool invocation details attached to a record (<c>tool_call</c> and extension tables).</summary>
/// <param name="CallId">Tool call identifier (<c>tool_call.call_id</c>).</param>
/// <param name="ToolName">Tool name (<c>tool_call.tool_name</c>).</param>
/// <param name="McpServerName">Optional MCP server name (<c>tool_call_mcp.server_name</c>).</param>
/// <param name="McpToolName">Optional MCP tool name (<c>tool_call_mcp.server_tool_name</c>).</param>
/// <param name="Input">Optional tool input payload and metrics.</param>
/// <param name="Result">Optional tool result payload and metrics.</param>
/// <param name="Succeeded">Optional success flag (<c>tool_call_outcome.succeeded</c>).</param>
/// <param name="Error">Optional structured tool error.</param>
/// <param name="ExitCode">Optional process exit code (<c>tool_call_exit_code.exit_code</c>).</param>
public record CollectedToolCall(
    string CallId,
    string ToolName,
    string? McpServerName,
    string? McpToolName,
    CollectedToolContent? Input,
    CollectedToolContent? Result,
    bool? Succeeded,
    CollectedError? Error,
    long? ExitCode);

/// <summary>Subagent or agent invocation details attached to a record (<c>agent_invocation</c> and extension tables).</summary>
/// <param name="AgentName">Agent name (<c>agent_invocation.agent_name</c>).</param>
/// <param name="SpawningCallId">Optional spawning record identifier (<c>agent_invocation_spawn.spawning_record_id</c>).</param>
/// <param name="Succeeded">Optional success flag (<c>agent_invocation_outcome.succeeded</c>).</param>
/// <param name="Error">Optional error message (<c>agent_invocation_error.message</c>).</param>
public record CollectedAgentInvocation(
    string AgentName,
    string? SpawningCallId,
    bool? Succeeded,
    string? Error);

/// <summary>Diagnostic signals attached to a record (<c>record_error</c>, <c>record_edit_failure</c>, <c>record_correction</c>, <c>record_correction_rule</c>, <c>record_declared_context_window</c>).</summary>
/// <param name="ErrorMessage">Optional error message (<c>record_error.message</c>).</param>
/// <param name="EditFailures">Optional edit failure count (<c>record_edit_failure.failure_count</c>).</param>
/// <param name="CorrectionRule">Optional correction rule identifier (<c>record_correction_rule.correction_rule</c>; implies <c>record_correction</c>).</param>
/// <param name="DeclaredContextWindow">Optional declared context window size (<c>record_declared_context_window.declared_context_window</c>).</param>
public record CollectedRecordSignals(
    string? ErrorMessage,
    long? EditFailures,
    string? CorrectionRule,
    long? DeclaredContextWindow);

/// <summary>Collected telemetry record (<c>record</c> and its normalized child tables).</summary>
/// <param name="RecordId">Canonical record/span identifier (<c>record.span_id</c>).</param>
/// <param name="HarnessId">Attributed harness identifier (<c>record.harness_id</c>).</param>
/// <param name="Op">Operation classification (<c>record.op</c>).</param>
/// <param name="Kind">Span kind (<c>record.kind</c>).</param>
/// <param name="Name">Span or event name (<c>record.name</c>).</param>
/// <param name="StartedAt">Start timestamp (<c>record.started_at_utc</c>).</param>
/// <param name="Status">Record status (<c>record.status</c>).</param>
/// <param name="SessionKey">Optional linked session key (<c>record_session_link.session_key</c>).</param>
/// <param name="ParentRecordId">Optional parent record identifier (<c>record_parent.parent_span_id</c>).</param>
/// <param name="TraceId">Optional trace identifier (<c>record_trace.trace_id</c>).</param>
/// <param name="Model">Optional model identifier (<c>record_model.model_id</c>).</param>
/// <param name="EndedAt">Optional end timestamp (<c>record_end.ended_at_utc</c>).</param>
/// <param name="ReportedDurationMs">Optional harness-reported duration in milliseconds (<c>record_reported_duration.duration_ms</c>).</param>
/// <param name="Tokens">Token counts by class (<c>record_token_count</c>).</param>
/// <param name="ReportedCost">Optional harness-reported cost (<c>record_reported_cost</c>).</param>
/// <param name="Parts">Ordered content parts (<c>content_part</c>).</param>
/// <param name="Tool">Optional tool invocation details (<c>tool_call</c>).</param>
/// <param name="Agent">Optional agent invocation details (<c>agent_invocation</c>).</param>
/// <param name="SkillsInvoked">Skills invoked by this record (<c>record_skill_invocation.skill_name</c>).</param>
/// <param name="AgentInstanceId">Optional agent instance identifier (<c>record_agent_instance.agent_instance_id</c>).</param>
/// <param name="ToolDefinitions">Ordered tool definitions (<c>tool_definition</c>).</param>
/// <param name="Signals">Diagnostic signals attached to the record.</param>
/// <param name="ReportedInputTokens">Optional reported input total (<c>record_reported_total.token_count</c> with direction <c>input</c>).</param>
/// <param name="ReportedOutputTokens">Optional reported output total (<c>record_reported_total.token_count</c> with direction <c>output</c>).</param>
public record CollectedRecord(
    string RecordId,
    string HarnessId,
    RecordOp Op,
    RecordKind Kind,
    string Name,
    DateTimeOffset StartedAt,
    RecordStatus Status,
    string? SessionKey,
    string? ParentRecordId,
    string? TraceId,
    string? Model,
    DateTimeOffset? EndedAt,
    long? ReportedDurationMs,
    IReadOnlyList<CollectedTokenCount> Tokens,
    CollectedCost? ReportedCost,
    IReadOnlyList<CollectedContentPart> Parts,
    CollectedToolCall? Tool,
    CollectedAgentInvocation? Agent,
    IReadOnlyList<string> SkillsInvoked,
    string? AgentInstanceId,
    IReadOnlyList<CollectedToolDefinition> ToolDefinitions,
    CollectedRecordSignals Signals,
    long? ReportedInputTokens = null,
    long? ReportedOutputTokens = null);

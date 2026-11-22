using KyberDash.Domain;

namespace KyberDash.Contracts.Repositories;

/// <summary>Ordered turn span reference for a projected session (<c>session_turn</c>).</summary>
/// <param name="SessionId">Owning session identifier.</param>
/// <param name="Position">Zero-based turn index.</param>
/// <param name="SpanId">Record span identifier for the turn.</param>
public record StoredSessionTurn(string SessionId, int Position, string SpanId);

/// <summary>Derived context counter persisted on a session context (<c>session_context_derived_count</c>).</summary>
/// <param name="Counter">Counter name.</param>
/// <param name="Count">Counter value.</param>
public record StoredSessionContextDerivedCount(string Counter, long Count);

/// <summary>Bucket token count for a projected context turn (<c>session_context_turn_bucket</c>).</summary>
/// <param name="Bucket">Context bucket name.</param>
/// <param name="Tokens">Measured or attributed token count.</param>
public record StoredTurnBucketTokens(string Bucket, long Tokens);

/// <summary>Per-server tool definition token count for a projected context turn (<c>session_context_turn_server_definitions</c>).</summary>
/// <param name="Server">MCP server name.</param>
/// <param name="Tokens">Tool definition token count.</param>
public record StoredTurnServerDefinitionTokens(string Server, long Tokens);

/// <summary>Normalized context analysis turn (<c>session_context_turn</c> and child extension tables).</summary>
/// <param name="Position">Zero-based turn index.</param>
/// <param name="InputTokens">Total input tokens.</param>
/// <param name="BucketedTokens">Attributed bucketed tokens.</param>
/// <param name="ResidualTokens">Residual tokens.</param>
/// <param name="ResidualAttribution">Residual attribution label.</param>
/// <param name="BuiltinToolDefinitionTokens">Built-in tool definition token count.</param>
/// <param name="FreshInput">Fresh input token count.</param>
/// <param name="Buckets">Per-bucket token breakdown (<c>session_context_turn_bucket</c>).</param>
/// <param name="ServerDefinitions">Per-server definition token breakdown (<c>session_context_turn_server_definitions</c>).</param>
/// <param name="Headroom">Optional remaining context headroom (<c>session_context_turn_headroom.headroom</c>).</param>
/// <param name="Pressure">Optional context pressure ratio (<c>session_context_turn_headroom.pressure</c>).</param>
/// <param name="AccumulationRate">Optional accumulation rate (<c>session_context_turn_accumulation.rate</c>).</param>
/// <param name="StrippedBlockCount">Optional stripped instruction block count (<c>session_context_turn_stripped_instructions.block_count</c>).</param>
/// <param name="StrippedTokens">Optional stripped instruction tokens (<c>session_context_turn_stripped_instructions.tokens</c>).</param>
/// <param name="PreviousFreshInput">Optional previous fresh input (<c>session_context_turn_fresh_jump.previous_fresh_input</c>).</param>
/// <param name="FreshJumpFactor">Optional fresh jump factor (<c>session_context_turn_fresh_jump.factor</c>).</param>
/// <param name="NonResidentTokens">Optional non-resident tokens (<c>session_context_turn_non_resident.tokens</c>).</param>
public record StoredSessionContextTurn(
    int Position,
    long InputTokens,
    long BucketedTokens,
    long ResidualTokens,
    string ResidualAttribution,
    long BuiltinToolDefinitionTokens,
    long FreshInput,
    IReadOnlyList<StoredTurnBucketTokens> Buckets,
    IReadOnlyList<StoredTurnServerDefinitionTokens> ServerDefinitions,
    long? Headroom = null,
    double? Pressure = null,
    double? AccumulationRate = null,
    long? StrippedBlockCount = null,
    long? StrippedTokens = null,
    long? PreviousFreshInput = null,
    double? FreshJumpFactor = null,
    long? NonResidentTokens = null);

/// <summary>Normalized session context projection (<c>session_context</c> and child tables).</summary>
/// <param name="ContextLimit">Resolved context window limit.</param>
/// <param name="ContextLimitSource">Source of the context window limit.</param>
/// <param name="ResidualTotal">Total residual tokens.</param>
/// <param name="FreshJumpFactor">Session fresh jump factor.</param>
/// <param name="AccumulationRate">Session accumulation rate.</param>
/// <param name="ObservedBuckets">Observed content buckets (<c>session_context_observed_part</c>).</param>
/// <param name="DerivedCounts">Derived counters (<c>session_context_derived_count</c>).</param>
/// <param name="FlaggedTurnIndexes">Flagged turn indexes (<c>session_context_flagged_turn</c>).</param>
/// <param name="Turns">Per-turn context breakdown (<c>session_context_turn</c>).</param>
public record StoredSessionContext(
    long ContextLimit,
    string ContextLimitSource,
    long ResidualTotal,
    double FreshJumpFactor,
    double AccumulationRate,
    IReadOnlyList<string> ObservedBuckets,
    IReadOnlyList<StoredSessionContextDerivedCount> DerivedCounts,
    IReadOnlyList<int> FlaggedTurnIndexes,
    IReadOnlyList<StoredSessionContextTurn> Turns);

/// <summary>Ranked tool schema row (<c>session_schema_ranked_tool</c> and <c>session_schema_ranked_tool_server</c>).</summary>
/// <param name="Position">Zero-based rank position.</param>
/// <param name="ToolName">Tool name.</param>
/// <param name="CostTokens">Token cost across turns.</param>
/// <param name="Invoked">Whether the tool was invoked in the session.</param>
/// <param name="ServerName">Optional MCP server name.</param>
public record StoredSchemaRankedTool(
    int Position,
    string ToolName,
    long CostTokens,
    bool Invoked,
    string? ServerName = null);

/// <summary>Per-server schema residency token count (<c>session_schema_server</c>).</summary>
/// <param name="ServerName">MCP server name.</param>
/// <param name="Tokens">Residency token total.</param>
public record StoredSchemaServerTokens(string ServerName, long Tokens);

/// <summary>Normalized session schema projection (<c>session_schema</c> and child tables).</summary>
/// <param name="InvocationCount">Tool invocation count.</param>
/// <param name="TurnCount">Evaluated turn count.</param>
/// <param name="RankedTools">Ranked tool definitions.</param>
/// <param name="Servers">Per-server schema token totals.</param>
/// <param name="UnusedTokenResidencies">Optional unused schema token residencies (<c>session_schema_unused</c>).</param>
public record StoredSessionSchema(
    int InvocationCount,
    int TurnCount,
    IReadOnlyList<StoredSchemaRankedTool> RankedTools,
    IReadOnlyList<StoredSchemaServerTokens> Servers,
    long? UnusedTokenResidencies = null);

/// <summary>
/// Normalized projected session summary (<c>session_projection*</c> and <c>session_turn</c> tables).
/// </summary>
/// <param name="SessionId">Session identifier.</param>
/// <param name="Label">Human-readable session label.</param>
/// <param name="IsSubagent">Whether this session is a delegated subagent session.</param>
/// <param name="SpanCount">Number of spans in the session.</param>
/// <param name="StartedAtUtc">Earliest span timestamp.</param>
/// <param name="EndedAtUtc">Latest span timestamp.</param>
/// <param name="ParentSessionId">Optional parent session identifier (<c>session_projection_parent</c>).</param>
/// <param name="AgentName">Optional subagent name (<c>session_projection_agent</c>).</param>
/// <param name="IncludedInParent">Whether this subagent is folded into its parent (<c>session_projection_included_in_parent</c>).</param>
/// <param name="RequestSpanIds">Ordered request span identifiers (<c>session_request</c>).</param>
/// <param name="Turns">Ordered turn span entries (<c>session_turn</c>).</param>
/// <param name="Context">Optional measurable context projection (<c>session_context*</c>).</param>
/// <param name="ContextUnmeasurableReason">Optional reason context could not be measured (<c>session_context_unmeasurable</c>).</param>
/// <param name="Schema">Optional measurable schema projection (<c>session_schema*</c>).</param>
/// <param name="SchemaUnmeasurableReason">Optional reason schema could not be measured (<c>session_schema_unmeasurable</c>).</param>
/// <param name="TimelineSubagentSpanIds">Subagent timeline span identifiers (<c>session_timeline_subagent</c>).</param>
/// <param name="TimelineAuxiliarySpanIds">Auxiliary timeline span identifiers (<c>session_timeline_auxiliary</c>).</param>
public record StoredSessionProjection(
    string SessionId,
    string Label,
    bool IsSubagent,
    int SpanCount,
    string StartedAtUtc,
    string EndedAtUtc,
    string? ParentSessionId,
    string? AgentName,
    bool IncludedInParent,
    IReadOnlyList<string> RequestSpanIds,
    IReadOnlyList<StoredSessionTurn> Turns,
    StoredSessionContext? Context = null,
    string? ContextUnmeasurableReason = null,
    StoredSessionSchema? Schema = null,
    string? SchemaUnmeasurableReason = null,
    IReadOnlyList<string>? TimelineSubagentSpanIds = null,
    IReadOnlyList<string>? TimelineAuxiliarySpanIds = null);

/// <summary>Normalized run grouping row (<c>run</c>, <c>run_label</c>, <c>run_working_directory</c>, <c>run_bounds</c>).</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="HarnessId">Attributed harness identifier.</param>
/// <param name="GroupingBasis">Grouping basis (<c>explicit</c> or <c>derived</c>).</param>
/// <param name="GroupingRule">Grouping rule (<c>explicit_run_id</c> or <c>session_fallback</c>).</param>
/// <param name="ExecutionCount">Number of executions in the run.</param>
/// <param name="Label">Optional run label (<c>run_label.label</c>).</param>
/// <param name="WorkingDirectory">Optional working directory (<c>run_working_directory.working_directory</c>).</param>
/// <param name="StartedAtUtc">Optional start timestamp (<c>run_bounds.started_at_utc</c>).</param>
/// <param name="EndedAtUtc">Optional end timestamp (<c>run_bounds.ended_at_utc</c>).</param>
public record StoredRun(
    string RunId,
    string HarnessId,
    string GroupingBasis,
    string GroupingRule,
    int ExecutionCount,
    string? Label = null,
    string? WorkingDirectory = null,
    string? StartedAtUtc = null,
    string? EndedAtUtc = null);

/// <summary>Normalized agent execution row (<c>execution</c>, <c>execution_session</c>, <c>execution_parent</c>, <c>execution_agent</c>, <c>execution_bounds</c>).</summary>
/// <param name="ExecutionId">Execution identifier.</param>
/// <param name="RunId">Owning run identifier.</param>
/// <param name="HarnessId">Attributed harness identifier.</param>
/// <param name="IsRoot">Whether this execution is the root of its run.</param>
/// <param name="SessionId">Optional linked session identifier (<c>execution_session.session_id</c>).</param>
/// <param name="ParentExecutionId">Optional parent execution identifier (<c>execution_parent.parent_execution_id</c>).</param>
/// <param name="AgentName">Optional agent name (<c>execution_agent.agent_name</c>).</param>
/// <param name="StartedAtUtc">Optional start timestamp (<c>execution_bounds.started_at_utc</c>).</param>
/// <param name="EndedAtUtc">Optional end timestamp (<c>execution_bounds.ended_at_utc</c>).</param>
public record StoredExecution(
    string ExecutionId,
    string RunId,
    string HarnessId,
    bool IsRoot,
    string? SessionId = null,
    string? ParentExecutionId = null,
    string? AgentName = null,
    string? StartedAtUtc = null,
    string? EndedAtUtc = null);

/// <summary>Normalized harness rollup metric row (<c>harness_rollup_metric</c>).</summary>
/// <param name="Metric">Metric identifier.</param>
/// <param name="Value">Metric value.</param>
/// <param name="Basis">Metric basis (<c>measured</c> or <c>derived</c>).</param>
public record StoredHarnessRollupMetric(string Metric, double Value, string Basis);

/// <summary>Normalized harness rollup (<c>harness_rollup</c> and <c>harness_rollup_metric</c>).</summary>
/// <param name="HarnessId">Attributed harness identifier.</param>
/// <param name="SessionCount">Session count.</param>
/// <param name="RunCount">Run count.</param>
/// <param name="ExecutionCount">Execution count.</param>
/// <param name="UnknownWindowSessions">Sessions with unknown context window.</param>
/// <param name="WindowSessionsTotal">Total sessions evaluated for window coverage.</param>
/// <param name="Metrics">Normalized metric entries (<c>harness_rollup_metric</c>).</param>
public record StoredHarnessRollup(
    string HarnessId,
    int SessionCount,
    int RunCount,
    int ExecutionCount,
    int UnknownWindowSessions,
    int WindowSessionsTotal,
    IReadOnlyList<StoredHarnessRollupMetric> Metrics);

/// <summary>Compaction hazard context-limit metadata (<c>finding_compaction_hazard</c>).</summary>
/// <param name="ContextLimit">Context limit in tokens.</param>
/// <param name="ContextLimitSource">Source of the context limit.</param>
public record StoredCompactionHazard(long ContextLimit, string ContextLimitSource);

/// <summary>Normalized diagnostic finding (<c>finding</c> and child extension tables).</summary>
/// <param name="FindingId">Finding identifier.</param>
/// <param name="DetectorId">Detector identifier.</param>
/// <param name="Title">Finding title.</param>
/// <param name="Mechanism">Finding mechanism explanation.</param>
/// <param name="Confidence">Confidence tier.</param>
/// <param name="Recommendation">Actionable recommendation.</param>
/// <param name="OutcomeRiskCaveat">Outcome risk caveat.</param>
/// <param name="RankScore">Ranking score.</param>
/// <param name="EvidenceLinks">Ordered evidence links (<c>finding_evidence_link</c>).</param>
/// <param name="EstimatedWasteTokens">Optional estimated waste in tokens (<c>finding_estimated_waste</c>).</param>
/// <param name="ErrorBar">Optional waste error bar (<c>finding_error_bar</c>).</param>
/// <param name="RunId">Optional run identifier (<c>finding_run</c>).</param>
/// <param name="SessionId">Optional session identifier (<c>finding_session</c>).</param>
/// <param name="MeasurementClass">Optional measurement class (<c>finding_measurement_class</c>).</param>
/// <param name="ConfidenceBasis">Optional confidence basis (<c>finding_confidence_basis</c>).</param>
/// <param name="WhatWouldRaiseIt">Optional confidence upgrade hint (<c>finding_what_would_raise_it</c>).</param>
/// <param name="CompactionHazard">Optional compaction hazard metadata (<c>finding_compaction_hazard</c>).</param>
public record StoredFinding(
    string FindingId,
    string DetectorId,
    string Title,
    string Mechanism,
    string Confidence,
    string Recommendation,
    string OutcomeRiskCaveat,
    double RankScore,
    IReadOnlyList<FindingEvidenceLink> EvidenceLinks,
    long? EstimatedWasteTokens = null,
    FindingErrorBar? ErrorBar = null,
    string? RunId = null,
    string? SessionId = null,
    string? MeasurementClass = null,
    string? ConfidenceBasis = null,
    string? WhatWouldRaiseIt = null,
    StoredCompactionHazard? CompactionHazard = null);

/// <summary>Normalized prediction and calibration row (<c>prediction</c> and child extension tables).</summary>
/// <param name="PredictionId">Prediction identifier.</param>
/// <param name="FindingId">Related finding identifier.</param>
/// <param name="RunId">Target run identifier.</param>
/// <param name="PredictedWasteTokens">Predicted waste in tokens.</param>
/// <param name="Confidence">Numeric confidence.</param>
/// <param name="ConfidenceTier">Confidence tier label.</param>
/// <param name="CreatedAtUtc">UTC creation timestamp.</param>
/// <param name="ComparisonRunId">Optional comparison run identifier (<c>prediction_comparison</c>).</param>
/// <param name="ObservedDeltaTokens">Optional observed delta in tokens (<c>prediction_observed_delta</c>).</param>
/// <param name="CalibrationScore">Optional calibration score (<c>prediction_calibration</c>).</param>
public record StoredPrediction(
    string PredictionId,
    string FindingId,
    string RunId,
    long PredictedWasteTokens,
    double Confidence,
    string ConfidenceTier,
    string CreatedAtUtc,
    string? ComparisonRunId = null,
    long? ObservedDeltaTokens = null,
    double? CalibrationScore = null);

/// <summary>
/// Typed read/replace persistence contract for derived projections (<c>session_projection*</c>,
/// <c>session_turn</c>, <c>run*</c>, <c>execution*</c>, <c>harness_rollup*</c>, <c>finding*</c>,
/// and <c>prediction*</c>).
/// </summary>
public interface IProjectionStore
{
    /// <summary>Gets a projected session summary by <paramref name="sessionId"/>.</summary>
    /// <param name="sessionId">Session identifier.</param>
    StoredSessionProjection? GetSessionProjection(string sessionId);

    /// <summary>Lists projected session summaries optionally filtered by <paramref name="harnessId"/>.</summary>
    /// <param name="harnessId">Optional harness filter.</param>
    IReadOnlyList<StoredSessionProjection> ListSessionProjections(string? harnessId = null);

    /// <summary>Replaces one projected session summary and its child rows atomically.</summary>
    /// <param name="projection">Projected session summary to persist.</param>
    void ReplaceSessionProjection(StoredSessionProjection projection);

    /// <summary>Replaces multiple projected session summaries and their child rows atomically.</summary>
    /// <param name="projections">Projected session summaries to persist.</param>
    void ReplaceSessionProjections(IEnumerable<StoredSessionProjection> projections);

    /// <summary>Deletes a projected session summary and its child rows.</summary>
    /// <param name="sessionId">Session identifier to delete.</param>
    void DeleteSessionProjection(string sessionId);

    /// <summary>Gets ordered turns (<c>session_turn</c>) for <paramref name="sessionId"/>.</summary>
    /// <param name="sessionId">Session identifier.</param>
    IReadOnlyList<StoredSessionTurn> GetSessionTurns(string sessionId);

    /// <summary>Replaces ordered turns (<c>session_turn</c>) for <paramref name="sessionId"/>.</summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="turns">Ordered turn entries.</param>
    void ReplaceSessionTurns(string sessionId, IEnumerable<StoredSessionTurn> turns);

    /// <summary>Gets one run grouping by <paramref name="runId"/>.</summary>
    /// <param name="runId">Run identifier.</param>
    StoredRun? GetRun(string runId);

    /// <summary>Lists run groupings optionally filtered by <paramref name="harnessId"/>.</summary>
    /// <param name="harnessId">Optional harness filter.</param>
    IReadOnlyList<StoredRun> ListRuns(string? harnessId = null);

    /// <summary>Replaces run groupings and their child rows (<c>run*</c>).</summary>
    /// <param name="runs">Run rows to persist.</param>
    void ReplaceRuns(IEnumerable<StoredRun> runs);

    /// <summary>Deletes a run grouping by <paramref name="runId"/>.</summary>
    /// <param name="runId">Run identifier.</param>
    void DeleteRun(string runId);

    /// <summary>Gets one agent execution by <paramref name="executionId"/>.</summary>
    /// <param name="executionId">Execution identifier.</param>
    StoredExecution? GetExecution(string executionId);

    /// <summary>Lists agent executions belonging to <paramref name="runId"/>.</summary>
    /// <param name="runId">Run identifier.</param>
    IReadOnlyList<StoredExecution> ListExecutionsForRun(string runId);

    /// <summary>Lists agent executions belonging to <paramref name="harnessId"/>.</summary>
    /// <param name="harnessId">Harness identifier.</param>
    IReadOnlyList<StoredExecution> ListExecutionsByHarness(string harnessId);

    /// <summary>Replaces agent executions and their child rows (<c>execution*</c>).</summary>
    /// <param name="executions">Execution rows to persist.</param>
    void ReplaceExecutions(IEnumerable<StoredExecution> executions);

    /// <summary>Deletes agent executions belonging to <paramref name="runId"/>.</summary>
    /// <param name="runId">Run identifier.</param>
    void DeleteExecutionsForRun(string runId);

    /// <summary>Gets one harness rollup by <paramref name="harnessId"/>.</summary>
    /// <param name="harnessId">Harness identifier.</param>
    StoredHarnessRollup? GetHarnessRollup(string harnessId);

    /// <summary>Lists all persisted harness rollups (<c>harness_rollup*</c>).</summary>
    IReadOnlyList<StoredHarnessRollup> ListHarnessRollups();

    /// <summary>Replaces harness rollups and their metric rows (<c>harness_rollup*</c>).</summary>
    /// <param name="rollups">Harness rollups to persist.</param>
    void ReplaceHarnessRollups(IEnumerable<StoredHarnessRollup> rollups);

    /// <summary>Deletes a harness rollup by <paramref name="harnessId"/>.</summary>
    /// <param name="harnessId">Harness identifier.</param>
    void DeleteHarnessRollup(string harnessId);

    /// <summary>Gets one diagnostic finding by <paramref name="findingId"/>.</summary>
    /// <param name="findingId">Finding identifier.</param>
    StoredFinding? GetFinding(string findingId);

    /// <summary>Lists diagnostic findings optionally filtered by <paramref name="runId"/> or <paramref name="sessionId"/>.</summary>
    /// <param name="runId">Optional run identifier filter.</param>
    /// <param name="sessionId">Optional session identifier filter.</param>
    IReadOnlyList<StoredFinding> ListFindings(string? runId = null, string? sessionId = null);

    /// <summary>Replaces diagnostic findings and their child rows (<c>finding*</c>).</summary>
    /// <param name="findings">Findings to persist.</param>
    void ReplaceFindings(IEnumerable<StoredFinding> findings);

    /// <summary>Deletes diagnostic findings associated with <paramref name="sessionId"/>.</summary>
    /// <param name="sessionId">Session identifier.</param>
    void DeleteFindingsForSession(string sessionId);

    /// <summary>Gets one prediction by <paramref name="predictionId"/>.</summary>
    /// <param name="predictionId">Prediction identifier.</param>
    StoredPrediction? GetPrediction(string predictionId);

    /// <summary>Lists predictions optionally filtered by <paramref name="runId"/> or <paramref name="findingId"/>.</summary>
    /// <param name="runId">Optional run identifier filter.</param>
    /// <param name="findingId">Optional finding identifier filter.</param>
    IReadOnlyList<StoredPrediction> ListPredictions(string? runId = null, string? findingId = null);

    /// <summary>Replaces one prediction and its child rows (<c>prediction*</c>).</summary>
    /// <param name="prediction">Prediction row to persist.</param>
    void ReplacePrediction(StoredPrediction prediction);

    /// <summary>Deletes one prediction by <paramref name="predictionId"/>.</summary>
    /// <param name="predictionId">Prediction identifier.</param>
    void DeletePrediction(string predictionId);
}

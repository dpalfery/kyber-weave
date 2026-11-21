using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Codex;

/// <summary>Native Codex rollout tool invocation extracted from <c>function_call</c>, <c>custom_tool_call</c>, or <c>local_shell_call</c>.</summary>
/// <param name="CallId">Correlation identifier from <c>call_id</c> or fallback <c>id</c>.</param>
/// <param name="ToolName">Invoked tool name.</param>
/// <param name="RawInputText">Raw <c>arguments</c>, <c>input</c>, or <c>action</c> text prior to measurement/withholding.</param>
/// <param name="IsCustomTool">True when the entry originated from <c>custom_tool_call</c>; false for <c>function_call</c> or <c>local_shell_call</c>.</param>
/// <param name="Timestamp">Optional ISO-8601 rollout entry timestamp.</param>
/// <param name="Status">Optional call-level status reported on <c>local_shell_call</c> (<c>completed</c>, <c>incomplete</c>, <c>in_progress</c>).</param>
/// <param name="WithholdInputPlainText">True when input plain text must always be withheld (such as <c>local_shell_call.action</c>), retaining size and fingerprint only.</param>
public record CodexRolloutToolCall(
    string CallId,
    string ToolName,
    string RawInputText,
    bool IsCustomTool,
    string? Timestamp,
    string? Status = null,
    bool WithholdInputPlainText = false);

/// <summary>Native Codex rollout tool execution result extracted from <c>function_call_output</c>, <c>custom_tool_call_output</c>, or <c>local_shell_call_output</c>.</summary>
/// <param name="CallId">Correlation identifier from <c>call_id</c> or fallback <c>id</c>.</param>
/// <param name="ToolName">Optional tool name reported alongside the output.</param>
/// <param name="OutputText">Extracted result text or serialized JSON payload, when present.</param>
/// <param name="IsError">True when the tool execution failed or exited non-zero.</param>
/// <param name="ExitCode">Optional process exit code reported on the output item.</param>
/// <param name="Timestamp">Optional ISO-8601 rollout entry timestamp.</param>
public record CodexRolloutToolResult(
    string CallId,
    string? ToolName,
    string? OutputText,
    bool IsError,
    long? ExitCode,
    string? Timestamp);

/// <summary>Native Codex rollout message item extracted from <c>response_item</c> (<c>message</c>).</summary>
/// <param name="Role">Optional message role (<c>user</c>, <c>assistant</c>, <c>developer</c>, etc.).</param>
/// <param name="Text">Extracted message text.</param>
/// <param name="CorrectionRule">Matched deterministic user-correction rule, if any.</param>
/// <param name="Timestamp">Optional ISO-8601 rollout entry timestamp.</param>
public record CodexRolloutMessage(
    string? Role,
    string Text,
    string? CorrectionRule,
    string? Timestamp);

/// <summary>Native Codex rollout token usage snapshot extracted from <c>event_msg</c> (<c>token_count</c>).</summary>
/// <param name="Model">Resolved model identifier.</param>
/// <param name="InputTokens">Inclusive input token count reported by Codex.</param>
/// <param name="CachedInputTokens">Cached input token count subset.</param>
/// <param name="OutputTokens">Output token count (already inclusive of reasoning tokens for Codex).</param>
/// <param name="ReasoningTokens">Reasoning output token count subset.</param>
public record CodexRolloutUsage(
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens);

/// <summary>Native Codex rollout turn grouping messages, tool calls, tool results, and token usage.</summary>
/// <param name="TurnIndex">One-based turn ordinal within the rollout unit.</param>
/// <param name="Timestamp">Optional ISO-8601 turn timestamp.</param>
/// <param name="Model">Resolved model identifier for the turn.</param>
/// <param name="TerminationReason">Turn completion or abort discriminator, if observed.</param>
/// <param name="ExitCode">Turn exit code, if observed.</param>
/// <param name="Usage">Optional token usage snapshot.</param>
/// <param name="Messages">Messages accumulated in this turn.</param>
/// <param name="ToolCalls">Tool calls invoked in this turn.</param>
/// <param name="ToolResults">Tool outputs observed in this turn.</param>
public record CodexRolloutTurn(
    int TurnIndex,
    string? Timestamp,
    string? Model,
    string? TerminationReason,
    int? ExitCode,
    CodexRolloutUsage? Usage,
    IReadOnlyList<CodexRolloutMessage> Messages,
    IReadOnlyList<CodexRolloutToolCall> ToolCalls,
    IReadOnlyList<CodexRolloutToolResult> ToolResults);

/// <summary>Parsed in-memory representation of a Codex rollout JSONL source unit.</summary>
/// <param name="SessionId">Native session identifier from <c>session_meta</c>, if present.</param>
/// <param name="Model">Last observed session/turn model identifier, if any.</param>
/// <param name="SystemPrompt">System prompt or base instructions, if present.</param>
/// <param name="InstructionContext">Workspace <c>AGENTS.md</c> instruction context, if present.</param>
/// <param name="WorkingDirectory">Reported workspace working directory, if present.</param>
/// <param name="Turns">Ordered rollout turns.</param>
/// <param name="AllToolResults">All tool outputs observed across the rollout unit for cross-turn pairing.</param>
/// <param name="Problems">Structured ingest problems observed while parsing the rollout unit.</param>
public record CodexRolloutDocument(
    string? SessionId,
    string? Model,
    string? SystemPrompt,
    string? InstructionContext,
    string? WorkingDirectory,
    IReadOnlyList<CodexRolloutTurn> Turns,
    IReadOnlyList<CodexRolloutToolResult> AllToolResults,
    IReadOnlyList<CollectedProblem> Problems);

/// <summary>
/// Pure <see cref="IHarnessMapper"/> for Codex CLI rollout JSONL files, pairing <c>function_call</c>,
/// <c>custom_tool_call</c>, and <c>local_shell_call</c> items with <c>function_call_output</c>,
/// <c>custom_tool_call_output</c>, and <c>local_shell_call_output</c> by <c>call_id</c> (with <c>id</c> fallback)
/// into child <c>tool.invoke</c> <see cref="CollectedRecord"/>s.
/// </summary>
public class CodexRolloutMapper : IHarnessMapper
{
    private const string DefaultModel = "codex";
    private const string MalformedJsonCode = "MALFORMED_JSON_LINE";
    private const string UnknownEventTypeCode = "UNKNOWN_EVENT_TYPE";
    private const string DefaultToolErrorMessage = "Tool execution failed.";
    private const string IncompleteLocalShellErrorMessage = "Local shell call incomplete.";
    private const string LocalShellToolName = "local_shell";

    /// <inheritdoc />
    public string HarnessId => TranscriptReaderRepository.CodexHarnessId;

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        string harnessId = ResolveHarnessId(unit.Unit.HarnessId);
        CollectedSource source = new(harnessId, unit.Unit.Kind, unit.Unit.SourceKey);

        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        CodexRolloutDocument document = Parse(unit, importedAtUtc);
        return MapDocument(unit, harnessId, source, document);
    }

    /// <summary>
    /// Parses <paramref name="unit"/> into a strongly typed <see cref="CodexRolloutDocument"/>
    /// without touching the filesystem or ambient clock.
    /// </summary>
    /// <param name="unit">Loaded Codex rollout source unit.</param>
    /// <param name="importedAtUtc">Caller-supplied UTC import timestamp for problem metadata.</param>
    /// <returns>The parsed rollout document and any safe parse diagnostics.</returns>
    public CodexRolloutDocument Parse(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        string harnessId = ResolveHarnessId(unit.Unit.HarnessId);
        string importedAtText = FormatUtc(importedAtUtc);

        string? sessionId = null;
        string? systemPrompt = null;
        string? instructionContext = null;
        string? workingDirectory = null;
        string currentModel = DefaultModel;

        List<CodexRolloutMessage> pendingMessages = [];
        List<CodexRolloutToolCall> pendingCalls = [];
        List<CodexRolloutToolResult> pendingResults = [];
        List<CodexRolloutToolResult> allResults = [];
        string? terminationReason = null;
        int? exitCode = null;
        string? turnTimestamp = null;
        bool dirty = false;
        int turnIndex = 0;

        List<CodexRolloutTurn> turns = [];
        List<CollectedProblem> problems = [];

        CodexRolloutTurn FlushTurn(string? fallbackTimestamp, CodexRolloutUsage? usage)
        {
            turnIndex++;
            CodexRolloutTurn turn = new(
                TurnIndex: turnIndex,
                Timestamp: turnTimestamp ?? fallbackTimestamp,
                Model: usage?.Model ?? currentModel,
                TerminationReason: terminationReason,
                ExitCode: exitCode,
                Usage: usage,
                Messages: [.. pendingMessages],
                ToolCalls: [.. pendingCalls],
                ToolResults: [.. pendingResults]);

            pendingMessages.Clear();
            pendingCalls.Clear();
            pendingResults.Clear();
            terminationReason = null;
            exitCode = null;
            turnTimestamp = null;
            dirty = false;
            return turn;
        }

        using StringReader reader = new(unit.RawText ?? string.Empty);
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                problems.Add(new CollectedProblem(
                    ProblemKey: string.Create(CultureInfo.InvariantCulture, $"malformed-json:{unit.Unit.SourceKey}:{lineNumber}"),
                    Severity: "warning",
                    Code: MalformedJsonCode,
                    Message: string.Create(CultureInfo.InvariantCulture, $"Skipped malformed JSONL entry at line {lineNumber}."),
                    Location: unit.Unit.SourceKey,
                    HarnessId: harnessId,
                    TimestampUtc: importedAtText));
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new CollectedProblem(
                        ProblemKey: string.Create(CultureInfo.InvariantCulture, $"malformed-json:{unit.Unit.SourceKey}:{lineNumber}"),
                        Severity: "warning",
                        Code: MalformedJsonCode,
                        Message: string.Create(CultureInfo.InvariantCulture, $"Skipped non-object JSONL entry at line {lineNumber}."),
                        Location: unit.Unit.SourceKey,
                        HarnessId: harnessId,
                        TimestampUtc: importedAtText));
                    continue;
                }

                JsonElement entry = doc.RootElement;
                string? entryType = ReadString(entry, "type");
                string? entryTimestamp = ReadString(entry, "timestamp");

                if (!IsKnownTopLevelEntryType(entryType))
                {
                    string safeType = SafeEventTypeLabel(entryType);
                    problems.Add(new CollectedProblem(
                        ProblemKey: string.Create(CultureInfo.InvariantCulture, $"unknown-event:{unit.Unit.SourceKey}:{lineNumber}"),
                        Severity: "warning",
                        Code: UnknownEventTypeCode,
                        Message: string.Create(CultureInfo.InvariantCulture, $"Skipped unknown Codex rollout entry type '{safeType}' at line {lineNumber}."),
                        Location: unit.Unit.SourceKey,
                        HarnessId: harnessId,
                        TimestampUtc: importedAtText));
                    continue;
                }

                if (!entry.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (string.Equals(entryType, "session_meta", StringComparison.Ordinal))
                {
                    string? sid = ReadString(payload, "session_id") ?? ReadString(payload, "id");
                    if (!string.IsNullOrEmpty(sid))
                    {
                        sessionId = sid;
                    }

                    string? prompt = ReadCodexSystemPrompt(payload);
                    if (!string.IsNullOrEmpty(prompt))
                    {
                        systemPrompt = prompt;
                    }

                    string? modelFromMeta = ReadString(payload, "model");
                    if (!string.IsNullOrEmpty(modelFromMeta))
                    {
                        currentModel = modelFromMeta;
                    }

                    string? cwd = ReadString(payload, "cwd");
                    if (!string.IsNullOrEmpty(cwd))
                    {
                        workingDirectory = cwd;
                    }

                    turnTimestamp ??= entryTimestamp;
                    dirty = true;
                    continue;
                }

                if (string.Equals(entryType, "world_state", StringComparison.Ordinal))
                {
                    string? agentsMd = ReadCodexAgentsMd(payload);
                    if (!string.IsNullOrEmpty(agentsMd))
                    {
                        instructionContext = agentsMd;
                        turnTimestamp ??= entryTimestamp;
                        dirty = true;
                    }

                    continue;
                }

                if (string.Equals(entryType, "turn_context", StringComparison.Ordinal))
                {
                    string? contextModel = ReadString(payload, "model");
                    if (!string.IsNullOrEmpty(contextModel))
                    {
                        currentModel = contextModel;
                    }

                    string? cwd = ReadString(payload, "cwd");
                    if (!string.IsNullOrEmpty(cwd))
                    {
                        workingDirectory = cwd;
                    }

                    continue;
                }

                if (string.Equals(entryType, "compacted", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(entryType, "event_msg", StringComparison.Ordinal))
                {
                    string? eventType = ReadString(payload, "type");
                    if (string.Equals(eventType, "task_complete", StringComparison.Ordinal))
                    {
                        if (!dirty && turns.Count > 0)
                        {
                            CodexRolloutTurn last = turns[^1];
                            turns[^1] = last with { TerminationReason = "task_complete", ExitCode = 0 };
                        }
                        else
                        {
                            terminationReason = "task_complete";
                            exitCode = 0;
                            dirty = true;
                        }
                    }
                    else if (eventType is "task_failed" or "error")
                    {
                        int failCode = TryReadInt(payload, "exit_code", out int parsedCode) ? parsedCode : 1;
                        if (!dirty && turns.Count > 0)
                        {
                            CodexRolloutTurn last = turns[^1];
                            turns[^1] = last with { TerminationReason = eventType, ExitCode = failCode };
                        }
                        else
                        {
                            terminationReason = eventType;
                            exitCode = failCode;
                            dirty = true;
                        }
                    }
                    else if (eventType is "turn_aborted" or "cancelled")
                    {
                        if (!dirty && turns.Count > 0)
                        {
                            CodexRolloutTurn last = turns[^1];
                            turns[^1] = last with { TerminationReason = "aborted" };
                        }
                        else
                        {
                            terminationReason = "aborted";
                            dirty = true;
                        }
                    }
                    else if (string.Equals(eventType, "token_count", StringComparison.Ordinal))
                    {
                        CodexRolloutUsage? usage = TryExtractUsage(payload, currentModel, out string? detectedModel);
                        if (!string.IsNullOrEmpty(detectedModel))
                        {
                            currentModel = detectedModel;
                        }

                        turns.Add(FlushTurn(entryTimestamp, usage));
                    }

                    continue;
                }

                if (!string.Equals(entryType, "response_item", StringComparison.Ordinal))
                {
                    continue;
                }

                string? itemType = ReadString(payload, "type");
                if (string.Equals(itemType, "message", StringComparison.Ordinal))
                {
                    string? text = ReadCodexMessageText(payload);
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    string? role = ReadString(payload, "role");
                    CorrectionDetectionResult correction = CanonFindings.DetectUserCorrection(text);
                    pendingMessages.Add(new CodexRolloutMessage(
                        Role: role,
                        Text: text,
                        CorrectionRule: correction.Matched ? correction.Rule : null,
                        Timestamp: entryTimestamp));
                    turnTimestamp ??= entryTimestamp;
                    dirty = true;
                }
                else if (string.Equals(itemType, "function_call", StringComparison.Ordinal))
                {
                    string callId = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? string.Empty;
                    string? name = ReadString(payload, "name");
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    string rawArgs = ReadStringOrRawJson(payload, "arguments")
                        ?? ReadStringOrRawJson(payload, "input")
                        ?? "{}";
                    pendingCalls.Add(new CodexRolloutToolCall(
                        CallId: callId,
                        ToolName: name,
                        RawInputText: rawArgs,
                        IsCustomTool: false,
                        Timestamp: entryTimestamp));
                    turnTimestamp ??= entryTimestamp;
                    dirty = true;
                }
                else if (string.Equals(itemType, "custom_tool_call", StringComparison.Ordinal))
                {
                    string callId = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? string.Empty;
                    string? name = ReadString(payload, "name");
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    string rawInput = ReadStringOrRawJson(payload, "input")
                        ?? ReadStringOrRawJson(payload, "arguments")
                        ?? string.Empty;
                    pendingCalls.Add(new CodexRolloutToolCall(
                        CallId: callId,
                        ToolName: name,
                        RawInputText: rawInput,
                        IsCustomTool: true,
                        Timestamp: entryTimestamp));
                    turnTimestamp ??= entryTimestamp;
                    dirty = true;
                }
                else if (string.Equals(itemType, "local_shell_call", StringComparison.Ordinal))
                {
                    string callId = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? string.Empty;
                    string rawAction = ReadStringOrRawJson(payload, "action") ?? "{}";
                    string? rawStatus = ReadString(payload, "status");
                    string? normalizedStatus = NormalizeLocalShellStatus(rawStatus);
                    if (rawStatus is not null && normalizedStatus is null)
                    {
                        string safeStatus = SafeEventTypeLabel(rawStatus);
                        problems.Add(new CollectedProblem(
                            ProblemKey: string.Create(CultureInfo.InvariantCulture, $"unknown-local-shell-status:{unit.Unit.SourceKey}:{lineNumber}"),
                            Severity: "warning",
                            Code: UnknownEventTypeCode,
                            Message: string.Create(CultureInfo.InvariantCulture, $"Skipped unrecognized local_shell_call status '{safeStatus}' at line {lineNumber}."),
                            Location: unit.Unit.SourceKey,
                            HarnessId: harnessId,
                            TimestampUtc: importedAtText));
                    }

                    pendingCalls.Add(new CodexRolloutToolCall(
                        CallId: callId,
                        ToolName: LocalShellToolName,
                        RawInputText: rawAction,
                        IsCustomTool: false,
                        Timestamp: entryTimestamp,
                        Status: normalizedStatus,
                        WithholdInputPlainText: true));
                    turnTimestamp ??= entryTimestamp;
                    dirty = true;
                }
                else if (itemType is "function_call_output" or "custom_tool_call_output" or "local_shell_call_output")
                {
                    string callId = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? string.Empty;
                    string? toolName = ReadString(payload, "name");
                    string? outputText = ReadCodexToolResultText(payload);
                    long? toolExitCode = TryReadLong(payload, "exit_code", out long ec)
                        ? ec
                        : TryReadNestedExitCode(payload);
                    string? statusText = ReadString(payload, "status");
                    bool isError = ReadBool(payload, "is_error") == true
                        || string.Equals(statusText, "error", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(statusText, "failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(statusText, "incomplete", StringComparison.OrdinalIgnoreCase)
                        || (toolExitCode.HasValue && toolExitCode.Value != 0);

                    if (!string.IsNullOrEmpty(callId))
                    {
                        CodexRolloutToolResult result = new(
                            CallId: callId,
                            ToolName: toolName,
                            OutputText: outputText,
                            IsError: isError,
                            ExitCode: toolExitCode,
                            Timestamp: entryTimestamp);
                        pendingResults.Add(result);
                        allResults.Add(result);
                    }

                    if (turns.Count == 0 && !dirty)
                    {
                        turnTimestamp ??= entryTimestamp;
                    }
                }
            }
        }

        if (dirty || (turns.Count == 0 && (systemPrompt is not null || instructionContext is not null)))
        {
            turns.Add(FlushTurn(null, null));
        }

        return new CodexRolloutDocument(
            SessionId: sessionId,
            Model: currentModel,
            SystemPrompt: systemPrompt,
            InstructionContext: instructionContext,
            WorkingDirectory: workingDirectory,
            Turns: turns,
            AllToolResults: allResults,
            Problems: problems);
    }

    private static CollectedBatch MapDocument(
        NativeSourceUnit unit,
        string harnessId,
        CollectedSource source,
        CodexRolloutDocument document)
    {
        if (document.Turns.Count == 0 && string.IsNullOrWhiteSpace(document.SessionId))
        {
            return new CollectedBatch(source, [], [], document.Problems);
        }

        string fileStem = Path.GetFileNameWithoutExtension(unit.Unit.SourceKey);
        string nativeSessionId = SafeOptionalText(document.SessionId)
            ?? (string.IsNullOrWhiteSpace(fileStem) ? "codex-session" : fileStem);
        string sessionKey = nativeSessionId;
        string traceId = $"synth:{harnessId}:{nativeSessionId}";

        CollectedWorkspace? workspace = SafeOptionalText(document.WorkingDirectory) is string cwd
            ? new CollectedWorkspace(cwd, null, null)
            : null;

        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: sessionKey,
                HarnessId: harnessId,
                NativeSessionId: nativeSessionId,
                Workspace: workspace,
                Title: null,
                ReportedUsage: []),
        ];

        Dictionary<string, Queue<CodexRolloutToolResult>> resultsByCallId = BuildResultLookup(document.AllToolResults);
        DateTimeOffset fallbackTimestamp = ResolveFallbackTimestamp(document.Turns);

        List<CollectedRecord> records = [];
        HashSet<string> usedRecordIds = new(StringComparer.Ordinal);

        for (int i = 0; i < document.Turns.Count; i++)
        {
            CodexRolloutTurn turn = document.Turns[i];
            string parentRecordId = EnsureUniqueRecordId(
                usedRecordIds,
                string.Create(CultureInfo.InvariantCulture, $"synth:{harnessId}:{nativeSessionId}:turn-{turn.TurnIndex}"));

            DateTimeOffset turnStartedAt = ParseTimestampOrDefault(turn.Timestamp, fallbackTimestamp);
            RecordStatus turnStatus = MapTurnStatus(turn.TerminationReason, turn.ExitCode);
            string? turnModel = SafeOptionalText(turn.Model ?? document.Model);
            string turnName = turnModel is not null ? $"codex:{turnModel}" : "codex:codex";

            List<CollectedTokenCount> tokens = BuildTurnTokens(turn.Usage);
            List<CollectedContentPart> turnParts = BuildTurnParts(document, turn, includeSessionHeaders: i == 0);

            string? correctionRule = null;
            foreach (CodexRolloutMessage msg in turn.Messages)
            {
                if (!string.IsNullOrWhiteSpace(msg.CorrectionRule))
                {
                    correctionRule = msg.CorrectionRule;
                    break;
                }
            }

            string? turnError = turnStatus == RecordStatus.Error
                ? string.Create(CultureInfo.InvariantCulture, $"Codex turn ended with '{SafeOptionalText(turn.TerminationReason) ?? "error"}'.")
                : null;

            CollectedRecord parentRecord = new(
                RecordId: parentRecordId,
                HarnessId: harnessId,
                Op: RecordOp.LlmInvoke,
                Kind: RecordKind.Internal,
                Name: turnName,
                StartedAt: turnStartedAt,
                Status: turnStatus,
                SessionKey: sessionKey,
                ParentRecordId: null,
                TraceId: traceId,
                Model: turnModel,
                EndedAt: null,
                ReportedDurationMs: null,
                Tokens: tokens,
                ReportedCost: null,
                Parts: turnParts,
                Tool: null,
                Agent: null,
                SkillsInvoked: [],
                AgentInstanceId: null,
                ToolDefinitions: [],
                Signals: new CollectedRecordSignals(turnError, null, correctionRule, null),
                ReportedInputTokens: turn.Usage?.InputTokens,
                ReportedOutputTokens: turn.Usage?.OutputTokens);

            records.Add(parentRecord);

            for (int t = 0; t < turn.ToolCalls.Count; t++)
            {
                CodexRolloutToolCall toolCall = turn.ToolCalls[t];
                CodexRolloutToolResult? matched = DequeueResult(resultsByCallId, toolCall.CallId);
                CollectedRecord child = BuildToolInvokeRecord(
                    parentRecord,
                    harnessId,
                    sessionKey,
                    traceId,
                    turn.TurnIndex,
                    t,
                    toolCall,
                    matched,
                    usedRecordIds);
                records.Add(child);
            }
        }

        return new CollectedBatch(source, sessions, records, document.Problems);
    }

    private static CollectedRecord BuildToolInvokeRecord(
        CollectedRecord parentRecord,
        string harnessId,
        string sessionKey,
        string traceId,
        int turnIndex,
        int toolIndex,
        CodexRolloutToolCall toolCall,
        CodexRolloutToolResult? matchedResult,
        HashSet<string> usedRecordIds)
    {
        string safeCallId = SafeOptionalText(toolCall.CallId)
            ?? string.Create(CultureInfo.InvariantCulture, $"call-{turnIndex}-{toolIndex}");
        string safeToolName = SafeOptionalText(toolCall.ToolName) ?? "tool";

        string toolKeyId = !string.IsNullOrEmpty(toolCall.CallId)
            ? toolCall.CallId
            : toolIndex.ToString(CultureInfo.InvariantCulture);
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{toolKeyId}:{toolCall.ToolName}:{toolCall.RawInputText}"));
        string toolHash = Convert.ToHexStringLower(hashBytes)[..12];
        string childRecordId = EnsureUniqueRecordId(usedRecordIds, $"{parentRecord.RecordId}-t{toolHash}");

        DateTimeOffset startedAt = ParseTimestampOrDefault(toolCall.Timestamp, parentRecord.StartedAt);
        DateTimeOffset? endedAt = null;
        long? durationMs = null;

        if (!string.IsNullOrEmpty(toolCall.Timestamp)
            && !string.IsNullOrEmpty(matchedResult?.Timestamp)
            && TryParseTimestamp(toolCall.Timestamp, out DateTimeOffset parsedStart)
            && TryParseTimestamp(matchedResult.Timestamp, out DateTimeOffset parsedEnd))
        {
            long diffMs = (long)Math.Round((parsedEnd - parsedStart).TotalMilliseconds);
            if (diffMs >= 0)
            {
                endedAt = parsedEnd;
                durationMs = diffMs;
            }
        }

        (RecordStatus status, bool? succeeded, string? errorMessage) = ResolveToolOutcome(toolCall, matchedResult);

        ToolContentMeasurement inputMeasurement = ToolContentMeasurer.Measure(toolCall.RawInputText);
        CollectedToolContent inputContent = toolCall.WithholdInputPlainText
            ? inputMeasurement.ToToolContent(includeFingerprint: true) with { PlainText = null, OriginalUtf8BytesWhenTruncated = null }
            : inputMeasurement.ToToolContent(includeFingerprint: true);

        CollectedToolContent? resultContent = null;
        List<CollectedContentPart> parts = [];
        if (matchedResult?.OutputText is not null)
        {
            ToolContentMeasurement resultMeasurement = ToolContentMeasurer.Measure(matchedResult.OutputText);
            resultContent = resultMeasurement.ToToolContent(includeFingerprint: false);

            string? plainText = resultMeasurement.IsDocument ? null : resultMeasurement.PlainText;
            CollectedWithheldText? withheld = resultMeasurement.IsDocument ? resultMeasurement.ToWithheldText() : null;
            long? originalBytes = resultMeasurement.IsDocument ? null : resultMeasurement.OriginalUtf8BytesWhenTruncated;

            parts.Add(new CollectedContentPart(
                Position: 0,
                Bucket: ContentBucket.ToolResultContent,
                PlainText: plainText,
                Withheld: withheld,
                ReportedTokens: null,
                Server: null,
                Role: "tool",
                Resident: null,
                OriginalUtf8BytesWhenTruncated: originalBytes));
        }

        CollectedError? error = errorMessage is not null ? new CollectedError(errorMessage, null) : null;

        CollectedToolCall collectedTool = new(
            CallId: safeCallId,
            ToolName: safeToolName,
            McpServerName: null,
            McpToolName: null,
            Input: inputContent,
            Result: resultContent,
            Succeeded: succeeded,
            Error: error,
            ExitCode: matchedResult?.ExitCode);

        return new CollectedRecord(
            RecordId: childRecordId,
            HarnessId: harnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: safeToolName,
            StartedAt: startedAt,
            Status: status,
            SessionKey: sessionKey,
            ParentRecordId: parentRecord.RecordId,
            TraceId: traceId,
            Model: null,
            EndedAt: endedAt,
            ReportedDurationMs: durationMs,
            Tokens: [],
            ReportedCost: null,
            Parts: parts,
            Tool: collectedTool,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(errorMessage, null, null, null));
    }

    private static List<CollectedTokenCount> BuildTurnTokens(CodexRolloutUsage? usage)
    {
        if (usage is null)
        {
            return [];
        }

        long freshInput = Math.Max(0L, usage.InputTokens - usage.CachedInputTokens);
        long cacheRead = Math.Max(0L, usage.CachedInputTokens);
        long output = Math.Max(0L, usage.OutputTokens);

        List<CollectedTokenCount> tokens =
        [
            new CollectedTokenCount(TokenClass.Input, freshInput),
            new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
            new CollectedTokenCount(TokenClass.Output, output),
        ];

        if (usage.ReasoningTokens > 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Reasoning, usage.ReasoningTokens));
        }

        return tokens;
    }

    private static List<CollectedContentPart> BuildTurnParts(
        CodexRolloutDocument document,
        CodexRolloutTurn turn,
        bool includeSessionHeaders)
    {
        List<CollectedContentPart> parts = [];
        int position = 0;

        if (includeSessionHeaders)
        {
            if (document.SystemPrompt is not null)
            {
                parts.Add(MeasurePart(position++, ContentBucket.SystemPrompt, document.SystemPrompt, "system"));
            }

            if (document.InstructionContext is not null)
            {
                parts.Add(MeasurePart(position++, ContentBucket.InstructionContext, document.InstructionContext, "system"));
            }
        }

        foreach (CodexRolloutMessage message in turn.Messages)
        {
            parts.Add(MeasurePart(position++, ContentBucket.ConversationHistory, message.Text, SafeOptionalText(message.Role)));
        }

        return parts;
    }

    private static CollectedContentPart MeasurePart(
        int position,
        ContentBucket bucket,
        string text,
        string? role)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(text);
        string? plainText = measurement.IsDocument ? null : measurement.PlainText;
        CollectedWithheldText? withheld = measurement.IsDocument ? measurement.ToWithheldText() : null;
        long? originalBytes = measurement.IsDocument ? null : measurement.OriginalUtf8BytesWhenTruncated;

        return new CollectedContentPart(
            Position: position,
            Bucket: bucket,
            PlainText: plainText,
            Withheld: withheld,
            ReportedTokens: null,
            Server: null,
            Role: role,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: originalBytes);
    }

    private static Dictionary<string, Queue<CodexRolloutToolResult>> BuildResultLookup(IReadOnlyList<CodexRolloutToolResult> results)
    {
        Dictionary<string, Queue<CodexRolloutToolResult>> lookup = new(StringComparer.Ordinal);
        foreach (CodexRolloutToolResult result in results)
        {
            if (string.IsNullOrEmpty(result.CallId))
            {
                continue;
            }

            if (!lookup.TryGetValue(result.CallId, out Queue<CodexRolloutToolResult>? queue))
            {
                queue = new Queue<CodexRolloutToolResult>();
                lookup[result.CallId] = queue;
            }

            queue.Enqueue(result);
        }

        return lookup;
    }

    private static CodexRolloutToolResult? DequeueResult(
        Dictionary<string, Queue<CodexRolloutToolResult>> resultsByCallId,
        string callId)
    {
        if (string.IsNullOrEmpty(callId))
        {
            return null;
        }

        if (resultsByCallId.TryGetValue(callId, out Queue<CodexRolloutToolResult>? queue) && queue.Count > 0)
        {
            return queue.Dequeue();
        }

        return null;
    }

    private static string EnsureUniqueRecordId(HashSet<string> usedRecordIds, string candidate) =>
        HarnessJson.EnsureUniqueRecordId(usedRecordIds, candidate);

    private static RecordStatus MapTurnStatus(string? terminationReason, int? exitCode)
    {
        if (exitCode is not null and not 0)
        {
            return RecordStatus.Error;
        }

        if (string.Equals(terminationReason, "task_complete", StringComparison.Ordinal))
        {
            return RecordStatus.Ok;
        }

        return RecordStatus.Unspecified;
    }

    private static DateTimeOffset ResolveFallbackTimestamp(IReadOnlyList<CodexRolloutTurn> turns)
    {
        foreach (CodexRolloutTurn turn in turns)
        {
            if (TryParseTimestamp(turn.Timestamp, out DateTimeOffset parsed))
            {
                return parsed;
            }

            foreach (CodexRolloutToolCall call in turn.ToolCalls)
            {
                if (TryParseTimestamp(call.Timestamp, out DateTimeOffset callTs))
                {
                    return callTs;
                }
            }

            foreach (CodexRolloutMessage message in turn.Messages)
            {
                if (TryParseTimestamp(message.Timestamp, out DateTimeOffset msgTs))
                {
                    return msgTs;
                }
            }
        }

        return DateTimeOffset.UnixEpoch;
    }

    private static DateTimeOffset ParseTimestampOrDefault(string? timestamp, DateTimeOffset fallback) =>
        TryParseTimestamp(timestamp, out DateTimeOffset parsed) ? parsed : fallback;

    private static bool TryParseTimestamp(string? timestamp, out DateTimeOffset parsed) =>
        HarnessJson.TryParseTimestamp(timestamp, out parsed);

    private static string ResolveHarnessId(string? unitHarnessId) =>
        HarnessJson.ResolveCodexHarnessId(unitHarnessId);

    private static string? NormalizeLocalShellStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return "completed";
        }

        if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
        {
            return "incomplete";
        }

        if (string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase))
        {
            return "in_progress";
        }

        return null;
    }

    private static (RecordStatus Status, bool? Succeeded, string? ErrorMessage) ResolveToolOutcome(
        CodexRolloutToolCall toolCall,
        CodexRolloutToolResult? matchedResult)
    {
        if (string.Equals(toolCall.Status, "in_progress", StringComparison.Ordinal))
        {
            return (RecordStatus.Unset, null, null);
        }

        if (string.Equals(toolCall.Status, "incomplete", StringComparison.Ordinal))
        {
            string message = matchedResult?.OutputText is not null
                ? SafeErrorMessage(matchedResult.OutputText)
                : IncompleteLocalShellErrorMessage;
            return (RecordStatus.Error, false, message);
        }

        if (string.Equals(toolCall.Status, "completed", StringComparison.Ordinal))
        {
            if (matchedResult is { IsError: true })
            {
                return (RecordStatus.Error, false, SafeErrorMessage(matchedResult.OutputText));
            }

            return (RecordStatus.Ok, true, null);
        }

        if (matchedResult is null)
        {
            return (RecordStatus.Unset, null, null);
        }

        if (matchedResult.IsError)
        {
            return (RecordStatus.Error, false, SafeErrorMessage(matchedResult.OutputText));
        }

        return (RecordStatus.Ok, true, null);
    }

    private static bool IsKnownTopLevelEntryType(string? entryType) =>
        entryType is "session_meta"
            or "world_state"
            or "turn_context"
            or "compacted"
            or "event_msg"
            or "response_item";

    private static string SafeEventTypeLabel(string? entryType)
    {
        if (string.IsNullOrWhiteSpace(entryType) || JsonDocumentText.IsDocument(entryType))
        {
            return "unknown";
        }

        return entryType.Length > 64 ? entryType[..64] : entryType;
    }

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    private static string SafeErrorMessage(string? outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText) || JsonDocumentText.IsDocument(outputText))
        {
            return DefaultToolErrorMessage;
        }

        return outputText.Length > 512 ? outputText[..512] : outputText;
    }

    private static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string? ReadCodexSystemPrompt(JsonElement payload) =>
        HarnessJson.ReadCodexSystemPrompt(payload);

    private static string? ReadCodexAgentsMd(JsonElement payload) =>
        HarnessJson.ReadCodexAgentsMd(payload);

    private static string? ReadCodexMessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            string? text = content.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> texts = [];
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? text = ReadString(block, "text");
            if (!string.IsNullOrEmpty(text))
            {
                texts.Add(text);
            }
        }

        return texts.Count > 0 ? string.Join('\n', texts) : null;
    }

    private static string? ReadCodexToolResultText(JsonElement payload)
    {
        if (!payload.TryGetProperty("output", out JsonElement output))
        {
            return null;
        }

        if (output.ValueKind == JsonValueKind.String)
        {
            return output.GetString();
        }

        if (output.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return output.GetRawText();
        }

        return null;
    }

    private static CodexRolloutUsage? TryExtractUsage(
        JsonElement payload,
        string fallbackModel,
        out string? detectedModel)
    {
        detectedModel = null;

        JsonElement usageContainer = default;
        bool hasUsage = false;
        if (payload.TryGetProperty("info", out JsonElement infoEl) && infoEl.ValueKind == JsonValueKind.Object)
        {
            detectedModel = ReadString(infoEl, "model");
            if (infoEl.TryGetProperty("last_token_usage", out JsonElement lastUsage) && lastUsage.ValueKind == JsonValueKind.Object)
            {
                usageContainer = lastUsage;
                hasUsage = true;
            }
            else if (infoEl.TryGetProperty("total_token_usage", out JsonElement totalUsage) && totalUsage.ValueKind == JsonValueKind.Object)
            {
                usageContainer = totalUsage;
                hasUsage = true;
            }
        }
        else if (payload.TryGetProperty("usage", out JsonElement directUsage) && directUsage.ValueKind == JsonValueKind.Object)
        {
            usageContainer = directUsage;
            hasUsage = true;
        }

        if (!hasUsage)
        {
            return null;
        }

        string model = detectedModel ?? ReadString(payload, "model") ?? fallbackModel;
        long inputTokens = ReadLong(usageContainer, "input_tokens");
        long cachedInputTokens = ReadLong(usageContainer, "cached_input_tokens");
        long outputTokens = ReadLong(usageContainer, "output_tokens");
        long reasoningTokens = ReadLong(usageContainer, "reasoning_output_tokens");

        return new CodexRolloutUsage(model, inputTokens, cachedInputTokens, outputTokens, reasoningTokens);
    }

    private static string? ReadStringOrRawJson(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }

        if (el.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return el.GetRawText();
        }

        return null;
    }

    private static string? ReadString(JsonElement obj, string key) =>
        HarnessJson.ReadString(obj, key);

    private static bool? ReadBool(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static long ReadLong(JsonElement obj, string key) =>
        TryReadLong(obj, key, out long val) ? val : 0L;

    private static bool TryReadLong(JsonElement obj, string key, out long value)
    {
        value = 0L;
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return el.TryGetInt64(out value);
    }

    private static bool TryReadInt(JsonElement obj, string key, out int value) =>
        HarnessJson.TryReadInt(obj, key, out value);

    private static long? TryReadNestedExitCode(JsonElement payload)
    {
        if (payload.TryGetProperty("metadata", out JsonElement meta)
            && meta.ValueKind == JsonValueKind.Object
            && TryReadLong(meta, "exit_code", out long exitCode))
        {
            return exitCode;
        }

        return null;
    }
}

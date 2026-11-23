using System.Globalization;
using System.Text.RegularExpressions;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Shared OTLP span-mapping pipeline behind the harness <see cref="OtlpSpanNative"/> mappers.
/// Every mapper keeps its own wire contract (operation routing, token convention, session/model
/// resolution, and any pre/post span quirks) and delegates the identical batch/record/tool/part
/// assembly here through <see cref="OtlpMapperHooks"/> so the wire behavior stays in one place.
/// </summary>
internal static partial class OtlpSpanMapperCore
{
    internal const string DefaultToolErrorMessage = "Tool execution failed.";
    internal const string DefaultAgentErrorMessage = "Agent invocation failed.";

    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvokeAgentNameRegex();

    /// <summary>
    /// Classifies a span by the operation signals every harness shares: a tool-name attribute is a
    /// tool invocation, an <c>invoke_agent</c> operation or span name is an agent invocation, and
    /// token-count attributes are a model invocation. Mappers with a harness-specific routing rule
    /// run it after their own checks so the shared signals classify identically everywhere.
    /// </summary>
    /// <param name="native">Typed native OTLP span.</param>
    /// <returns>The classified operation.</returns>
    internal static RecordOp ResolveGenericOp(OtlpSpanNative native)
    {
        if (OtlpSpanNative.HasAnyKey(native.Attributes, ["gen_ai.tool.name"]))
        {
            return RecordOp.ToolInvoke;
        }

        if (string.Equals(native.OperationName, "invoke_agent", StringComparison.Ordinal)
            || InvokeAgentNameRegex().IsMatch(native.Name))
        {
            return RecordOp.AgentInvoke;
        }

        if (OtlpSpanNative.HasAnyKey(native.Attributes, OtlpSpanNative.InputTokenKeys)
            || OtlpSpanNative.HasAnyKey(native.Attributes, OtlpSpanNative.OutputTokenKeys))
        {
            return RecordOp.LlmInvoke;
        }

        return RecordOp.Unspecified;
    }

    /// <summary>Per-harness token-convention hook shared by the span mappers.</summary>
    internal delegate bool TryBuildTokenUsageHandler(OtlpSpanNative native, RecordOp op, out TokenUsage? usage);

    /// <summary>
    /// Per-harness variation points for <see cref="OtlpSpanMapperCore"/>. Only
    /// <see cref="ResolveOp"/> and <see cref="TryBuildTokenUsage"/> are required; every other hook
    /// falls back to the canonical <see cref="OtlpSpanNative"/> property when left
    /// <see langword="null"/>.
    /// </summary>
    internal sealed record OtlpMapperHooks(
        Func<OtlpSpanNative, RecordOp> ResolveOp,
        TryBuildTokenUsageHandler TryBuildTokenUsage,
        Func<OtlpSpanNative, string?>? ResolveSessionKey = null,
        Func<OtlpSpanNative, string?>? ResolveModel = null,
        Func<OtlpSpanNative, string?>? ResolveToolName = null,
        Func<OtlpSpanNative, string?>? ResolveToolCallId = null,
        Func<OtlpSpanNative, string?>? ResolveToolErrorCode = null,
        Func<OtlpSpanNative, string?>? ResolveExtraToolStatus = null,
        Func<OtlpSpanNative, double?>? ResolveCostUsd = null,
        Func<OtlpSpanNative, string, (string? Server, string? Tool)?>? EnrichMcpNames = null,
        Action<OtlpSpanNative, List<CollectedContentPart>>? AppendParts = null,
        Func<OtlpSpanNative, bool>? ShouldSkipSpan = null);

    /// <summary>
    /// Maps a batch of decoded OTLP <paramref name="spans"/> into sessions, validated records, and
    /// token/ingest problems using the harness <paramref name="hooks"/>.
    /// </summary>
    internal static CollectedBatch MapSpans(
        IReadOnlyList<OtlpSpan> spans,
        Func<OtlpSpan, OtlpSpanNative> parse,
        string? sourceLocator,
        string harnessId,
        OtlpMapperHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(hooks);

        string locator = SafeOptionalText(sourceLocator)
            ?? (spans.Count > 0 ? parse(spans[0]).Source : "otlp");
        CollectedSource source = new(harnessId, SourceKind.Otlp, locator);

        if (spans.Count == 0)
        {
            return new CollectedBatch(source, [], [], []);
        }

        Dictionary<string, CollectedSession> sessions = new(StringComparer.Ordinal);
        List<CollectedRecord> records = [];
        List<CollectedProblem> problems = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);
        HashSet<string> usedProblemKeys = new(StringComparer.Ordinal);

        for (int index = 0; index < spans.Count; index++)
        {
            OtlpSpan span = spans[index];
            ArgumentNullException.ThrowIfNull(span);

            OtlpSpanNative native = parse(span);
            if (hooks.ShouldSkipSpan?.Invoke(native) == true)
            {
                continue;
            }

            if (!seenRecordIds.Add(native.SpanId))
            {
                continue;
            }

            if (hooks.TryBuildTokenUsage(native, hooks.ResolveOp(native), out TokenUsage? usage) && usage is not null)
            {
                Problem? validationProblem = CanonValidation.ValidateTokens(usage, native.SpanId);
                if (validationProblem is not null)
                {
                    string problemKey = CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp:", native.SpanId, validationProblem.Code, index);
                    problems.Add(new CollectedProblem(
                        ProblemKey: problemKey,
                        Severity: FormatProblemSeverity(validationProblem.Severity),
                        Code: validationProblem.Code,
                        Message: SafeOptionalText(validationProblem.Message) ?? "Span failed token validation.",
                        SpanId: native.SpanId,
                        Location: SafeOptionalText(validationProblem.Location) ?? native.SpanId,
                        SessionKey: SafeOptionalText(ResolveSessionKey(hooks, native)),
                        HarnessId: harnessId,
                        TimestampUtc: native.StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                        Stack: SafeOptionalText(validationProblem.Stack)));
                    continue;
                }
            }

            CollectedRecord record = MapNative(native, harnessId, hooks);
            records.Add(record);

            if (!string.IsNullOrWhiteSpace(record.SessionKey) && !sessions.ContainsKey(record.SessionKey))
            {
                sessions[record.SessionKey] = new CollectedSession(
                    SessionKey: record.SessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: record.SessionKey,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []);
            }
        }

        return new CollectedBatch(source, [.. sessions.Values], records, problems);
    }

    /// <summary>Maps a single decoded OTLP <paramref name="native"/> span into a normalized record.</summary>
    internal static CollectedRecord MapNative(OtlpSpanNative native, string harnessId, OtlpMapperHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(hooks);

        RecordOp op = hooks.ResolveOp(native);
        RecordKind kind = CanonRecordVocabulary.ParseRecordKind(native.Kind);
        RecordStatus status = ResolveStatus(native, op, ResolveExtraToolStatus(hooks, native));
        string opWire = CanonRecordVocabulary.FormatRecordOp(op);
        string name = !string.IsNullOrWhiteSpace(native.Name) && !JsonDocumentText.IsDocument(native.Name)
            ? native.Name
            : opWire;

        List<CollectedTokenCount> tokens = [];
        long? reportedInputTokens = null;
        long? reportedOutputTokens = null;
        if (hooks.TryBuildTokenUsage(native, op, out TokenUsage? usage) && usage is not null)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, Math.Max(0L, usage.FreshInput)));
            tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, Math.Max(0L, usage.CacheRead)));
            tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, Math.Max(0L, usage.CacheCreation)));
            tokens.Add(new CollectedTokenCount(TokenClass.Output, Math.Max(0L, usage.Output)));
            if (usage.Reasoning is long reasoning && reasoning > 0)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.Reasoning, reasoning));
            }

            reportedInputTokens = usage.ReportedInput >= 0 ? usage.ReportedInput : null;
            reportedOutputTokens = usage.ReportedOutput >= 0 ? usage.ReportedOutput : null;
        }

        CollectedCost? cost = ResolveCostUsd(hooks, native) is double costValue
            && double.IsFinite(costValue)
            && costValue >= 0
            ? new CollectedCost(costValue, "USD")
            : null;

        List<CollectedContentPart> parts = BuildCollectedParts(native);
        hooks.AppendParts?.Invoke(native, parts);

        CollectedToolCall? tool = null;
        if (op == RecordOp.ToolInvoke)
        {
            tool = BuildToolCall(native, name, status, parts, hooks);
        }

        CollectedAgentInvocation? agent = null;
        if (op == RecordOp.AgentInvoke)
        {
            agent = BuildAgentInvocation(native, name, status);
        }

        string? errorMessage = SafeOptionalText(native.ErrorMessage)
            ?? (status == RecordStatus.Error ? SafeOptionalText(native.StatusMessage) : null);

        CollectedRecordSignals signals = new(
            ErrorMessage: errorMessage,
            EditFailures: native.EditFailures,
            CorrectionRule: SafeOptionalText(native.CorrectionRule),
            DeclaredContextWindow: native.DeclaredContextWindow);

        return new CollectedRecord(
            RecordId: native.SpanId,
            HarnessId: harnessId,
            Op: op,
            Kind: kind,
            Name: name,
            StartedAt: native.StartedAt,
            Status: status,
            SessionKey: SafeOptionalText(ResolveSessionKey(hooks, native)),
            ParentRecordId: SafeOptionalText(native.ParentSpanId),
            TraceId: SafeOptionalText(native.TraceId),
            Model: SafeOptionalText(ResolveModel(hooks, native)),
            EndedAt: native.EndedAt,
            ReportedDurationMs: native.ReportedDurationMs,
            Tokens: tokens,
            ReportedCost: cost,
            Parts: parts,
            Tool: tool,
            Agent: agent,
            SkillsInvoked: native.SkillsInvoked,
            AgentInstanceId: SafeOptionalText(native.AgentInstanceId),
            ToolDefinitions: [],
            Signals: signals,
            ReportedInputTokens: reportedInputTokens,
            ReportedOutputTokens: reportedOutputTokens);
    }

    /// <summary>Maps collected content parts, dropping wire buckets the canon store does not keep.</summary>
    internal static List<CollectedContentPart> BuildCollectedParts(OtlpSpanNative native)
    {
        ArgumentNullException.ThrowIfNull(native);

        List<CollectedContentPart> parts = [];
        HashSet<int> usedPositions = [];
        int nextPosition = 0;

        foreach (ContentPart part in native.Parts)
        {
            if (!ContentBuckets.TryMap(part.Part, out ContentBucket bucket))
            {
                continue;
            }

            int position = part.Order is >= 0 ? part.Order.Value : nextPosition;
            while (!usedPositions.Add(position))
            {
                position++;
            }

            nextPosition = Math.Max(nextPosition, position + 1);

            ToolContentMeasurement measurement = ToolContentMeasurer.Measure(part.Text ?? string.Empty);
            string? plainText = measurement.IsDocument ? null : measurement.PlainText;
            CollectedWithheldText? withheld = measurement.IsDocument ? measurement.ToWithheldText() : null;
            long? originalBytes = measurement.IsDocument ? null : measurement.OriginalUtf8BytesWhenTruncated;

            parts.Add(new CollectedContentPart(
                Position: position,
                Bucket: bucket,
                PlainText: plainText,
                Withheld: withheld,
                ReportedTokens: part.Tokens is >= 0 ? part.Tokens : null,
                Server: SafeOptionalText(part.Server),
                Role: SafeOptionalText(part.Role),
                Resident: part.Resident,
                OriginalUtf8BytesWhenTruncated: originalBytes));
        }

        return parts;
    }

    /// <summary>Builds the tool-call payload for a tool span, appending an unseen result part.</summary>
    internal static CollectedToolCall BuildToolCall(
        OtlpSpanNative native,
        string fallbackName,
        RecordStatus status,
        List<CollectedContentPart> parts,
        OtlpMapperHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(hooks);

        string callId = SafeOptionalText(ResolveToolCallId(hooks, native)) ?? native.SpanId;
        string toolName = SafeOptionalText(ResolveToolName(hooks, native)) ?? fallbackName;

        string? mcpServerName = SafeOptionalText(native.ToolMcpServerName);
        string? mcpToolName = SafeOptionalText(native.ToolMcpToolName);
        var enrichedMcpNames = hooks.EnrichMcpNames?.Invoke(native, toolName);
        if ((mcpServerName is null || mcpToolName is null) && enrichedMcpNames is not null)
        {
            mcpServerName ??= enrichedMcpNames.Value.Server;
            mcpToolName ??= enrichedMcpNames.Value.Tool;
        }

        if (mcpServerName is not null && mcpToolName is null)
        {
            mcpToolName = toolName;
        }
        else if (mcpServerName is null)
        {
            mcpToolName = null;
        }

        CollectedToolContent? input = native.ToolRawInput is not null
            ? ToolContentMeasurer.Measure(native.ToolRawInput).ToToolContent(includeFingerprint: true)
            : null;

        CollectedToolContent? result = null;
        if (native.ToolRawResult is not null)
        {
            ToolContentMeasurement resultMeasurement = ToolContentMeasurer.Measure(native.ToolRawResult);
            result = resultMeasurement.ToToolContent(includeFingerprint: false);

            bool hasResultPart = parts.Any(static p => p.Bucket == ContentBucket.ToolResultContent);
            if (!hasResultPart)
            {
                int nextPos = parts.Count == 0 ? 0 : parts.Max(static p => p.Position) + 1;
                parts.Add(new CollectedContentPart(
                    Position: nextPos,
                    Bucket: ContentBucket.ToolResultContent,
                    PlainText: resultMeasurement.IsDocument ? null : resultMeasurement.PlainText,
                    Withheld: resultMeasurement.IsDocument ? resultMeasurement.ToWithheldText() : null,
                    ReportedTokens: null,
                    Server: mcpServerName,
                    Role: "tool",
                    Resident: null,
                    OriginalUtf8BytesWhenTruncated: resultMeasurement.IsDocument ? null : resultMeasurement.OriginalUtf8BytesWhenTruncated));
            }
        }

        bool? succeeded = status switch
        {
            RecordStatus.Error => false,
            RecordStatus.Ok => true,
            _ => null,
        };

        string? errorCode = SafeOptionalText(ResolveToolErrorCode(hooks, native));

        CollectedError? error = null;
        if (succeeded == false)
        {
            string message = SafeOptionalText(native.ErrorMessage)
                ?? SafeOptionalText(native.StatusMessage)
                ?? DefaultToolErrorMessage;
            error = new CollectedError(message, errorCode);
        }

        return new CollectedToolCall(
            CallId: callId,
            ToolName: toolName,
            McpServerName: mcpServerName,
            McpToolName: mcpToolName,
            Input: input,
            Result: result,
            Succeeded: succeeded,
            Error: error,
            ExitCode: native.ToolExitCode);
    }

    /// <summary>Builds the agent-invocation payload for an agent span.</summary>
    internal static CollectedAgentInvocation BuildAgentInvocation(
        OtlpSpanNative native,
        string fallbackName,
        RecordStatus status)
    {
        ArgumentNullException.ThrowIfNull(native);

        string agentName = SafeOptionalText(native.AgentName)
            ?? ExtractInvokeAgentSuffix(fallbackName)
            ?? fallbackName;

        string? spawningCallId = SafeOptionalText(native.AgentSpawningCallId)
            ?? SafeOptionalText(native.ParentSpanId);

        bool? succeeded = status switch
        {
            RecordStatus.Error => false,
            RecordStatus.Ok => true,
            _ => null,
        };

        string? error = SafeOptionalText(native.AgentError);
        if (succeeded == false)
        {
            error ??= SafeOptionalText(native.ErrorMessage)
                ?? SafeOptionalText(native.StatusMessage)
                ?? DefaultAgentErrorMessage;
        }

        return new CollectedAgentInvocation(
            AgentName: agentName,
            SpawningCallId: spawningCallId,
            Succeeded: succeeded,
            Error: error);
    }

    /// <summary>
    /// Resolves the record status, letting tool/agent outcome attributes promote an unset base
    /// status. <paramref name="extraToolStatus"/> carries a harness-specific tool status attribute
    /// (Factory Droid) alongside the canonical one.
    /// </summary>
    internal static RecordStatus ResolveStatus(OtlpSpanNative native, RecordOp op, string? extraToolStatus = null)
    {
        ArgumentNullException.ThrowIfNull(native);

        RecordStatus baseStatus = CanonRecordVocabulary.ParseRecordStatus(native.StatusCode);
        if (baseStatus == RecordStatus.Error)
        {
            return RecordStatus.Error;
        }

        if (op == RecordOp.ToolInvoke)
        {
            if (native.ToolIsError == true
                || IsErrorStatus(native.ToolStatus)
                || IsErrorStatus(extraToolStatus)
                || (native.ToolExitCode is not null and not 0))
            {
                return RecordStatus.Error;
            }

            if (baseStatus is RecordStatus.Unset or RecordStatus.Unspecified
                && (native.ToolIsError == false
                    || IsOkStatus(native.ToolStatus)
                    || IsOkStatus(extraToolStatus)))
            {
                return RecordStatus.Ok;
            }
        }

        if (op == RecordOp.AgentInvoke)
        {
            if (string.Equals(native.AgentStatus, "error", StringComparison.OrdinalIgnoreCase))
            {
                return RecordStatus.Error;
            }

            if (baseStatus is RecordStatus.Unset or RecordStatus.Unspecified
                && string.Equals(native.AgentStatus, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return RecordStatus.Ok;
            }
        }

        return baseStatus;

        static bool IsErrorStatus(string? value) =>
            string.Equals(value, "error", StringComparison.OrdinalIgnoreCase);

        static bool IsOkStatus(string? value) =>
            string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "success", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ExtractInvokeAgentSuffix(string spanName)
    {
        foreach (string prefix in new[] { "invoke_agent ", "invoke_agent:" })
        {
            if (spanName.StartsWith(prefix, StringComparison.Ordinal))
            {
                string candidate = spanName[prefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && !JsonDocumentText.IsDocument(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    internal static string FormatProblemSeverity(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Warning => "warning",
        _ => "error",
    };

    private static string? ResolveSessionKey(OtlpMapperHooks hooks, OtlpSpanNative native) =>
        hooks.ResolveSessionKey?.Invoke(native) ?? native.SessionId;

    private static string? ResolveModel(OtlpMapperHooks hooks, OtlpSpanNative native) =>
        hooks.ResolveModel?.Invoke(native) ?? native.Model;

    private static string? ResolveToolName(OtlpMapperHooks hooks, OtlpSpanNative native) =>
        hooks.ResolveToolName?.Invoke(native) ?? native.ToolName;

    private static string? ResolveToolCallId(OtlpMapperHooks hooks, OtlpSpanNative native) =>
        hooks.ResolveToolCallId?.Invoke(native) ?? native.ToolCallId;

    private static string? ResolveToolErrorCode(OtlpMapperHooks hooks, OtlpSpanNative native) =>
        hooks.ResolveToolErrorCode?.Invoke(native) ?? native.ToolErrorCode;

    private static string? ResolveExtraToolStatus(OtlpMapperHooks hooks, OtlpSpanNative native) =>
        hooks.ResolveExtraToolStatus?.Invoke(native);

    private static double? ResolveCostUsd(OtlpMapperHooks hooks, OtlpSpanNative native) =>
        hooks.ResolveCostUsd?.Invoke(native) ?? native.ReportedCostUsd;
}

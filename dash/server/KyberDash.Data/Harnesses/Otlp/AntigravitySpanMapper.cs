using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Pure <see cref="IHarnessMapper"/> for Antigravity OTLP trace spans, translating decoded
/// <see cref="OtlpSpan"/> payloads via <see cref="OtlpSpanNative"/> into static
/// <see cref="CollectedRecord"/> and <see cref="CollectedBatch"/> DTOs using Antigravity's
/// cache-inclusive token convention.
/// </summary>
/// <remarks>
/// Antigravity emits canonical <c>gen_ai.*</c> attributes with Gemini thought counters:
/// <c>gen_ai.agent.name = "antigravity"</c> carries vendor evidence, usage-bearing spans map to
/// <c>llm.invoke</c> with <see cref="CanonAdapters.ConvertInclusiveCounts"/> forcing
/// <c>cache_creation</c> to zero (unexported, mirroring <c>AntigravityAdapter</c>), nonzero
/// <c>gen_ai.usage.reasoning_tokens</c> wins over <c>gemini.usage.thoughts_tokens</c>,
/// <c>gen_ai.tool.name</c> spans map to <c>tool.invoke</c>, and <c>invoke_agent</c> spans map to
/// <c>agent.invoke</c>, mirroring <c>CanonAdapters.CanonicalOp</c>.
/// </remarks>
public sealed partial class AntigravitySpanMapper : IHarnessMapper
{
    private const string DefaultToolErrorMessage = "Tool execution failed.";
    private const string DefaultAgentErrorMessage = "Agent invocation failed.";
    private const string MalformedOtlpCode = "MALFORMED_OTLP_PAYLOAD";

    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvokeAgentNameRegex();

    /// <summary>Canonical harness identifier emitted for Antigravity OTLP spans.</summary>
    public const string AntigravityHarnessId = "antigravity";

    /// <inheritdoc />
    public string HarnessId => AntigravityHarnessId;

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

        IReadOnlyList<OtlpSpan> spans;
        try
        {
            spans = OtlpCollector.DecodeTracesJson(Encoding.UTF8.GetBytes(unit.RawText));
        }
        catch (Exception ex) when (ex is OtlpDecodeException or OtlpPayloadTooLargeException or JsonException)
        {
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(unit.Unit.SourceKey)))[..16];
            string message = SafeOptionalText(ex.Message) ?? "Failed to decode OTLP trace payload.";
            CollectedProblem problem = new(
                ProblemKey: $"otlp:{digest}:{MalformedOtlpCode}",
                Severity: FormatProblemSeverity(ProblemSeverity.Error),
                Code: MalformedOtlpCode,
                Message: message,
                Location: SafeOptionalText(unit.Unit.SourceKey),
                HarnessId: harnessId,
                TimestampUtc: importedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            return new CollectedBatch(source, [], [], [problem]);
        }

        return MapSpans(spans, unit.Unit.SourceKey, harnessId);
    }

    /// <summary>
    /// Parses <paramref name="span"/> into an <see cref="OtlpSpanNative"/> using the canonical
    /// OTLP attribute key lists.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Extracted <see cref="OtlpSpanNative"/>.</returns>
    public OtlpSpanNative Parse(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        return OtlpSpanNative.Parse(span);
    }

    /// <summary>
    /// Maps a single decoded Antigravity OTLP <paramref name="span"/> into a normalized
    /// <see cref="CollectedRecord"/>.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Normalized <see cref="CollectedRecord"/>.</returns>
    public CollectedRecord MapSpan(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        OtlpSpanNative native = Parse(span);
        return MapNative(native, AntigravityHarnessId);
    }

    /// <summary>
    /// Maps a batch of decoded Antigravity OTLP <paramref name="spans"/> into sessions, validated
    /// records, and token/ingest problems.
    /// </summary>
    /// <param name="spans">Decoded OTLP spans.</param>
    /// <param name="sourceLocator">Optional source locator override; defaults to the first span's <c>service.name</c> or <c>"otlp"</c>.</param>
    /// <param name="harnessIdOverride">Optional harness identifier override; defaults to <see cref="HarnessId"/>.</param>
    /// <returns>Static <see cref="CollectedBatch"/> for persistence.</returns>
    public CollectedBatch MapSpans(
        IReadOnlyList<OtlpSpan> spans,
        string? sourceLocator = null,
        string? harnessIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(spans);

        string harnessId = ResolveHarnessId(harnessIdOverride);
        string locator = SafeOptionalText(sourceLocator)
            ?? (spans.Count > 0 ? OtlpSpanNative.Parse(spans[0]).Source : "otlp");
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

            OtlpSpanNative native = Parse(span);
            if (!seenRecordIds.Add(native.SpanId))
            {
                continue;
            }

            if (TryBuildTokenUsage(native, ResolveOp(native), out TokenUsage usage))
            {
                Problem? validationProblem = CanonValidation.ValidateTokens(usage, native.SpanId);
                if (validationProblem is not null)
                {
                    string problemKey = UniqueProblemKey(usedProblemKeys, native.SpanId, validationProblem.Code, index);
                    problems.Add(new CollectedProblem(
                        ProblemKey: problemKey,
                        Severity: FormatProblemSeverity(validationProblem.Severity),
                        Code: validationProblem.Code,
                        Message: SafeOptionalText(validationProblem.Message) ?? "Span failed token validation.",
                        SpanId: native.SpanId,
                        Location: SafeOptionalText(validationProblem.Location) ?? native.SpanId,
                        SessionKey: SafeOptionalText(native.SessionId),
                        HarnessId: harnessId,
                        TimestampUtc: native.StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                        Stack: SafeOptionalText(validationProblem.Stack)));
                    continue;
                }
            }

            CollectedRecord record = MapNative(native, harnessId);
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

    private static CollectedRecord MapNative(OtlpSpanNative native, string harnessId)
    {
        RecordOp op = ResolveOp(native);
        RecordKind kind = MapRecordKind(native.Kind);
        RecordStatus status = ResolveStatus(native, op);
        string opWire = FormatRecordOp(op);
        string name = !string.IsNullOrWhiteSpace(native.Name) && !JsonDocumentText.IsDocument(native.Name)
            ? native.Name
            : opWire;

        List<CollectedTokenCount> tokens = [];
        long? reportedInputTokens = null;
        long? reportedOutputTokens = null;
        if (TryBuildTokenUsage(native, op, out TokenUsage usage))
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

        CollectedCost? cost = native.ReportedCostUsd is double costValue
            && double.IsFinite(costValue)
            && costValue >= 0
            ? new CollectedCost(costValue, "USD")
            : null;

        List<CollectedContentPart> parts = BuildCollectedParts(native);

        CollectedToolCall? tool = null;
        if (op == RecordOp.ToolInvoke)
        {
            tool = BuildToolCall(native, name, status, parts);
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
            SessionKey: SafeOptionalText(native.SessionId),
            ParentRecordId: SafeOptionalText(native.ParentSpanId),
            TraceId: SafeOptionalText(native.TraceId),
            Model: SafeOptionalText(native.Model),
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

    private static RecordOp ResolveOp(OtlpSpanNative native)
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

    private static RecordStatus ResolveStatus(OtlpSpanNative native, RecordOp op)
    {
        RecordStatus baseStatus = MapRecordStatus(native.StatusCode);
        if (baseStatus == RecordStatus.Error)
        {
            return RecordStatus.Error;
        }

        if (op == RecordOp.ToolInvoke)
        {
            if (native.ToolIsError == true
                || string.Equals(native.ToolStatus, "error", StringComparison.OrdinalIgnoreCase)
                || (native.ToolExitCode is not null and not 0))
            {
                return RecordStatus.Error;
            }

            if (baseStatus is RecordStatus.Unset or RecordStatus.Unspecified
                && (native.ToolIsError == false
                    || string.Equals(native.ToolStatus, "ok", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(native.ToolStatus, "success", StringComparison.OrdinalIgnoreCase)))
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
    }

    private static bool TryBuildTokenUsage(OtlpSpanNative native, RecordOp op, out TokenUsage usage)
    {
        bool hasAnyToken = native.InputTokens.HasValue
            || native.OutputTokens.HasValue
            || native.CacheReadTokens.HasValue
            || native.ReasoningTokens.HasValue
            || native.ThoughtTokens.HasValue;

        if (!hasAnyToken && op != RecordOp.LlmInvoke)
        {
            usage = default!;
            return false;
        }

        long input = native.InputTokens ?? 0;
        long cacheRead = native.CacheReadTokens ?? 0;
        long output = native.OutputTokens ?? 0;
        bool inputPresent = OtlpSpanNative.HasAnyKey(native.Attributes, OtlpSpanNative.InputTokenKeys);

        long reasoningValue = native.ReasoningTokens ?? 0;
        if (reasoningValue == 0)
        {
            reasoningValue = native.ThoughtTokens ?? 0;
        }

        long? thoughts = reasoningValue != 0 ? reasoningValue : null;

        usage = CanonAdapters.ConvertInclusiveCounts(input, cacheRead, 0, output, inputPresent, thoughts);
        return true;
    }

    private static List<CollectedContentPart> BuildCollectedParts(OtlpSpanNative native)
    {
        List<CollectedContentPart> parts = [];
        HashSet<int> usedPositions = [];
        int nextPosition = 0;

        foreach (ContentPart part in native.Parts)
        {
            if (!TryMapContentBucket(part.Part, out ContentBucket bucket))
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

    private static CollectedToolCall BuildToolCall(
        OtlpSpanNative native,
        string fallbackName,
        RecordStatus status,
        List<CollectedContentPart> parts)
    {
        string callId = SafeOptionalText(native.ToolCallId) ?? native.SpanId;
        string toolName = SafeOptionalText(native.ToolName) ?? fallbackName;

        string? mcpServerName = SafeOptionalText(native.ToolMcpServerName);
        string? mcpToolName = SafeOptionalText(native.ToolMcpToolName);
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

        CollectedError? error = null;
        if (succeeded == false)
        {
            string message = SafeOptionalText(native.ErrorMessage)
                ?? SafeOptionalText(native.StatusMessage)
                ?? DefaultToolErrorMessage;
            error = new CollectedError(message, SafeOptionalText(native.ToolErrorCode));
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

    private static CollectedAgentInvocation BuildAgentInvocation(
        OtlpSpanNative native,
        string fallbackName,
        RecordStatus status)
    {
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

    private static string? ExtractInvokeAgentSuffix(string spanName)
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

    private static string ResolveHarnessId(string? unitHarnessId)
    {
        if (string.IsNullOrWhiteSpace(unitHarnessId))
        {
            return AntigravityHarnessId;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(unitHarnessId);
        return string.Equals(normalized, "antigravity", StringComparison.Ordinal)
            ? AntigravityHarnessId
            : normalized;
    }

    private static string UniqueProblemKey(HashSet<string> usedKeys, string spanId, string code, int index)
    {
        string baseKey = string.Create(CultureInfo.InvariantCulture, $"otlp:{spanId}:{code}");
        if (usedKeys.Add(baseKey))
        {
            return baseKey;
        }

        string indexedKey = string.Create(CultureInfo.InvariantCulture, $"{baseKey}:{index}");
        _ = usedKeys.Add(indexedKey);
        return indexedKey;
    }

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    private static string FormatProblemSeverity(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Warning => "warning",
        _ => "error",
    };

    private static string FormatRecordOp(RecordOp op) => op switch
    {
        RecordOp.LlmInvoke => "llm.invoke",
        RecordOp.ToolInvoke => "tool.invoke",
        RecordOp.AgentInvoke => "agent.invoke",
        _ => "unspecified",
    };

    private static RecordKind MapRecordKind(string kind) => kind switch
    {
        "internal" => RecordKind.Internal,
        "server" => RecordKind.Server,
        "client" => RecordKind.Client,
        "producer" => RecordKind.Producer,
        "consumer" => RecordKind.Consumer,
        _ => RecordKind.Unspecified,
    };

    private static RecordStatus MapRecordStatus(string status) => status switch
    {
        "ok" => RecordStatus.Ok,
        "error" => RecordStatus.Error,
        "unset" => RecordStatus.Unset,
        _ => RecordStatus.Unspecified,
    };

    private static bool TryMapContentBucket(string bucket, out ContentBucket result)
    {
        switch (bucket)
        {
            case "system_prompt":
                result = ContentBucket.SystemPrompt;
                return true;
            case "tool_definitions":
                result = ContentBucket.ToolDefinitions;
                return true;
            case "tool_definition":
                result = ContentBucket.ToolDefinition;
                return true;
            case "instruction_context":
                result = ContentBucket.InstructionContext;
                return true;
            case "conversation_history":
                result = ContentBucket.ConversationHistory;
                return true;
            case "tool_result_content":
                result = ContentBucket.ToolResultContent;
                return true;
            case "user_prompt":
                result = ContentBucket.UserPrompt;
                return true;
            case "user_message":
                result = ContentBucket.UserMessage;
                return true;
            case "reasoning":
                result = ContentBucket.Reasoning;
                return true;
            default:
                result = default;
                return false;
        }
    }
}

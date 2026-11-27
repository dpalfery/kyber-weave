using System.Globalization;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Pure mapper for Codex <c>codex.tool_result</c> OTLP log events into static
/// <see cref="CollectedRecord"/> <c>tool.invoke</c> DTOs.
/// </summary>
/// <remarks>
/// <para>
/// Attribute names follow the authoritative <c>openai/codex</c> telemetry source
/// (<c>codex-rs/otel/src/tool_result.rs</c>, documented in <c>codex-rs/otel/README.md</c>):
/// the event carries <c>tool_name</c>, <c>call_id</c>, <c>success</c>, and
/// <c>duration_ms</c> alongside log-only <c>arguments</c> and an <c>output</c> preview.
/// Only the four documented invocation attributes reach the DTOs; the arguments and
/// output excerpt are dropped as JSON-document or tool-specific text (DD-3, OD-6), and
/// every other event attribute is dropped because it is not part of the canonical
/// <c>tool.invoke</c> shape. The dropped fields are registered in
/// <c>docs/dash/field-register.md</c>.
/// </para>
/// <para>
/// An attribute the <c>openai/codex</c> telemetry source does not name is never mapped.
/// </para>
/// </remarks>
public static class CodexToolResultMapper
{
    /// <summary>OTLP log event name emitted by Codex when a tool invocation completes.</summary>
    public const string ToolResultEventName = "codex.tool_result";

    /// <summary>Problem code recorded when a <c>codex.tool_result</c> event lacks its documented identity attributes.</summary>
    public const string InvalidToolResultCode = "CODEX_TOOL_RESULT_INVALID";

    /// <summary>
    /// <c>codex.tool_result</c> attribute keys mapped onto the <c>tool.invoke</c> record,
    /// as named by <c>codex-rs/otel/src/tool_result.rs</c> in <c>openai/codex</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> MappedAttributeKeys =
    [
        "tool_name",
        "call_id",
        "success",
        "duration_ms",
    ];

    /// <summary>
    /// <c>codex.tool_result</c> payload attributes dropped without reaching the DTOs: the tool
    /// arguments (a JSON document) and the output excerpt (tool-specific text), per DD-3 and OD-6.
    /// </summary>
    public static readonly IReadOnlyList<string> DroppedPayloadAttributeKeys = ["arguments", "output"];

    private const string EventNameKey = "event.name";
    private const string ToolNameKey = "tool_name";
    private const string CallIdKey = "call_id";
    private const string SuccessKey = "success";
    private const string DurationMsKey = "duration_ms";
    private const string ConversationIdKey = "conversation.id";
    private const string IsoTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string UnnamedSource = "otlp";

    private static readonly string[] ModelKeys = ["gen_ai.response.model", "gen_ai.request.model", "model"];

    /// <summary>
    /// Returns whether <paramref name="log"/> is a Codex <c>codex.tool_result</c> log event.
    /// </summary>
    /// <param name="log">Decoded OTLP log record to inspect.</param>
    /// <returns><see langword="true"/> when the log carries the <c>codex.tool_result</c> event name.</returns>
    public static bool IsToolResult(OtlpLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Attributes.TryGetValue(EventNameKey, out JsonElement eventName)
            && eventName.ValueKind == JsonValueKind.String
            && string.Equals(eventName.GetString(), ToolResultEventName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Maps a single Codex <c>codex.tool_result</c> <paramref name="log"/> into a normalized
    /// <c>tool.invoke</c> <see cref="CollectedRecord"/>.
    /// </summary>
    /// <param name="log">Decoded OTLP log record to map.</param>
    /// <param name="importedAtUtc">Import instant used for diagnostic problem timestamps.</param>
    /// <param name="harnessIdOverride">Optional harness identifier override; defaults to <c>codex-cli</c>.</param>
    /// <param name="record">Mapped record when the log is a well-formed tool-result event; otherwise <see langword="null"/>.</param>
    /// <param name="problem">Payload-free diagnostic when the log is a malformed tool-result event; otherwise <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="log"/> is a <c>codex.tool_result</c> event
    /// (exactly one of <paramref name="record"/> or <paramref name="problem"/> is non-null);
    /// <see langword="false"/> when the log is a different event (both are <see langword="null"/>).
    /// </returns>
    public static bool TryMap(
        OtlpLog log,
        DateTimeOffset importedAtUtc,
        string? harnessIdOverride,
        out CollectedRecord? record,
        out CollectedProblem? problem)
    {
        ArgumentNullException.ThrowIfNull(log);

        record = null;
        problem = null;

        if (!IsToolResult(log))
        {
            return false;
        }

        string harnessId = ResolveHarnessId(harnessIdOverride);

        if (!DateTimeOffset.TryParse(
            log.Timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsedTimestamp))
        {
            problem = new CollectedProblem(
                ProblemKey: string.Create(CultureInfo.InvariantCulture, $"otlp-log:{log.LogId}:{OtlpLogMapper.InvalidTimestampCode}"),
                Severity: ProblemSeverityWireString(ProblemSeverity.Error),
                Code: OtlpLogMapper.InvalidTimestampCode,
                Message: "OTLP log record lacked a valid UTC timestamp; raw payload was not retained.",
                SpanId: SafeOptionalText(log.LogId),
                Location: SafeOptionalText(log.LogId),
                SessionKey: ResolveSessionKey(log),
                HarnessId: harnessId,
                TimestampUtc: importedAtUtc.UtcDateTime.ToString(IsoTimestampFormat, CultureInfo.InvariantCulture),
                Stack: null);
            return true;
        }

        string? toolName = ReadNonEmptyString(log.Attributes, ToolNameKey);
        if (toolName is null)
        {
            problem = InvalidToolResultProblem(log, harnessId, importedAtUtc, ToolNameKey);
            return true;
        }

        string? callId = ReadNonEmptyString(log.Attributes, CallIdKey);
        if (callId is null)
        {
            problem = InvalidToolResultProblem(log, harnessId, importedAtUtc, CallIdKey);
            return true;
        }

        bool? succeeded = ReadOptionalSuccess(log.Attributes);
        long? durationMs = ReadOptionalDurationMs(log.Attributes);

        DateTimeOffset startedAt = parsedTimestamp.ToUniversalTime();
        DateTimeOffset? endedAt = durationMs is long duration ? startedAt.AddMilliseconds(duration) : null;
        RecordStatus status = succeeded is null ? RecordStatus.Unset : succeeded.Value ? RecordStatus.Ok : RecordStatus.Error;

        CollectedToolCall tool = new(
            CallId: callId,
            ToolName: toolName,
            McpServerName: null,
            McpToolName: null,
            Input: null,
            Result: null,
            Succeeded: succeeded,
            Error: null,
            ExitCode: null);

        record = new CollectedRecord(
            RecordId: log.LogId,
            HarnessId: harnessId,
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: toolName,
            StartedAt: startedAt,
            Status: status,
            SessionKey: ResolveSessionKey(log),
            ParentRecordId: SafeOptionalText(log.SpanId),
            TraceId: SafeOptionalText(log.TraceId),
            Model: ReadNonEmptyString(log.Attributes, ModelKeys),
            EndedAt: endedAt,
            ReportedDurationMs: durationMs,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: tool,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null));
        return true;
    }

    /// <summary>
    /// Maps <c>codex.tool_result</c> <paramref name="logs"/> into a static
    /// <see cref="CollectedBatch"/>; logs carrying other events are skipped because they belong
    /// to other mappers, and malformed tool-result events become payload-free problems.
    /// </summary>
    /// <param name="logs">Decoded OTLP log records to map.</param>
    /// <param name="sourceLocator">Optional source locator override; defaults to the first log's <c>service.name</c> or <c>"otlp"</c>.</param>
    /// <param name="harnessIdOverride">Optional harness identifier override; defaults to <c>codex-cli</c>.</param>
    /// <param name="importedAtUtc">Optional import instant for diagnostic problem timestamps; defaults to now.</param>
    /// <param name="timeProvider">Clock supplying the default import instant; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <returns>Static <see cref="CollectedBatch"/> for persistence.</returns>
    public static CollectedBatch MapLogs(
        IReadOnlyList<OtlpLog> logs,
        string? sourceLocator = null,
        string? harnessIdOverride = null,
        DateTimeOffset? importedAtUtc = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logs);

        string harnessId = ResolveHarnessId(harnessIdOverride);
        string locator = SafeOptionalText(sourceLocator)
            ?? (logs.Count > 0 ? ExtractSource(logs[0].Resource) : UnnamedSource);
        CollectedSource source = new(harnessId, SourceKind.Otlp, locator);
        DateTimeOffset importedAt = importedAtUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow();

        Dictionary<string, CollectedSession> sessions = new(StringComparer.Ordinal);
        List<CollectedRecord> records = [];
        List<CollectedProblem> problems = [];
        HashSet<string> usedProblemKeys = new(StringComparer.Ordinal);

        foreach (OtlpLog log in logs)
        {
            ArgumentNullException.ThrowIfNull(log);

            if (!TryMap(log, importedAt, harnessId, out CollectedRecord? record, out CollectedProblem? problem))
            {
                continue;
            }

            if (record is not null)
            {
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

                continue;
            }

            if (problem is not null)
            {
                problems.Add(EnsureUniqueProblemKey(problem, usedProblemKeys));
            }
        }

        return new CollectedBatch(source, [.. sessions.Values], records, problems);
    }

    private static CollectedProblem InvalidToolResultProblem(
        OtlpLog log,
        string harnessId,
        DateTimeOffset importedAtUtc,
        string missingKey)
    {
        return new CollectedProblem(
            ProblemKey: string.Create(CultureInfo.InvariantCulture, $"otlp-log:{log.LogId}:{InvalidToolResultCode}"),
            Severity: ProblemSeverityWireString(ProblemSeverity.Error),
            Code: InvalidToolResultCode,
            Message: string.Create(
                CultureInfo.InvariantCulture,
                $"Codex codex.tool_result event lacked a valid plain-text '{missingKey}' attribute; raw payload was not retained."),
            SpanId: SafeOptionalText(log.LogId),
            Location: SafeOptionalText(log.LogId),
            SessionKey: ResolveSessionKey(log),
            HarnessId: harnessId,
            TimestampUtc: importedAtUtc.UtcDateTime.ToString(IsoTimestampFormat, CultureInfo.InvariantCulture),
            Stack: null);
    }

    private static CollectedProblem EnsureUniqueProblemKey(CollectedProblem problem, HashSet<string> usedKeys)
    {
        if (usedKeys.Add(problem.ProblemKey))
        {
            return problem;
        }

        int suffix = 2;
        string candidate;
        do
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{problem.ProblemKey}:{suffix}");
            suffix++;
        }
        while (!usedKeys.Add(candidate));

        return problem with { ProblemKey = candidate };
    }

    private static string? ResolveSessionKey(OtlpLog log)
    {
        return SafeOptionalText(log.SessionId)
            ?? SafeOptionalText(CanonAdapters.CanonicalSessionId(log.Attributes))
            ?? ReadNonEmptyString(log.Attributes, ConversationIdKey);
    }

    private static string ResolveHarnessId(string? harnessIdOverride) =>
        HarnessJson.ResolveCodexHarnessId(harnessIdOverride);

    private static string ExtractSource(IReadOnlyDictionary<string, JsonElement> resource)
    {
        if (resource.TryGetValue("service.name", out JsonElement serviceElement)
            && serviceElement.ValueKind == JsonValueKind.String)
        {
            string? value = SafeOptionalText(serviceElement.GetString());
            if (value is not null)
            {
                return value;
            }
        }

        return UnnamedSource;
    }

    private static bool? ReadOptionalSuccess(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        if (!attributes.TryGetValue(SuccessKey, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out long integer) => integer != 0,
            JsonValueKind.Number when element.TryGetDouble(out double number) && double.IsFinite(number) => number != 0,
            JsonValueKind.Number => null,
            JsonValueKind.String => bool.TryParse(element.GetString(), out bool parsed) ? parsed : null,
            _ => null,
        };
    }

    private static long? ReadOptionalDurationMs(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        if (!attributes.TryGetValue(DurationMsKey, out JsonElement element))
        {
            return null;
        }

        long? parsed = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out double floating)
                && double.IsFinite(floating)
                && floating >= long.MinValue
                && floating <= long.MaxValue => Convert.ToInt64(floating, CultureInfo.InvariantCulture),
            JsonValueKind.Number => null,
            JsonValueKind.String when long.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long textInteger) => textInteger,
            JsonValueKind.String when double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double textDouble)
                && double.IsFinite(textDouble)
                && textDouble >= long.MinValue
                && textDouble <= long.MaxValue => Convert.ToInt64(textDouble, CultureInfo.InvariantCulture),
            JsonValueKind.String => null,
            _ => null,
        };

        return parsed is long value && value >= 0 ? value : null;
    }

    private static string? ReadNonEmptyString(IReadOnlyDictionary<string, JsonElement> attributes, string key)
    {
        if (attributes.TryGetValue(key, out JsonElement element)
            && element.ValueKind == JsonValueKind.String)
        {
            return SafeOptionalText(element.GetString());
        }

        return null;
    }

    private static string? ReadNonEmptyString(IReadOnlyDictionary<string, JsonElement> attributes, string[] keys) =>
        OtlpSpanNative.FirstSafeString(attributes, keys);

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    private static string ProblemSeverityWireString(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Warning => "warning",
        _ => "error",
    };
}

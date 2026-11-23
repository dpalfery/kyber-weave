using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Result of mapping decoded OTLP log records into normalized relational collector batches
/// and standalone <c>ingest_problem</c> entries.
/// </summary>
/// <param name="Batches">Normalized collector batches grouped by harness and source.</param>
/// <param name="StandaloneProblems">Diagnostic problems for unmappable logs not attached to a batch.</param>
/// <param name="Outcome">Accepted and rejected log counters.</param>
public record OtlpLogMapResult(
    IReadOnlyList<CollectedBatch> Batches,
    IReadOnlyList<CollectedProblem> StandaloneProblems,
    IngestOutcome Outcome);

/// <summary>
/// Maps decoded <see cref="OtlpLog"/> records directly on receipt into relational collector DTOs
/// (<see cref="CollectedBatch"/>) or payload-free <see cref="CollectedProblem"/> entries
/// (<c>ingest_problem</c>), eliminating intermediate <c>pending_logs</c>, <c>quarantined_logs</c>,
/// and <c>enriched_logs</c> staging tables.
/// </summary>
public static class OtlpLogMapper
{
    private const string UnnamedSource = "otlp";
    private const string IsoTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Problem code recorded when an OTLP log carries no valid plain-text log identifier.</summary>
    public const string InvalidIdCode = "OTLP_LOG_INVALID_ID";

    /// <summary>Problem code recorded when an OTLP log carries an unparseable timestamp.</summary>
    public const string InvalidTimestampCode = "OTLP_LOG_INVALID_TIMESTAMP";

    /// <summary>Problem code recorded when an OTLP log cannot be attributed to any supported harness.</summary>
    public const string UnattributedCode = "OTLP_LOG_UNATTRIBUTED";

    /// <summary>Problem code recorded when an attributed log carries an unsupported OpenCode contract version.</summary>
    public const string UnknownOpencodeContractCode = "UNKNOWN_OPENCODE_CONTRACT_VERSION";

    /// <summary>Problem code recorded when an attributed log carries an unsupported Droid contract version.</summary>
    public const string UnknownDroidContractCode = "UNKNOWN_DROID_CONTRACT_VERSION";

    /// <summary>Problem code recorded when an adapter throws while normalizing an attributed OTLP log.</summary>
    public const string NormalizeFailedCode = "ADAPTER_NORMALIZE_FAILED";

    private static readonly string[] ModelKeys = ["gen_ai.response.model", "gen_ai.request.model", "model"];
    private static readonly string[] DurationKeys = ["duration_ms", "durationMs"];
    private static readonly string[] CostUsdKeys = ["gen_ai.usage.cost", "cost_usd", "costUsd"];
    private static readonly string[] ErrorMessageKeys = ["error.message", "error"];
    private static readonly string[] EventNameKeys = ["event.name", "event_name"];

    /// <summary>
    /// Maps <paramref name="logs"/> into relational <see cref="CollectedBatch"/> outputs and
    /// payload-free <see cref="CollectedProblem"/> entries.
    /// </summary>
    /// <param name="logs">Decoded OTLP log records to map.</param>
    /// <param name="adapters">Optional harness adapters in tie-break priority order.</param>
    /// <returns>Mapped relational batches, standalone problems, and outcome counters.</returns>
    public static OtlpLogMapResult Map(
        IReadOnlyList<OtlpLog> logs,
        IReadOnlyList<IHarnessAdapter>? adapters = null)
    {
        ArgumentNullException.ThrowIfNull(logs);
        if (logs.Count == 0)
        {
            return new OtlpLogMapResult([], [], new IngestOutcome(0, 0, 0));
        }

        IReadOnlyList<IHarnessAdapter> activeAdapters = adapters ?? CanonAdapters.DefaultAdapters;
        AdapterRegistry registry = new(activeAdapters);
        Dictionary<string, IHarnessAdapter> adapterByName = new(StringComparer.Ordinal);
        foreach (IHarnessAdapter adapter in activeAdapters)
        {
            adapterByName[adapter.Name] = adapter;
        }

        List<PreparedLog> prepared = new(logs.Count);
        List<RawSpan> votable = [];
        HashSet<string> usedProblemKeys = new(StringComparer.Ordinal);
        List<(string? HarnessId, string Source, CollectedProblem Problem)> problemAssignments = [];
        int rejected = 0;

        for (int index = 0; index < logs.Count; index++)
        {
            OtlpLog log = logs[index];
            ArgumentNullException.ThrowIfNull(log);

            string source = ExtractSource(log.Resource);
            string? sessionKey = SafeOptionalText(log.SessionId ?? CanonAdapters.CanonicalSessionId(log.Attributes));

            if (string.IsNullOrWhiteSpace(log.LogId) || JsonDocumentText.IsDocument(log.LogId))
            {
                rejected++;
                string fallbackKey = string.Create(CultureInfo.InvariantCulture, $"missing-id-{index}");
                problemAssignments.Add((
                    null,
                    source,
                    new CollectedProblem(
                        ProblemKey: CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp-log:", fallbackKey, InvalidIdCode, index),
                        Severity: ProblemSeverityWireString(ProblemSeverity.Error),
                        Code: InvalidIdCode,
                        Message: "OTLP log record lacked a valid plain-text log identifier; raw payload was not retained.",
                        SpanId: SafeOptionalText(log.SpanId),
                        Location: SafeOptionalText(log.SpanId),
                        SessionKey: sessionKey,
                        HarnessId: null,
                        TimestampUtc: null,
                        Stack: null)));
                continue;
            }

            if (!DateTimeOffset.TryParse(
                log.Timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsedTimestamp))
            {
                rejected++;
                problemAssignments.Add((
                    null,
                    source,
                    new CollectedProblem(
                        ProblemKey: CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp-log:", log.LogId, InvalidTimestampCode, index),
                        Severity: ProblemSeverityWireString(ProblemSeverity.Error),
                        Code: InvalidTimestampCode,
                        Message: "OTLP log record lacked a valid UTC timestamp; raw payload was not retained.",
                        SpanId: SafeOptionalText(log.SpanId),
                        Location: log.LogId,
                        SessionKey: sessionKey,
                        HarnessId: null,
                        TimestampUtc: null,
                        Stack: null)));
                continue;
            }

            DateTimeOffset startedAtUtc = parsedTimestamp.ToUniversalTime();
            string timestampUtc = startedAtUtc.UtcDateTime.ToString(IsoTimestampFormat, CultureInfo.InvariantCulture);
            string? eventName = ExtractSafeAttributeString(log.Attributes, EventNameKeys);
            string spanName = eventName ?? SafeOptionalText(log.Scope.Name) ?? "otlp.log";

            RawSpan rawSpan = new(
                SpanId: log.LogId,
                TraceId: SafeOptionalText(log.TraceId),
                ParentSpanId: SafeOptionalText(log.SpanId),
                Source: source,
                Attributes: log.Attributes,
                Name: spanName,
                Kind: "internal",
                Timestamp: timestampUtc);

            if (CanonAdapters.IsOpencodeEnvelopeSpan(rawSpan) && !CanonAdapters.IsSupportedOpencodeEnvelope(rawSpan))
            {
                rejected++;
                problemAssignments.Add((
                    null,
                    source,
                    new CollectedProblem(
                        ProblemKey: CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp-log:", log.LogId, UnknownOpencodeContractCode, index),
                        Severity: ProblemSeverityWireString(ProblemSeverity.Error),
                        Code: UnknownOpencodeContractCode,
                        Message: "OTLP log carried an unsupported OpenCode contract version; raw payload was not retained.",
                        SpanId: SafeOptionalText(log.SpanId),
                        Location: log.LogId,
                        SessionKey: sessionKey,
                        HarnessId: null,
                        TimestampUtc: timestampUtc,
                        Stack: null)));
                continue;
            }

            if (CanonAdapters.IsDroidEnvelopeSpan(rawSpan) && !CanonAdapters.IsSupportedDroidEnvelope(rawSpan))
            {
                rejected++;
                problemAssignments.Add((
                    null,
                    source,
                    new CollectedProblem(
                        ProblemKey: CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp-log:", log.LogId, UnknownDroidContractCode, index),
                        Severity: ProblemSeverityWireString(ProblemSeverity.Error),
                        Code: UnknownDroidContractCode,
                        Message: "OTLP log carried an unsupported Droid contract version; raw payload was not retained.",
                        SpanId: SafeOptionalText(log.SpanId),
                        Location: log.LogId,
                        SessionKey: sessionKey,
                        HarnessId: null,
                        TimestampUtc: timestampUtc,
                        Stack: null)));
                continue;
            }

            votable.Add(rawSpan);
            prepared.Add(new PreparedLog(index, log, rawSpan, source, sessionKey, startedAtUtc, timestampUtc, eventName));
        }

        IReadOnlyDictionary<string, string> attributed = votable.Count > 0
            ? registry.Attribute(votable)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        List<MappedRecordEntry> accepted = [];

        foreach (PreparedLog item in prepared)
        {
            string? harness = ResolveHarness(item, attributed, adapterByName);
            if (harness is null || !adapterByName.TryGetValue(harness, out IHarnessAdapter? adapter))
            {
                rejected++;
                IReadOnlyList<string> namespaces = ObservedNamespaces(item.Log.Attributes);
                string message = namespaces.Count > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"OTLP log could not be attributed to a supported harness (namespaces: {string.Join(", ", namespaces)}); raw payload was not retained.")
                    : "OTLP log could not be attributed to a supported harness; raw payload was not retained.";

                problemAssignments.Add((
                    null,
                    item.Source,
                    new CollectedProblem(
                        ProblemKey: CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp-log:", item.Log.LogId, UnattributedCode, item.Index),
                        Severity: ProblemSeverityWireString(ProblemSeverity.Error),
                        Code: UnattributedCode,
                        Message: message,
                        SpanId: SafeOptionalText(item.Log.SpanId),
                        Location: item.Log.LogId,
                        SessionKey: item.SessionKey,
                        HarnessId: null,
                        TimestampUtc: item.TimestampUtc,
                        Stack: null)));
                continue;
            }

            CanonicalRecord normalized;
            try
            {
                normalized = adapter.Normalize(item.RawSpan);
            }
            catch (Exception exception)
            {
                rejected++;
                problemAssignments.Add((
                    harness,
                    item.Source,
                    new CollectedProblem(
                        ProblemKey: CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp-log:", item.Log.LogId, NormalizeFailedCode, item.Index),
                        Severity: ProblemSeverityWireString(ProblemSeverity.Error),
                        Code: NormalizeFailedCode,
                        Message: SafeProblemMessage(exception.Message, "OTLP log normalization failed; raw payload was not retained."),
                        SpanId: SafeOptionalText(item.Log.SpanId),
                        Location: item.Log.LogId,
                        SessionKey: item.SessionKey,
                        HarnessId: harness,
                        TimestampUtc: item.TimestampUtc,
                        Stack: SafeOptionalText(exception.StackTrace))));
                continue;
            }

            Problem? validationProblem = adapter.Validate(normalized);
            if (validationProblem is not null)
            {
                rejected++;
                problemAssignments.Add((
                    normalized.Harness,
                    item.Source,
                    new CollectedProblem(
                        ProblemKey: CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp-log:", item.Log.LogId, validationProblem.Code, item.Index),
                        Severity: ProblemSeverityWireString(validationProblem.Severity),
                        Code: validationProblem.Code,
                        Message: SafeProblemMessage(validationProblem.Message, "OTLP log failed validation; raw payload was not retained."),
                        SpanId: SafeOptionalText(item.Log.SpanId),
                        Location: SafeOptionalText(validationProblem.Location) ?? item.Log.LogId,
                        SessionKey: item.SessionKey,
                        HarnessId: normalized.Harness,
                        TimestampUtc: item.TimestampUtc,
                        Stack: SafeOptionalText(validationProblem.Stack))));
                continue;
            }

            CollectedRecord collected = ToCollectedRecord(item, normalized);
            accepted.Add(new MappedRecordEntry(item.Source, collected));
        }

        (IReadOnlyList<CollectedBatch> batches, IReadOnlyList<CollectedProblem> standaloneProblems) =
            BuildBatches(accepted, problemAssignments);

        return new OtlpLogMapResult(
            Batches: batches,
            StandaloneProblems: standaloneProblems,
            Outcome: new IngestOutcome(accepted.Count, 0, rejected));
    }

    private static string? ResolveHarness(
        PreparedLog item,
        IReadOnlyDictionary<string, string> attributed,
        IReadOnlyDictionary<string, IHarnessAdapter> adapterByName)
    {
        if (attributed.TryGetValue(item.RawSpan.SpanId, out string? harness))
        {
            if (string.Equals(harness, "droid", StringComparison.Ordinal)
                && !CanonAdapters.IsDroidEnvelopeSpan(item.RawSpan))
            {
                return null;
            }

            return harness;
        }

        string? byEvent = MatchHarnessPrefix(item.EventName);
        if (byEvent is not null && adapterByName.ContainsKey(byEvent))
        {
            return byEvent;
        }

        string? byScope = MatchHarnessPrefix(item.Log.Scope.Name);
        if (byScope is not null && adapterByName.ContainsKey(byScope))
        {
            return byScope;
        }

        return null;
    }

    private static string? MatchHarnessPrefix(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (candidate.StartsWith("claude_code", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("claude.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, "claude", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, "com.anthropic.claude_code", StringComparison.OrdinalIgnoreCase))
        {
            return "claude-code";
        }

        if (candidate.StartsWith("codex", StringComparison.OrdinalIgnoreCase))
        {
            return "codex";
        }

        if (candidate.StartsWith("copilot", StringComparison.OrdinalIgnoreCase))
        {
            return "copilot";
        }

        if (candidate.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "gemini";
        }

        if (candidate.StartsWith("antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return "antigravity";
        }

        if (candidate.StartsWith("pi.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, "pi", StringComparison.OrdinalIgnoreCase))
        {
            return "pi";
        }

        return null;
    }

    private static CollectedRecord ToCollectedRecord(PreparedLog item, CanonicalRecord normalized)
    {
        OtlpLog log = item.Log;
        RecordOp op = ResolveLogOp(normalized.Op, item.EventName, log.Attributes, normalized.Tokens);
        string? model = ExtractSafeAttributeString(log.Attributes, ModelKeys);
        string? errorMessage = ExtractSafeAttributeString(log.Attributes, ErrorMessageKeys);
        RecordStatus status = errorMessage is not null
            ? RecordStatus.Error
            : CanonRecordVocabulary.ParseRecordStatus(normalized.Status);

        string name = !string.IsNullOrWhiteSpace(normalized.Name) && !JsonDocumentText.IsDocument(normalized.Name)
            ? normalized.Name
            : CanonRecordVocabulary.FormatRecordOp(op);

        string? spanId = SafeOptionalText(log.SpanId);
        string? parentRecordId = spanId is not null && !string.Equals(spanId, log.LogId, StringComparison.Ordinal)
            ? spanId
            : null;

        List<CollectedTokenCount> tokens =
        [
            new CollectedTokenCount(TokenClass.Input, Math.Max(0L, normalized.Tokens.FreshInput)),
            new CollectedTokenCount(TokenClass.CacheRead, Math.Max(0L, normalized.Tokens.CacheRead)),
            new CollectedTokenCount(TokenClass.CacheWrite, Math.Max(0L, normalized.Tokens.CacheCreation)),
            new CollectedTokenCount(TokenClass.Output, Math.Max(0L, normalized.Tokens.Output)),
        ];

        if (normalized.Tokens.Reasoning is long reasoning && reasoning >= 0)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Reasoning, reasoning));
        }

        return new CollectedRecord(
            RecordId: log.LogId,
            HarnessId: normalized.Harness,
            Op: op,
            Kind: RecordKind.Internal,
            Name: name,
            StartedAt: item.StartedAtUtc,
            Status: status,
            SessionKey: item.SessionKey ?? SafeOptionalText(normalized.SessionId),
            ParentRecordId: parentRecordId,
            TraceId: SafeOptionalText(log.TraceId),
            Model: model,
            EndedAt: null,
            ReportedDurationMs: ExtractOptionalNonNegLong(log.Attributes, DurationKeys),
            Tokens: tokens,
            ReportedCost: ExtractReportedCost(normalized.Cost, log.Attributes),
            Parts: BuildCollectedParts(normalized, log.Body, item.EventName),
            Tool: null,
            Agent: null,
            SkillsInvoked: SkillExtraction.ExtractInvoked(log.Attributes),
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(errorMessage, null, null, null),
            ReportedInputTokens: normalized.Tokens.ReportedInput >= 0 ? normalized.Tokens.ReportedInput : null,
            ReportedOutputTokens: normalized.Tokens.ReportedOutput >= 0 ? normalized.Tokens.ReportedOutput : null);
    }

    private static RecordOp ResolveLogOp(
        string normalizedOp,
        string? eventName,
        IReadOnlyDictionary<string, JsonElement> attributes,
        TokenUsage tokens)
    {
        RecordOp mapped = CanonRecordVocabulary.ParseRecordOp(normalizedOp);
        if (mapped != RecordOp.Unspecified)
        {
            return mapped;
        }

        if (attributes.ContainsKey("tool_name")
            || (eventName is not null && eventName.Contains("tool", StringComparison.OrdinalIgnoreCase)))
        {
            return RecordOp.ToolInvoke;
        }

        if ((eventName is not null
                && (eventName.Contains("api_request", StringComparison.OrdinalIgnoreCase)
                    || eventName.Contains("api_response", StringComparison.OrdinalIgnoreCase)
                    || eventName.Contains("sse_event", StringComparison.OrdinalIgnoreCase)
                    || eventName.Contains("llm", StringComparison.OrdinalIgnoreCase)))
            || tokens.ReportedInput > 0
            || tokens.ReportedOutput > 0
            || ExtractSafeAttributeString(attributes, ModelKeys) is not null)
        {
            return RecordOp.LlmInvoke;
        }

        return RecordOp.Unspecified;
    }

    private static List<CollectedContentPart> BuildCollectedParts(
        CanonicalRecord normalized,
        string? logBody,
        string? eventName)
    {
        List<CollectedContentPart> parts = [];
        HashSet<int> usedPositions = [];
        int nextPosition = 0;

        if (normalized.Parts is { Count: > 0 })
        {
            foreach (ContentPart part in normalized.Parts)
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
                parts.Add(MeasureToCollectedPart(
                    position,
                    bucket,
                    part.Text ?? string.Empty,
                    part.Tokens is >= 0 ? part.Tokens : null,
                    SafeOptionalText(part.Server),
                    SafeOptionalText(part.Role),
                    part.Resident));
            }
        }
        else if (normalized.Content is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> entry in normalized.Content.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                if (!ContentBuckets.TryMap(entry.Key, out ContentBucket bucket))
                {
                    continue;
                }

                int position = nextPosition++;
                _ = usedPositions.Add(position);
                parts.Add(MeasureToCollectedPart(position, bucket, entry.Value ?? string.Empty, null, null, null, null));
            }
        }

        if (!string.IsNullOrEmpty(logBody))
        {
            int position = nextPosition;
            while (!usedPositions.Add(position))
            {
                position++;
            }

            ContentBucket bodyBucket = ResolveBodyBucket(eventName);
            parts.Add(MeasureToCollectedPart(position, bodyBucket, logBody, null, null, null, null));
        }

        return parts;
    }

    private static CollectedContentPart MeasureToCollectedPart(
        int position,
        ContentBucket bucket,
        string rawText,
        long? reportedTokens,
        string? server,
        string? role,
        bool? resident)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(rawText, CanonValidation.MaxContentPartBytes);
        string? plainText = measurement.IsDocument ? null : measurement.PlainText;
        CollectedWithheldText? withheld = measurement.IsDocument ? measurement.ToWithheldText() : null;
        long? originalBytes = measurement.IsDocument ? null : measurement.OriginalUtf8BytesWhenTruncated;

        return new CollectedContentPart(
            Position: position,
            Bucket: bucket,
            PlainText: plainText,
            Withheld: withheld,
            ReportedTokens: reportedTokens,
            Server: server,
            Role: role,
            Resident: resident,
            OriginalUtf8BytesWhenTruncated: originalBytes);
    }

    private static ContentBucket ResolveBodyBucket(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return ContentBucket.ConversationHistory;
        }

        if (eventName.EndsWith("user_prompt", StringComparison.OrdinalIgnoreCase))
        {
            return ContentBucket.UserPrompt;
        }

        if (eventName.Contains("tool_result", StringComparison.OrdinalIgnoreCase))
        {
            return ContentBucket.ToolResultContent;
        }

        if (eventName.Contains("system", StringComparison.OrdinalIgnoreCase))
        {
            return ContentBucket.SystemPrompt;
        }

        return ContentBucket.ConversationHistory;
    }

    private static (IReadOnlyList<CollectedBatch> Batches, IReadOnlyList<CollectedProblem> StandaloneProblems) BuildBatches(
        IReadOnlyList<MappedRecordEntry> accepted,
        IReadOnlyList<(string? HarnessId, string Source, CollectedProblem Problem)> problemAssignments)
    {
        Dictionary<(string HarnessId, string Source), (List<CollectedRecord> Records, Dictionary<string, CollectedSession> Sessions, List<CollectedProblem> Problems)> groups = [];
        List<(string HarnessId, string Source)> groupOrder = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (MappedRecordEntry entry in accepted)
        {
            CollectedRecord record = entry.Record;
            if (!seenRecordIds.Add(record.RecordId))
            {
                continue;
            }

            (string HarnessId, string Source) key = (record.HarnessId, entry.Source);
            if (!groups.TryGetValue(key, out (List<CollectedRecord> Records, Dictionary<string, CollectedSession> Sessions, List<CollectedProblem> Problems) bucket))
            {
                bucket = ([], new Dictionary<string, CollectedSession>(StringComparer.Ordinal), []);
                groups[key] = bucket;
                groupOrder.Add(key);
            }

            bucket.Records.Add(record);

            if (!string.IsNullOrWhiteSpace(record.SessionKey)
                && !bucket.Sessions.ContainsKey(record.SessionKey))
            {
                bucket.Sessions[record.SessionKey] = new CollectedSession(
                    SessionKey: record.SessionKey,
                    HarnessId: record.HarnessId,
                    NativeSessionId: record.SessionKey,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []);
            }
        }

        List<CollectedProblem> standaloneProblems = [];
        foreach ((string? harnessId, string source, CollectedProblem problem) in problemAssignments)
        {
            if (harnessId is not null
                && groups.TryGetValue((harnessId, source), out (List<CollectedRecord> Records, Dictionary<string, CollectedSession> Sessions, List<CollectedProblem> Problems) bucket))
            {
                bucket.Problems.Add(problem);
            }
            else
            {
                standaloneProblems.Add(problem);
            }
        }

        List<CollectedBatch> batches = new(groupOrder.Count);
        foreach ((string harnessId, string source) key in groupOrder)
        {
            (List<CollectedRecord> records, Dictionary<string, CollectedSession> sessions, List<CollectedProblem> problems) = groups[key];
            batches.Add(new CollectedBatch(
                Source: new CollectedSource(key.harnessId, SourceKind.Otlp, key.source),
                Sessions: [.. sessions.Values],
                Records: records,
                Problems: problems));
        }

        return (batches, standaloneProblems);
    }

    private static string ExtractSource(IReadOnlyDictionary<string, JsonElement> resource)
    {
        if (resource.TryGetValue("service.name", out JsonElement serviceEl)
            && serviceEl.ValueKind == JsonValueKind.String)
        {
            string? value = serviceEl.GetString();
            if (!string.IsNullOrWhiteSpace(value) && !JsonDocumentText.IsDocument(value))
            {
                return value;
            }
        }

        return UnnamedSource;
    }

    private static IReadOnlyList<string> ObservedNamespaces(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        HashSet<string> namespaces = new(StringComparer.Ordinal);
        foreach (string key in attributes.Keys)
        {
            int dot = key.IndexOf('.', StringComparison.Ordinal);
            string ns = dot >= 0 ? key[..dot] : key;
            if (!string.IsNullOrWhiteSpace(ns) && !JsonDocumentText.IsDocument(ns))
            {
                _ = namespaces.Add(ns);
            }
        }

        return [.. namespaces.Order(StringComparer.Ordinal)];
    }

    private static string? ExtractSafeAttributeString(
        IReadOnlyDictionary<string, JsonElement> attributes,
        IReadOnlyList<string> keys)
    {
        foreach (string key in keys)
        {
            if (attributes.TryGetValue(key, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? text = el.GetString();
                if (!string.IsNullOrWhiteSpace(text) && !JsonDocumentText.IsDocument(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static long? ExtractOptionalNonNegLong(
        IReadOnlyDictionary<string, JsonElement> attributes,
        IReadOnlyList<string> keys)
    {
        foreach (string key in keys)
        {
            if (!attributes.TryGetValue(key, out JsonElement el))
            {
                continue;
            }

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long longValue) && longValue >= 0)
            {
                return longValue;
            }

            if (el.ValueKind == JsonValueKind.Number
                && el.TryGetDouble(out double doubleValue)
                && double.IsFinite(doubleValue)
                && doubleValue >= 0
                && doubleValue <= long.MaxValue)
            {
                return (long)Math.Round(doubleValue, MidpointRounding.AwayFromZero);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts a finite non-negative reported cost from <paramref name="normalizedCost"/> or
    /// fallback USD cost attributes in <paramref name="attributes"/>.
    /// </summary>
    /// <param name="normalizedCost">Normalized adapter cost block.</param>
    /// <param name="attributes">Optional raw OTLP attributes inspected when <paramref name="normalizedCost"/> has no cost.</param>
    /// <returns>The reported <see cref="CollectedCost"/>, or <see langword="null"/> when absent or invalid.</returns>
    public static CollectedCost? ExtractReportedCost(
        CostBlock normalizedCost,
        IReadOnlyDictionary<string, JsonElement>? attributes)
    {
        ArgumentNullException.ThrowIfNull(normalizedCost);

        if (normalizedCost.Value is double costValue
            && double.IsFinite(costValue)
            && costValue >= 0
            && !string.IsNullOrWhiteSpace(normalizedCost.Currency)
            && !JsonDocumentText.IsDocument(normalizedCost.Currency))
        {
            return new CollectedCost(costValue, normalizedCost.Currency);
        }

        if (attributes is null)
        {
            return null;
        }

        foreach (string key in CostUsdKeys)
        {
            if (attributes.TryGetValue(key, out JsonElement el)
                && el.ValueKind == JsonValueKind.Number
                && el.TryGetDouble(out double usd)
                && double.IsFinite(usd)
                && usd >= 0)
            {
                return new CollectedCost(usd, "USD");
            }
        }

        return null;
    }

    private static string ProblemSeverityWireString(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Error => "error",
        ProblemSeverity.Warning => "warning",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown ProblemSeverity."),
    };

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);

    private static string SafeProblemMessage(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) || JsonDocumentText.IsDocument(message) ? fallback : message;


    private sealed record PreparedLog(
        int Index,
        OtlpLog Log,
        RawSpan RawSpan,
        string Source,
        string? SessionKey,
        DateTimeOffset StartedAtUtc,
        string TimestampUtc,
        string? EventName);

    private sealed record MappedRecordEntry(
        string Source,
        CollectedRecord Record);
}

using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Canonical ingest pipeline translating decoded <see cref="OtlpSpan"/> batches into attributed
/// relational <see cref="CollectedBatch"/> payloads, quarantine entries, and problems over
/// <see cref="IRelationalStoreWriter"/> and <see cref="IOperationalStore"/>.
/// </summary>
public static class CanonIngestPipeline
{
    private const string UnnamedSource = "otlp";

    private static readonly string[] ModelKeys = ["gen_ai.response.model", "gen_ai.request.model", "model"];

    private static readonly JsonSerializerOptions RawSpanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Converts a decoded <see cref="OtlpSpan"/> to a <see cref="RawSpan"/> for adapter attribution.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Adapter-facing raw span with <c>service.name</c> as source.</returns>
    public static RawSpan ToRawSpan(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);

        string source = UnnamedSource;
        if (span.Resource.TryGetValue("service.name", out JsonElement serviceEl)
            && serviceEl.ValueKind == JsonValueKind.String)
        {
            string? service = serviceEl.GetString();
            if (!string.IsNullOrEmpty(service))
            {
                source = service;
            }
        }

        return new RawSpan(
            SpanId: span.SpanId,
            TraceId: span.TraceId,
            ParentSpanId: span.ParentSpanId,
            Source: source,
            Attributes: span.Attributes,
            Name: span.Name,
            Kind: span.Kind,
            Timestamp: span.Timestamp);
    }

    /// <summary>
    /// Extracts the sorted distinct leading dot-delimited namespaces present in <paramref name="attributes"/>.
    /// </summary>
    /// <param name="attributes">Raw span attribute bag.</param>
    /// <returns>Sorted distinct namespace prefixes.</returns>
    public static IReadOnlyList<string> ObservedNamespaces(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        HashSet<string> namespaces = new(StringComparer.Ordinal);
        foreach (string key in attributes.Keys)
        {
            int dot = key.IndexOf('.', StringComparison.Ordinal);
            namespaces.Add(dot >= 0 ? key[..dot] : key);
        }

        return [.. namespaces.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Returns whether <paramref name="span"/> looks like ambient structural telemetry (HTTP, DB,
    /// healthcheck, or server span) rather than a model call lacking an adapter.
    /// </summary>
    /// <param name="span">Adapter-facing raw span.</param>
    /// <returns><see langword="true"/> for structural telemetry; otherwise <see langword="false"/>.</returns>
    public static bool IsStructuralSpan(RawSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (string.Equals(span.Kind, "server", StringComparison.Ordinal))
        {
            return true;
        }

        if (span.Name.Contains("health", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string key in span.Attributes.Keys)
        {
            if (key.StartsWith("http.", StringComparison.Ordinal)
                || key.StartsWith("db.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs attribution, normalization, token validation, and relational persistence for a batch of
    /// decoded OTLP spans against <paramref name="writer"/> and <paramref name="operationalStore"/>.
    /// </summary>
    /// <param name="spans">Decoded OTLP spans to ingest.</param>
    /// <param name="writer">Relational store writer receiving mapped batches and diagnostic problems.</param>
    /// <param name="operationalStore">Operational store receiving quarantined spans.</param>
    /// <param name="adapters">Harness adapters in tie-break order (defaults to <see cref="CanonAdapters.DefaultAdapters"/>).</param>
    /// <returns>Per-batch accepted, quarantined, and rejected counters.</returns>
    public static IngestOutcome IngestBatch(
        IReadOnlyList<OtlpSpan> spans,
        IRelationalStoreWriter writer,
        IOperationalStore operationalStore,
        IReadOnlyList<IHarnessAdapter>? adapters = null)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(operationalStore);

        if (spans.Count == 0)
        {
            return new IngestOutcome(0, 0, 0);
        }

        IngestSpanNormalizer.NormalizedSpanBatch normalized = NormalizeBatch(spans, adapters);

        foreach (StoredQuarantinedSpan quarantinedSpan in normalized.QuarantinedSpans)
        {
            operationalStore.PutQuarantinedSpan(quarantinedSpan);
        }

        foreach (CollectedBatch batch in normalized.RelationalBatches)
        {
            writer.WriteBatch(batch);
        }

        foreach (CollectedProblem problem in normalized.StandaloneProblems)
        {
            writer.WriteProblem(problem);
        }

        return normalized.Outcome;
    }

    /// <summary>
    /// Performs pure in-memory attribution, normalization, and validation over <paramref name="spans"/>
    /// without touching any store, producing both legacy DTOs and normalized relational DTO batches.
    /// </summary>
    /// <param name="spans">Decoded OTLP spans to normalize.</param>
    /// <param name="adapters">Harness adapters in tie-break order (defaults to <see cref="CanonAdapters.DefaultAdapters"/>).</param>
    /// <returns>Normalized records, quarantined spans, problems, relational batches, and outcome counters.</returns>
    internal static IngestSpanNormalizer.NormalizedSpanBatch NormalizeBatch(
        IReadOnlyList<OtlpSpan> spans,
        IReadOnlyList<IHarnessAdapter>? adapters = null)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0)
        {
            return IngestSpanNormalizer.NormalizedSpanBatch.Empty;
        }

        IReadOnlyList<IHarnessAdapter> activeAdapters = adapters ?? CanonAdapters.DefaultAdapters;
        AdapterRegistry registry = new(activeAdapters);
        Dictionary<string, IHarnessAdapter> adapterByName = new(StringComparer.Ordinal);
        foreach (IHarnessAdapter adapter in activeAdapters)
        {
            adapterByName[adapter.Name] = adapter;
        }

        List<RawSpan> rawSpans = new(spans.Count);
        foreach (OtlpSpan span in spans)
        {
            rawSpans.Add(ToRawSpan(span));
        }

        HashSet<int> opencodeVersionQuarantined = [];
        HashSet<int> droidVersionQuarantined = [];
        List<RawSpan> votable = new(rawSpans.Count);

        for (int index = 0; index < rawSpans.Count; index++)
        {
            RawSpan raw = rawSpans[index];
            if (CanonAdapters.IsOpencodeEnvelopeSpan(raw) && !CanonAdapters.IsSupportedOpencodeEnvelope(raw))
            {
                opencodeVersionQuarantined.Add(index);
                continue;
            }

            if (CanonAdapters.IsDroidEnvelopeSpan(raw) && !CanonAdapters.IsSupportedDroidEnvelope(raw))
            {
                droidVersionQuarantined.Add(index);
                continue;
            }

            votable.Add(raw);
        }

        IReadOnlyDictionary<string, string> attributed = registry.Attribute(votable);

        int quarantined = 0;
        int rejected = 0;
        List<CanonicalRecord> accepted = [];
        List<QuarantineRow> legacyQuarantined = [];
        List<StoredQuarantinedSpan> relationalQuarantined = [];
        List<SpanProblemRow> legacyProblems = [];
        List<CollectedProblem> allRelationalProblems = [];
        List<(string HarnessId, string Source, CollectedProblem Problem)> problemAssignments = [];
        HashSet<string> usedProblemKeys = new(StringComparer.Ordinal);

        for (int index = 0; index < rawSpans.Count; index++)
        {
            RawSpan raw = rawSpans[index];
            OtlpSpan otlpSpan = spans[index];
            IReadOnlyList<string> namespaces = ObservedNamespaces(raw.Attributes);

            if (opencodeVersionQuarantined.Contains(index))
            {
                AddQuarantine(
                    legacyQuarantined,
                    relationalQuarantined,
                    raw,
                    otlpSpan,
                    namespaces,
                    CanonAdapters.UnknownOpencodeContractVersionReason,
                    harness: null);
                quarantined++;
                continue;
            }

            if (droidVersionQuarantined.Contains(index))
            {
                AddQuarantine(
                    legacyQuarantined,
                    relationalQuarantined,
                    raw,
                    otlpSpan,
                    namespaces,
                    CanonAdapters.UnknownDroidContractVersionReason,
                    harness: null);
                quarantined++;
                continue;
            }

            if (!attributed.TryGetValue(raw.SpanId, out string? harness))
            {
                AddQuarantine(
                    legacyQuarantined,
                    relationalQuarantined,
                    raw,
                    otlpSpan,
                    namespaces,
                    IsStructuralSpan(raw) ? "non-model span" : "unclaimed",
                    harness: null);
                quarantined++;
                continue;
            }

            if (string.Equals(harness, "droid", StringComparison.Ordinal) && !CanonAdapters.IsDroidEnvelopeSpan(raw))
            {
                AddQuarantine(
                    legacyQuarantined,
                    relationalQuarantined,
                    raw,
                    otlpSpan,
                    namespaces,
                    IsStructuralSpan(raw) ? "non-model span" : "unclaimed",
                    harness: null);
                quarantined++;
                continue;
            }

            if (!adapterByName.TryGetValue(harness, out IHarnessAdapter? adapter))
            {
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"attribution named unregistered harness \"{harness}\""));
            }

            CanonicalRecord normalized;
            try
            {
                normalized = adapter.Normalize(raw);
            }
            catch (Exception exception) when (CanonAdapters.IsNormalizeRejection(exception))
            {
                rejected++;
                string? sessionId = CanonAdapters.CanonicalSessionId(raw.Attributes);
                string severity = CanonModelWireStrings.ProblemWireString(ProblemSeverity.Error);
                string message = SafeProblemMessage(exception.Message, "Adapter normalization failed.");
                SpanProblemRow legacyProblem = new(
                    SpanId: raw.SpanId,
                    Severity: severity,
                    Code: "ADAPTER_NORMALIZE_FAILED",
                    Message: message,
                    Stack: SafeOptionalText(exception.StackTrace),
                    Location: raw.SpanId,
                    SessionId: sessionId,
                    Harness: harness,
                    Timestamp: otlpSpan.Timestamp);
                legacyProblems.Add(legacyProblem);

                CollectedProblem relationalProblem = new(
                    ProblemKey: CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp:", raw.SpanId, "ADAPTER_NORMALIZE_FAILED", index),
                    Severity: severity,
                    Code: "ADAPTER_NORMALIZE_FAILED",
                    Message: message,
                    SpanId: raw.SpanId,
                    Location: raw.SpanId,
                    SessionKey: string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                    HarnessId: harness,
                    TimestampUtc: otlpSpan.Timestamp,
                    Stack: SafeOptionalText(exception.StackTrace));
                allRelationalProblems.Add(relationalProblem);
                problemAssignments.Add((harness, raw.Source, relationalProblem));
                continue;
            }

            CanonicalRecord timed = normalized with
            {
                Timestamp = otlpSpan.Timestamp,
                DurationMs = (long)Math.Round(otlpSpan.DurationMs, MidpointRounding.AwayFromZero),
                Status = string.Equals(normalized.Status, "error", StringComparison.Ordinal)
                    ? "error"
                    : otlpSpan.Status.Code,
            };

            if (string.Equals(timed.Op, "unspecified", StringComparison.Ordinal))
            {
                AddQuarantine(
                    legacyQuarantined,
                    relationalQuarantined,
                    raw,
                    otlpSpan,
                    namespaces,
                    "non-model span",
                    harness);
                quarantined++;
                continue;
            }

            Problem? problem = adapter.Validate(timed);
            if (problem is not null)
            {
                rejected++;
                string severity = CanonModelWireStrings.ProblemWireString(problem.Severity);
                string message = SafeProblemMessage(problem.Message, "Span failed validation.");
                string location = problem.Location ?? timed.SpanId;
                SpanProblemRow legacyProblem = new(
                    SpanId: timed.SpanId,
                    Severity: severity,
                    Code: problem.Code,
                    Message: message,
                    Stack: SafeOptionalText(problem.Stack),
                    Location: location,
                    SessionId: timed.SessionId,
                    Harness: timed.Harness,
                    Timestamp: timed.Timestamp);
                legacyProblems.Add(legacyProblem);

                CollectedProblem relationalProblem = new(
                    ProblemKey: CanonRecordVocabulary.UniqueProblemKey(usedProblemKeys, "otlp:", timed.SpanId, problem.Code, index),
                    Severity: severity,
                    Code: problem.Code,
                    Message: message,
                    SpanId: timed.SpanId,
                    Location: location,
                    SessionKey: string.IsNullOrWhiteSpace(timed.SessionId) ? null : timed.SessionId,
                    HarnessId: timed.Harness,
                    TimestampUtc: timed.Timestamp,
                    Stack: SafeOptionalText(problem.Stack));
                allRelationalProblems.Add(relationalProblem);
                problemAssignments.Add((timed.Harness, raw.Source, relationalProblem));
                continue;
            }

            accepted.Add(timed);
        }

        (IReadOnlyList<CollectedBatch> relationalBatches, IReadOnlyList<CollectedProblem> standaloneProblems) =
            BuildRelationalBatches(accepted, problemAssignments);

        IngestOutcome outcome = new(accepted.Count, quarantined, rejected);
        return new IngestSpanNormalizer.NormalizedSpanBatch(
            accepted,
            legacyQuarantined,
            legacyProblems,
            relationalBatches,
            relationalQuarantined,
            allRelationalProblems,
            standaloneProblems,
            outcome);
    }

    private static (IReadOnlyList<CollectedBatch> Batches, IReadOnlyList<CollectedProblem> StandaloneProblems)
        BuildRelationalBatches(
            IReadOnlyList<CanonicalRecord> accepted,
            IReadOnlyList<(string HarnessId, string Source, CollectedProblem Problem)> problemAssignments)
    {
        Dictionary<(string HarnessId, string Source), (List<CollectedRecord> Records, Dictionary<string, CollectedSession> Sessions, List<CollectedProblem> Problems)> groups = [];
        List<(string HarnessId, string Source)> groupOrder = [];
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        foreach (CanonicalRecord record in accepted)
        {
            if (!seenRecordIds.Add(record.SpanId))
            {
                continue;
            }

            string source = string.IsNullOrWhiteSpace(record.Source) ? UnnamedSource : record.Source;
            (string HarnessId, string Source) key = (record.Harness, source);
            if (!groups.TryGetValue(key, out (List<CollectedRecord> Records, Dictionary<string, CollectedSession> Sessions, List<CollectedProblem> Problems) bucket))
            {
                bucket = ([], new Dictionary<string, CollectedSession>(StringComparer.Ordinal), []);
                groups[key] = bucket;
                groupOrder.Add(key);
            }

            CollectedRecord collected = ToCollectedRecord(record);
            bucket.Records.Add(collected);

            if (!string.IsNullOrWhiteSpace(collected.SessionKey))
            {
                string? sessionLabel = ExtractSafeAttributeString(record.Raw, [CanonSynth.SessionLabelKey]);
                if (!bucket.Sessions.TryGetValue(collected.SessionKey, out CollectedSession? existing))
                {
                    bucket.Sessions[collected.SessionKey] = new CollectedSession(
                        SessionKey: collected.SessionKey,
                        HarnessId: collected.HarnessId,
                        NativeSessionId: collected.SessionKey,
                        Workspace: null,
                        Title: sessionLabel,
                        ReportedUsage: []);
                }
                else if (string.IsNullOrWhiteSpace(existing.Title) && sessionLabel is not null)
                {
                    bucket.Sessions[collected.SessionKey] = existing with { Title = sessionLabel };
                }
            }
        }

        List<CollectedProblem> standaloneProblems = [];
        foreach ((string harnessId, string source, CollectedProblem problem) in problemAssignments)
        {
            string normalizedSource = string.IsNullOrWhiteSpace(source) ? UnnamedSource : source;
            if (groups.TryGetValue((harnessId, normalizedSource), out (List<CollectedRecord> Records, Dictionary<string, CollectedSession> Sessions, List<CollectedProblem> Problems) bucket))
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

    private static CollectedRecord ToCollectedRecord(CanonicalRecord record)
    {
        DateTimeOffset startedAt = DateTimeOffset.TryParse(
            record.Timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.UnixEpoch;

        DateTimeOffset? endedAt = record.DurationMs > 0
            ? startedAt.AddMilliseconds(record.DurationMs)
            : null;

        long? reportedDurationMs = record.DurationMs >= 0 ? record.DurationMs : null;

        // A declared-unmeasured class stays absent. Writing the default zero would make the
        // session overview render a measured 0 for a harness that reported no counter.
        bool inputUnmeasured = IsDeclaredNotMeasurable(record.Measurability, "token_usage");
        bool outputUnmeasured = IsDeclaredNotMeasurable(record.Measurability, "output");
        List<CollectedTokenCount> tokens = [];
        if (!inputUnmeasured)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, Math.Max(0L, record.Tokens.FreshInput)));
            tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, Math.Max(0L, record.Tokens.CacheRead)));
            tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, Math.Max(0L, record.Tokens.CacheCreation)));
        }

        if (!outputUnmeasured)
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Output, Math.Max(0L, record.Tokens.Output)));
            if (record.Tokens.Reasoning is long reasoning && reasoning >= 0)
            {
                tokens.Add(new CollectedTokenCount(TokenClass.Reasoning, reasoning));
            }
        }

        CollectedCost? cost = OtlpLogMapper.ExtractReportedCost(record.Cost, record.Raw);

        string? model = ExtractSafeAttributeString(record.Raw, ModelKeys);
        string? errorMessage = ExtractSafeAttributeString(record.Raw, ["error.message"]);
        string? sessionKey = string.IsNullOrWhiteSpace(record.SessionId) ? null : record.SessionId;
        string? parentRecordId = string.IsNullOrWhiteSpace(record.ParentSpanId) ? null : record.ParentSpanId;
        string? traceId = string.IsNullOrWhiteSpace(record.TraceId) ? null : record.TraceId;
        string name = !string.IsNullOrWhiteSpace(record.Name) && !JsonDocumentText.IsDocument(record.Name)
            ? record.Name
            : record.Op;

        CollectedToolCall? tool = BuildToolCall(record);
        IReadOnlyList<string> skillsInvoked = SkillExtraction.ExtractInvoked(record.Raw, tool?.ToolName);
        IReadOnlyList<CollectedToolDefinition> toolDefinitions = BuildToolDefinitions(record, model, out HashSet<int> definitionPartIndexes);
        (string? ResponseId, string? RequestId) joinIds = OtlpJoinIds.Read(record.Harness, record.Raw);

        return new CollectedRecord(
            RecordId: record.SpanId,
            HarnessId: record.Harness,
            Op: CanonRecordVocabulary.ParseRecordOp(record.Op),
            Kind: CanonRecordVocabulary.ParseRecordKind(record.Kind),
            Name: name,
            StartedAt: startedAt,
            Status: CanonRecordVocabulary.ParseRecordStatus(record.Status),
            SessionKey: sessionKey,
            ParentRecordId: parentRecordId,
            TraceId: traceId,
            Model: model,
            EndedAt: endedAt,
            ReportedDurationMs: reportedDurationMs,
            Tokens: tokens,
            ReportedCost: cost,
            Parts: BuildCollectedParts(record, skipToolDefinitions: toolDefinitions.Count > 0, definitionPartIndexes, model),
            Tool: tool,
            Agent: null,
            SkillsInvoked: skillsInvoked,
            AgentInstanceId: null,
            ToolDefinitions: toolDefinitions,
            Signals: new CollectedRecordSignals(errorMessage, null, null, null),
            ReportedInputTokens: inputUnmeasured || record.Tokens.ReportedInput < 0 ? null : record.Tokens.ReportedInput,
            ReportedOutputTokens: outputUnmeasured || record.Tokens.ReportedOutput < 0 ? null : record.Tokens.ReportedOutput,
            ResponseId: joinIds.ResponseId,
            RequestId: joinIds.RequestId);
    }

    /// <summary>
    /// Returns whether <paramref name="measurability"/> declares <paramref name="metric"/> not measurable.
    /// A missing entry is not a declaration: the token classes stay as the adapter wrote them.
    /// </summary>
    private static bool IsDeclaredNotMeasurable(IReadOnlyDictionary<string, JsonElement>? measurability, string metric)
    {
        if (measurability is null || !measurability.TryGetValue(metric, out JsonElement element))
        {
            return false;
        }

        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("availability", out JsonElement availability)
            && availability.ValueKind == JsonValueKind.String
            && string.Equals(availability.GetString(), "not_measurable", StringComparison.Ordinal);
    }

    private static readonly string[] ToolNameKeys =
    [
        "gen_ai.tool.name",
        "opencode.tool.name",
        "factory.tool.name",
        "tool.name",
        "tool_name",
    ];

    private static readonly string[] ToolCallIdKeys =
    [
        "opencode.tool.call_id",
        "gen_ai.tool.call.id",
        "tool.call_id",
    ];

    private static readonly string[] SkillAttributeKeys =
    [
        "gen_ai.skill.name",
        "skill.name",
        "skill_name",
        "kyber.skills.invoked",
    ];

    private static readonly string[] ToolArgumentKeys =
    [
        "gen_ai.tool.call.arguments",
        "gen_ai.tool.arguments",
        "tool.arguments",
        "tool_arguments",
        "arguments",
        "input",
        "opencode.tool.input",
        "tool.input",
    ];

    private static readonly string[] SkillArgumentPropertyKeys = ["skill", "skill_name", "name"];

    private static IReadOnlyList<string> ExtractSkillsInvoked(
        IReadOnlyDictionary<string, JsonElement>? attributes,
        string? resolvedToolName)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return [];
        }

        List<string> skills = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string key in SkillAttributeKeys)
        {
            if (attributes.TryGetValue(key, out JsonElement element))
            {
                AddSkillsFromElement(element, skills, seen);
            }
        }

        string? toolName = !string.IsNullOrWhiteSpace(resolvedToolName)
            ? resolvedToolName
            : ExtractSafeAttributeString(attributes, ToolNameKeys);
        if (string.Equals(toolName, "Skill", StringComparison.OrdinalIgnoreCase))
        {
            ExtractSkillFromToolArguments(attributes, skills, seen);
        }

        return skills;
    }

    private static void AddSkillsFromElement(JsonElement element, List<string> skills, HashSet<string> seen)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            string trimmed = raw.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in doc.RootElement.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                TryAddSkill(item.GetString(), skills, seen);
                            }
                        }

                        return;
                    }
                }
                catch (JsonException)
                {
                    return;
                }
            }

            TryAddSkill(trimmed, skills, seen);
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    TryAddSkill(item.GetString(), skills, seen);
                }
            }
        }
    }

    private static void ExtractSkillFromToolArguments(
        IReadOnlyDictionary<string, JsonElement> attributes,
        List<string> skills,
        HashSet<string> seen)
    {
        foreach (string key in ToolArgumentKeys)
        {
            if (!attributes.TryGetValue(key, out JsonElement element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                AddSkillPropertyFromObject(element, skills, seen);
                return;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                string? raw = element.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string trimmed = raw.Trim();
                if (!trimmed.StartsWith('{'))
                {
                    continue;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        AddSkillPropertyFromObject(doc.RootElement, skills, seen);
                        return;
                    }
                }
                catch (JsonException)
                {
                    return;
                }
            }
        }
    }

    private static void AddSkillPropertyFromObject(JsonElement obj, List<string> skills, HashSet<string> seen)
    {
        foreach (string propKey in SkillArgumentPropertyKeys)
        {
            if (obj.TryGetProperty(propKey, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
            {
                if (TryAddSkill(prop.GetString(), skills, seen))
                {
                    return;
                }
            }
        }
    }

    private static bool TryAddSkill(string? raw, List<string> skills, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(raw) || JsonDocumentText.IsDocument(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (seen.Add(trimmed))
        {
            skills.Add(trimmed);
        }

        return true;
    }

    /// <summary>
    /// Builds the tool-call row for a <c>tool.invoke</c> record. The name is the harness attribute
    /// when one is present, otherwise the span name the adapter already rewrote to the tool.
    /// </summary>
    private static CollectedToolCall? BuildToolCall(CanonicalRecord record)
    {
        if (!string.Equals(record.Op, "tool.invoke", StringComparison.Ordinal))
        {
            return null;
        }

        string? toolName = ExtractSafeAttributeString(record.Raw, ToolNameKeys);
        if (string.IsNullOrWhiteSpace(toolName)
            && !string.IsNullOrWhiteSpace(record.Name)
            && !string.Equals(record.Name, record.Op, StringComparison.Ordinal)
            && !record.Name.EndsWith(".tool", StringComparison.Ordinal))
        {
            toolName = record.Name;
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        string? callId = ExtractSafeAttributeString(record.Raw, ToolCallIdKeys);
        bool? succeeded = record.Status switch
        {
            "ok" => true,
            "error" => false,
            _ => null,
        };

        return new CollectedToolCall(
            CallId: string.IsNullOrWhiteSpace(callId) ? record.SpanId : callId,
            ToolName: toolName,
            McpServerName: null,
            McpToolName: null,
            Input: null,
            Result: null,
            Succeeded: succeeded,
            Error: null,
            ExitCode: null);
    }

    private static IReadOnlyList<CollectedToolDefinition> BuildToolDefinitions(
        CanonicalRecord record,
        string? model,
        out HashSet<int> contributingPartIndexes)
    {
        List<CollectedToolDefinition> definitions = [];
        HashSet<string> seenNames = new(StringComparer.Ordinal);
        HashSet<int> contributing = [];
        int position = 0;

        if (record.Parts is { Count: > 0 })
        {
            for (int partIndex = 0; partIndex < record.Parts.Count; partIndex++)
            {
                ContentPart part = record.Parts[partIndex];
                if (!string.Equals(part.Part, "tool_definitions", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(part.Text))
                {
                    continue;
                }

                if (AddToolDefinitionsFromText(part.Text, part.Server, part.Tokens, model, definitions, seenNames, ref position))
                {
                    contributing.Add(partIndex);
                }
            }
        }
        else if (record.Content.TryGetValue("tool_definitions", out string? text) && !string.IsNullOrWhiteSpace(text))
        {
            _ = AddToolDefinitionsFromText(text, null, null, model, definitions, seenNames, ref position);
        }

        contributingPartIndexes = contributing;
        return definitions;
    }

    /// <summary>
    /// Parses the tool definitions in one part's text into <paramref name="definitions"/>, and
    /// answers whether any were added: a part the parser refused (truncated JSON, no name) has no
    /// definitions to store and stays as content instead.
    /// </summary>
    private static bool AddToolDefinitionsFromText(
        string text,
        string? defaultServer,
        long? partTokens,
        string? model,
        List<CollectedToolDefinition> definitions,
        HashSet<string> seenNames,
        ref int position)
    {
        int addedBefore = definitions.Count;
        string trimmed = text.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed);
                JsonElement root = doc.RootElement;
                bool isSingleItem = root.ValueKind == JsonValueKind.Object
                    && !(root.TryGetProperty("tools", out JsonElement toolsProp) && toolsProp.ValueKind == JsonValueKind.Array);

                IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : root.ValueKind == JsonValueKind.Object
                      && root.TryGetProperty("tools", out JsonElement toolsEl)
                      && toolsEl.ValueKind == JsonValueKind.Array
                        ? toolsEl.EnumerateArray()
                        : [root];

                foreach (JsonElement item in items)
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && !JsonDocumentText.IsDocument(s))
                        {
                            string name = s.Trim();
                            if (seenNames.Add(name))
                            {
                                // A single-item part with its own reported count keeps that count
                                // unlabelled; every other schema is measured from its text, so the
                                // count carries the measuring tokenizer.
                                long tokens;
                                string? tokenizer = null;
                                bool isModelOwn = false;
                                if (isSingleItem && partTokens is > 0)
                                {
                                    tokens = partTokens.Value;
                                }
                                else
                                {
                                    ToolContentMeasurement measurement = ToolContentMeasurer.Measure(name, model: model);
                                    tokens = Math.Max(1L, measurement.MeasuredTokens);
                                    tokenizer = measurement.MeasuredTokenizer;
                                    isModelOwn = measurement.MeasuredTokenizerIsModelOwn;
                                }

                                definitions.Add(new CollectedToolDefinition(position++, name, SafeOptionalText(defaultServer), tokens, tokenizer, isModelOwn));
                            }
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        string? name = ExtractToolNameFromObject(item);
                        if (name is not null && seenNames.Add(name))
                        {
                            string? server = ExtractToolServerFromObject(item) ?? defaultServer;
                            long tokens;
                            string? tokenizer = null;
                            bool isModelOwn = false;
                            if (isSingleItem && partTokens is > 0)
                            {
                                tokens = partTokens.Value;
                            }
                            else
                            {
                                ToolContentMeasurement measurement = ToolContentMeasurer.Measure(item.GetRawText(), model: model);
                                tokens = Math.Max(1L, measurement.MeasuredTokens);
                                tokenizer = measurement.MeasuredTokenizer;
                                isModelOwn = measurement.MeasuredTokenizerIsModelOwn;
                            }

                            definitions.Add(new CollectedToolDefinition(position++, name, SafeOptionalText(server), tokens, tokenizer, isModelOwn));
                        }
                    }
                }

                return definitions.Count > addedBefore;
            }
            catch (JsonException)
            {
                return definitions.Count > addedBefore;
            }
        }

        if (!JsonDocumentText.IsDocument(trimmed) && seenNames.Add(trimmed))
        {
            // A part-reported count stays unlabelled; a count measured from the definition's own
            // text carries the measuring tokenizer.
            long tokens;
            string? tokenizer = null;
            bool isModelOwn = false;
            if (partTokens is > 0)
            {
                tokens = partTokens.Value;
            }
            else
            {
                ToolContentMeasurement measurement = ToolContentMeasurer.Measure(trimmed, model: model);
                tokens = Math.Max(1L, measurement.MeasuredTokens);
                tokenizer = measurement.MeasuredTokenizer;
                isModelOwn = measurement.MeasuredTokenizerIsModelOwn;
            }

            definitions.Add(new CollectedToolDefinition(position++, trimmed, SafeOptionalText(defaultServer), tokens, tokenizer, isModelOwn));
        }

        return definitions.Count > addedBefore;
    }

    private static string? ExtractToolNameFromObject(JsonElement item)
    {
        if (item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String)
        {
            string? value = n.GetString();
            if (!string.IsNullOrWhiteSpace(value) && !JsonDocumentText.IsDocument(value))
            {
                return value.Trim();
            }
        }

        if (item.TryGetProperty("function", out JsonElement fn) && fn.ValueKind == JsonValueKind.Object
            && fn.TryGetProperty("name", out JsonElement fnName) && fnName.ValueKind == JsonValueKind.String)
        {
            string? value = fnName.GetString();
            if (!string.IsNullOrWhiteSpace(value) && !JsonDocumentText.IsDocument(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? ExtractToolServerFromObject(JsonElement item)
    {
        foreach (string key in ToolServerPropertyKeys)
        {
            if (item.TryGetProperty(key, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? value = prop.GetString();
                if (!string.IsNullOrWhiteSpace(value) && !JsonDocumentText.IsDocument(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static readonly string[] ToolServerPropertyKeys = ["server", "mcpServer"];

    private static List<CollectedContentPart> BuildCollectedParts(
        CanonicalRecord record,
        bool skipToolDefinitions,
        HashSet<int> definitionPartIndexes,
        string? model)
    {
        List<CollectedContentPart> parts = [];
        HashSet<int> usedPositions = [];
        int nextPosition = 0;

        if (record.Parts is { Count: > 0 })
        {
            for (int partIndex = 0; partIndex < record.Parts.Count; partIndex++)
            {
                ContentPart part = record.Parts[partIndex];
                if (!ContentBuckets.TryMap(part.Part, out ContentBucket bucket))
                {
                    continue;
                }

                // Skip only the tool_definitions parts whose text became stored definitions; a
                // sibling part the parser refused stays as content so its text and reported
                // tokens survive.
                if (skipToolDefinitions && bucket == ContentBucket.ToolDefinitions && definitionPartIndexes.Contains(partIndex))
                {
                    continue;
                }

                int position = part.Order is >= 0 ? part.Order.Value : nextPosition;
                while (!usedPositions.Add(position))
                {
                    position++;
                }

                nextPosition = Math.Max(nextPosition, position + 1);

                ToolContentMeasurement measurement = ToolContentMeasurer.Measure(part.Text ?? string.Empty, model: model);
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

        if (record.Content is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> entry in record.Content.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                if (!ContentBuckets.TryMap(entry.Key, out ContentBucket bucket))
                {
                    continue;
                }

                if (skipToolDefinitions && bucket == ContentBucket.ToolDefinitions)
                {
                    continue;
                }

                int position = nextPosition++;
                _ = usedPositions.Add(position);

                ToolContentMeasurement measurement = ToolContentMeasurer.Measure(entry.Value ?? string.Empty, model: model);
                string? plainText = measurement.IsDocument ? null : measurement.PlainText;
                CollectedWithheldText? withheld = measurement.IsDocument ? measurement.ToWithheldText() : null;
                long? originalBytes = measurement.IsDocument ? null : measurement.OriginalUtf8BytesWhenTruncated;

                parts.Add(new CollectedContentPart(
                    Position: position,
                    Bucket: bucket,
                    PlainText: plainText,
                    Withheld: withheld,
                    ReportedTokens: null,
                    Server: null,
                    Role: null,
                    Resident: null,
                    OriginalUtf8BytesWhenTruncated: originalBytes));
            }
        }

        return parts;
    }

    private static void AddQuarantine(
        List<QuarantineRow> legacyQuarantined,
        List<StoredQuarantinedSpan> relationalQuarantined,
        RawSpan raw,
        OtlpSpan otlpSpan,
        IReadOnlyList<string> namespaces,
        string reason,
        string? harness)
    {
        legacyQuarantined.Add(new QuarantineRow(
            SpanId: raw.SpanId,
            ObservedNamespaces: namespaces,
            Reason: reason,
            Source: raw.Source,
            Name: raw.Name,
            Timestamp: otlpSpan.Timestamp,
            RawPayload: BoundedSpanPayload(otlpSpan),
            Harness: harness));

        // Decision OD-6 / DD-10: relational quarantine drops raw payloads completely.
        relationalQuarantined.Add(new StoredQuarantinedSpan(
            SpanId: raw.SpanId,
            Reason: reason,
            ObservedNamespaces: namespaces,
            Source: SafeOptionalText(raw.Source),
            Name: SafeOptionalText(raw.Name),
            TimestampUtc: SafeOptionalText(otlpSpan.Timestamp),
            HarnessId: SafeOptionalText(harness)));
    }

    private static string? ExtractSafeAttributeString(
        IReadOnlyDictionary<string, JsonElement>? attributes,
        IReadOnlyList<string> keys)
    {
        if (attributes is null)
        {
            return null;
        }

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

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    private static string SafeProblemMessage(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) || JsonDocumentText.IsDocument(message) ? fallback : message;

    private static string BoundedSpanPayload(OtlpSpan span)
    {
        string json = JsonSerializer.Serialize(span, RawSpanJsonOptions);
        return CanonValidation.BoundRawPayload(json);
    }
}

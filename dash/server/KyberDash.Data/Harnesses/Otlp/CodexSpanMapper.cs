using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Pure <see cref="IHarnessMapper"/> for Codex OTLP trace spans, translating decoded
/// <see cref="OtlpSpan"/> payloads via <see cref="OtlpSpanNative"/> into static
/// <see cref="CollectedRecord"/> and <see cref="CollectedBatch"/> DTOs using Codex's
/// cache-inclusive token convention.
/// </summary>
/// <remarks>
/// Only the <c>CodexAdapter</c> allowlist (<see cref="AllowedKeys"/> and
/// <see cref="AllowedPrefixes"/>) reaches the DTOs: span attributes are filtered before
/// parsing, so prompts, tool arguments/results, <c>user.email</c>, and account identifiers
/// are dropped. The dropped fields are registered in <c>docs/dash/field-register.md</c>.
/// </remarks>
public class CodexSpanMapper : IHarnessMapper
{
    private const string MalformedOtlpCode = "MALFORMED_OTLP_PAYLOAD";

    /// <summary>Codex OTLP allowlisted exact attribute keys from <c>CodexAdapter</c>.</summary>
    public static readonly IReadOnlyList<string> AllowedKeys =
    [
        "model",
        "slug",
        "conversation.id",
        "gen_ai.session.id",
        "session.id",
        "event.timestamp",
    ];

    /// <summary>Codex OTLP allowlisted attribute key prefixes from <c>CodexAdapter</c>.</summary>
    public static readonly IReadOnlyList<string> AllowedPrefixes = ["gen_ai.usage.", "codex.usage."];

    /// <summary>Codex native session identifier attribute keys, preferred over canonical session keys.</summary>
    public static readonly IReadOnlyList<string> CodexSessionKeys = ["conversation.id"];

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

    /// <summary>Returns whether <paramref name="key"/> is in the Codex OTLP allowlist.</summary>
    /// <param name="key">Attribute key to check.</param>
    /// <returns><see langword="true"/> when the key is allowlisted exactly or by prefix.</returns>
    public static bool IsAllowlistedKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (string allowed in AllowedKeys)
        {
            if (string.Equals(key, allowed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (string prefix in AllowedPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses <paramref name="span"/> into an <see cref="OtlpSpanNative"/> keeping only
    /// allowlisted attribute keys; every other attribute is dropped before extraction.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Extracted <see cref="OtlpSpanNative"/> over allowlisted attributes only.</returns>
    public OtlpSpanNative Parse(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        return OtlpSpanNative.Parse(FilterAllowlisted(span));
    }

    /// <summary>
    /// Maps a single decoded Codex OTLP <paramref name="span"/> into a normalized
    /// <see cref="CollectedRecord"/>.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Normalized <see cref="CollectedRecord"/>.</returns>
    public CollectedRecord MapSpan(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        OtlpSpanNative native = Parse(span);
        return MapNative(native, TranscriptReaderRepository.CodexHarnessId);
    }

    /// <summary>
    /// Maps a batch of decoded Codex OTLP <paramref name="spans"/> into sessions, validated
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
            ?? (spans.Count > 0 ? Parse(spans[0]).Source : "otlp");
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

            if (TryBuildTokenUsage(native, ResolveOp(native), out TokenUsage? usage) && usage is not null)
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
                        SessionKey: SafeOptionalText(ResolveSession(native)),
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

    private static OtlpSpan FilterAllowlisted(OtlpSpan span)
    {
        IReadOnlyDictionary<string, JsonElement> attributes = span.Attributes
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Dictionary<string, JsonElement> filtered = new(attributes.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> entry in attributes)
        {
            if (IsAllowlistedKey(entry.Key))
            {
                filtered[entry.Key] = entry.Value.Clone();
            }
        }

        return span with { Attributes = filtered };
    }

    private static CollectedRecord MapNative(OtlpSpanNative native, string harnessId)
    {
        RecordOp op = ResolveOp(native);
        RecordKind kind = CanonRecordVocabulary.ParseRecordKind(native.Kind);
        RecordStatus status = CanonRecordVocabulary.ParseRecordStatus(native.StatusCode);
        string opWire = CanonRecordVocabulary.FormatRecordOp(op);
        string name = !string.IsNullOrWhiteSpace(native.Name) && !JsonDocumentText.IsDocument(native.Name)
            ? native.Name
            : opWire;

        List<CollectedTokenCount> tokens = [];
        long? reportedInputTokens = null;
        long? reportedOutputTokens = null;
        if (TryBuildTokenUsage(native, op, out TokenUsage? usage) && usage is not null)
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

        string? sessionId = ResolveSession(native);
        string? errorMessage = status == RecordStatus.Error ? SafeOptionalText(native.StatusMessage) : null;

        CollectedRecordSignals signals = new(
            ErrorMessage: errorMessage,
            EditFailures: null,
            CorrectionRule: null,
            DeclaredContextWindow: null);

        return new CollectedRecord(
            RecordId: native.SpanId,
            HarnessId: harnessId,
            Op: op,
            Kind: kind,
            Name: name,
            StartedAt: native.StartedAt,
            Status: status,
            SessionKey: SafeOptionalText(sessionId),
            ParentRecordId: SafeOptionalText(native.ParentSpanId),
            TraceId: SafeOptionalText(native.TraceId),
            Model: SafeOptionalText(native.Model),
            EndedAt: native.EndedAt,
            ReportedDurationMs: native.ReportedDurationMs,
            Tokens: tokens,
            ReportedCost: cost,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: signals,
            ReportedInputTokens: reportedInputTokens,
            ReportedOutputTokens: reportedOutputTokens);
    }

    private static RecordOp ResolveOp(OtlpSpanNative native)
    {
        if (OtlpSpanNative.HasAnyKey(native.Attributes, OtlpSpanNative.InputTokenKeys)
            || OtlpSpanNative.HasAnyKey(native.Attributes, OtlpSpanNative.OutputTokenKeys))
        {
            return RecordOp.LlmInvoke;
        }

        return RecordOp.Unspecified;
    }

    private static string? ResolveSession(OtlpSpanNative native)
    {
        return OtlpSpanNative.FirstSafeString(native.Attributes, CodexSessionKeys)
            ?? native.SessionId;
    }

    private static bool TryBuildTokenUsage(OtlpSpanNative native, RecordOp op, out TokenUsage? usage)
    {
        long? cacheCreation = OtlpSpanNative.ReadOptionalCounter(native.Attributes, OtlpSpanNative.CodexCacheWriteKeys);
        long? reasoningRaw = OtlpSpanNative.ReadOptionalCounter(native.Attributes, OtlpSpanNative.CodexReasoningKeys);

        bool hasAnyToken = native.InputTokens.HasValue
            || native.OutputTokens.HasValue
            || native.CacheReadTokens.HasValue
            || cacheCreation.HasValue
            || reasoningRaw.HasValue;

        if (!hasAnyToken && op != RecordOp.LlmInvoke)
        {
            usage = null;
            return false;
        }

        long input = native.InputTokens ?? 0;
        long cacheRead = native.CacheReadTokens ?? 0;
        long cacheCreationValue = cacheCreation ?? 0;
        long output = native.OutputTokens ?? 0;
        long? reasoning = reasoningRaw is long raw && raw != 0 ? raw : null;

        usage = CanonAdapters.InclusiveConvention(input, cacheRead, cacheCreationValue, output, reasoning);
        return true;
    }

    private static string ResolveHarnessId(string? unitHarnessId)
    {
        if (string.IsNullOrWhiteSpace(unitHarnessId))
        {
            return TranscriptReaderRepository.CodexHarnessId;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(unitHarnessId);
        return string.Equals(normalized, "codex", StringComparison.Ordinal)
            ? TranscriptReaderRepository.CodexHarnessId
            : normalized;
    }

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    private static string FormatProblemSeverity(ProblemSeverity severity) => severity switch
    {
        ProblemSeverity.Warning => "warning",
        _ => "error",
    };

}

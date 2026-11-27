using System.Globalization;
using System.Text.Json;

namespace KyberDash.Domain;

/// <summary>
/// Cross-path deduplication collapsing turns observed through both session files and OTLP telemetry
/// while preserving OTLP counters/cost as authority and recording any disagreement as a warning
/// problem (<c>dash/src/synth/dedup.ts</c>).
/// </summary>
public static class CanonSynthDedup
{
    /// <summary>Problem code stamped when file-synthesized and OTLP figures disagree for a joined turn.</summary>
    public const string DedupDisagreementCode = "DEDUP_DISAGREEMENT";

    private class SideSummary
    {
        public int Turns { get; set; }
        public string FirstSpanId { get; set; } = string.Empty;
        public long FreshInput { get; set; }
        public long CacheRead { get; set; }
        public long CacheCreation { get; set; }
        public long Output { get; set; }
        public long? Reasoning { get; set; }
        public long ReportedInput { get; set; }
        public long ReportedOutput { get; set; }
        public double CostValue { get; set; }
        public SortedSet<string> CostStatuses { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Computes the cross-path session deduplication key (<c>synth:&lt;harness&gt;:&lt;session&gt;</c>)
    /// for <paramref name="record"/>, or <see langword="null"/> if no session identity is claimed.
    /// </summary>
    public static string? DeduplicationKeyFor(CanonicalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.SpanId.StartsWith(CanonSynth.SynthSpanPrefix, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(record.TraceId))
                return null;

            string rest = record.TraceId.StartsWith(CanonSynth.SynthSpanPrefix, StringComparison.Ordinal)
                ? record.TraceId[CanonSynth.SynthSpanPrefix.Length..]
                : record.TraceId;
            int separator = rest.IndexOf(':', StringComparison.Ordinal);
            return separator < 0
                ? record.TraceId
                : $"{CanonSynth.SynthSpanPrefix}{rest[..separator]}:{rest[(separator + 1)..]}";
        }

        string? sessionId = RawSessionId(record);
        return sessionId is null ? null : $"{CanonSynth.SynthSpanPrefix}{record.Harness}:{sessionId}";
    }

    /// <summary>
    /// Computes the cross-path per-turn join key (<c>&lt;harness&gt;:&lt;session&gt;:&lt;nativeRecordId&gt;</c>)
    /// when classified harness, session ID, and native record ID are all present.
    /// </summary>
    public static string? TurnJoinKeyFor(CanonicalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (CanonSynth.IsExcludedHarness(record.Harness))
            return null;

        string? sessionId = SessionIdOf(record);
        string? nativeRecordId = NativeRecordIdOf(record);
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(nativeRecordId))
            return null;

        return $"{record.Harness}:{sessionId}:{nativeRecordId}";
    }

    private static string? SessionIdOf(CanonicalRecord record) =>
        !string.IsNullOrEmpty(record.SessionId) ? record.SessionId : RawSessionId(record);

    /// <summary>
    /// Joins one OTLP record with one file-synthesized record: keeps OTLP counters/cost and fills
    /// <see cref="CanonicalRecord.Parts"/> (and flat <see cref="CanonicalRecord.Content"/>) from the
    /// file record only when OTLP carried no structured parts.
    /// </summary>
    public static CanonicalRecord JoinOtelAndFileTurn(CanonicalRecord otlpRecord, CanonicalRecord fileRecord)
    {
        ArgumentNullException.ThrowIfNull(otlpRecord);
        ArgumentNullException.ThrowIfNull(fileRecord);

        if (otlpRecord.Parts is { Count: > 0 })
            return otlpRecord;

        if (fileRecord.Parts is null || fileRecord.Parts.Count == 0)
            return otlpRecord;

        return otlpRecord with
        {
            Parts = fileRecord.Parts,
            Content = CanonValidation.ContentFromParts(fileRecord.Parts),
        };
    }

    /// <summary>
    /// Collapses file-synthesized and OTLP records across ingest paths, persists any metric or cost
    /// disagreements to the problem sink as <c>DEDUP_DISAGREEMENT</c> warnings, and drops
    /// excluded harnesses (<c>gemini</c>, <c>vercel-gateway</c>).
    /// </summary>
    public static IReadOnlyList<CanonicalRecord> Deduplicate(
        IReadOnlyList<CanonicalRecord> synthRecords,
        IReadOnlyList<CanonicalRecord> otlpRecords,
        Action<SpanProblemRow>? recordProblem = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(synthRecords);
        ArgumentNullException.ThrowIfNull(otlpRecords);
        TimeProvider clock = timeProvider ?? TimeProvider.System;

        Dictionary<string, CanonicalRecord> fileByTurn = new(StringComparer.Ordinal);
        List<string> fileKeyOrder = [];
        List<CanonicalRecord> unmatchedFile = [];

        foreach (CanonicalRecord record in synthRecords)
        {
            if (CanonSynth.IsExcludedHarness(record.Harness))
                continue;

            string? key = TurnJoinKeyFor(record);
            if (key is null)
            {
                unmatchedFile.Add(record);
                continue;
            }

            if (!fileByTurn.ContainsKey(key))
                fileKeyOrder.Add(key);

            fileByTurn[key] = record;
        }

        List<CanonicalRecord> kept = [];
        HashSet<string> joinedFileKeys = new(StringComparer.Ordinal);

        foreach (CanonicalRecord otlp in otlpRecords)
        {
            if (CanonSynth.IsExcludedHarness(otlp.Harness))
                continue;

            string? key = TurnJoinKeyFor(otlp);
            if (key is null || !fileByTurn.TryGetValue(key, out CanonicalRecord? file))
            {
                kept.Add(otlp);
                continue;
            }

            joinedFileKeys.Add(key);
            kept.Add(JoinOtelAndFileTurn(otlp, file));
            RecordDisagreement(key, [file], [otlp], recordProblem, clock);
        }

        foreach (string key in fileKeyOrder)
        {
            if (!joinedFileKeys.Contains(key))
                kept.Add(fileByTurn[key]);
        }

        kept.AddRange(unmatchedFile);
        return kept;
    }

    private static string? RawSessionId(CanonicalRecord record)
    {
        if (record.Raw is null)
            return null;

        if (!record.Raw.TryGetValue("session.id", out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = element.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? NativeRecordIdOf(CanonicalRecord record)
    {
        if (record.Raw is null)
            return null;

        if (record.Raw.TryGetValue("provenance", out JsonElement provEl)
            && provEl.ValueKind == JsonValueKind.Object
            && provEl.TryGetProperty("nativeRecordId", out JsonElement nrEl)
            && nrEl.ValueKind == JsonValueKind.String)
        {
            string? val = nrEl.GetString();
            if (!string.IsNullOrEmpty(val))
                return val;
        }

        string[] keys = ["turnId", "gen_ai.response.id", "gen_ai.message.id"];
        foreach (string key in keys)
        {
            if (record.Raw.TryGetValue(key, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? val = el.GetString();
                if (!string.IsNullOrEmpty(val))
                    return val;
            }
        }

        return null;
    }

    private static void RecordDisagreement(
        string key,
        IReadOnlyList<CanonicalRecord> fileRecords,
        IReadOnlyList<CanonicalRecord> otlpRecords,
        Action<SpanProblemRow>? recordProblem = null,
        TimeProvider? timeProvider = null)
    {
        SideSummary file = Summarize(fileRecords);
        SideSummary otlp = Summarize(otlpRecords);
        string? what = FirstDisagreement(file, otlp);
        if (what is null)
            return;

        string? harness = otlpRecords.Count > 0
            ? otlpRecords[0].Harness
            : fileRecords.Count > 0 ? fileRecords[0].Harness : null;
        string timestamp = otlpRecords.Count > 0
            ? otlpRecords[0].Timestamp
            : fileRecords.Count > 0
                ? fileRecords[0].Timestamp
                : (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"session {key} arrived through both ingest paths and they disagree on {what}. {DescribeSide("file", file)}. {DescribeSide("OTLP", otlp)}. kept the D7-preferred OTLP path's counters (and file content only when OTLP had no parts; {otlp.Turns} record(s), first span {otlp.FirstSpanId}); the other path's figures are recorded here rather than stored");

        recordProblem?.Invoke(new SpanProblemRow(
            SpanId: otlp.FirstSpanId,
            Severity: "warning",
            Code: DedupDisagreementCode,
            Message: message,
            Location: key,
            SessionId: SessionIdOf(otlpRecords[0]) ?? SessionIdOf(fileRecords[0]),
            Harness: harness,
            Timestamp: timestamp));
    }

    private static SideSummary Summarize(IReadOnlyList<CanonicalRecord> records)
    {
        SideSummary summary = new()
        {
            Turns = records.Count,
            FirstSpanId = records.Count > 0 ? records[0].SpanId : string.Empty,
        };

        foreach (CanonicalRecord record in records)
        {
            summary.FreshInput += record.Tokens.FreshInput;
            summary.CacheRead += record.Tokens.CacheRead;
            summary.CacheCreation += record.Tokens.CacheCreation;
            summary.Output += record.Tokens.Output;
            if (record.Tokens.Reasoning is { } reasoning)
                summary.Reasoning = (summary.Reasoning ?? 0) + reasoning;

            summary.ReportedInput += record.Tokens.ReportedInput;
            summary.ReportedOutput += record.Tokens.ReportedOutput;
            if (record.Cost.Value is { } costValue)
                summary.CostValue += costValue;

            summary.CostStatuses.Add(record.Cost.Status.ToWireString());
        }

        return summary;
    }

    private static string? FirstDisagreement(SideSummary file, SideSummary otlp)
    {
        if (file.Turns != otlp.Turns)
            return string.Create(CultureInfo.InvariantCulture, $"turn count ({file.Turns} vs {otlp.Turns})");
        if (file.FreshInput != otlp.FreshInput)
            return string.Create(CultureInfo.InvariantCulture, $"fresh input ({file.FreshInput} vs {otlp.FreshInput})");
        if (file.CacheRead != otlp.CacheRead)
            return string.Create(CultureInfo.InvariantCulture, $"cache-read input ({file.CacheRead} vs {otlp.CacheRead})");
        if (file.CacheCreation != otlp.CacheCreation)
            return string.Create(CultureInfo.InvariantCulture, $"cache-creation input ({file.CacheCreation} vs {otlp.CacheCreation})");
        if (file.Output != otlp.Output)
            return string.Create(CultureInfo.InvariantCulture, $"output tokens ({file.Output} vs {otlp.Output})");
        if (file.Reasoning != otlp.Reasoning)
        {
            string left = file.Reasoning.HasValue ? file.Reasoning.Value.ToString(CultureInfo.InvariantCulture) : "absent";
            string right = otlp.Reasoning.HasValue ? otlp.Reasoning.Value.ToString(CultureInfo.InvariantCulture) : "absent";
            return $"reasoning tokens ({left} vs {right})";
        }
        if (file.ReportedInput != otlp.ReportedInput)
            return string.Create(CultureInfo.InvariantCulture, $"reported input ({file.ReportedInput} vs {otlp.ReportedInput})");
        if (file.ReportedOutput != otlp.ReportedOutput)
            return string.Create(CultureInfo.InvariantCulture, $"reported output ({file.ReportedOutput} vs {otlp.ReportedOutput})");
        if (Math.Abs(file.CostValue - otlp.CostValue) > 1e-12)
            return string.Create(CultureInfo.InvariantCulture, $"cost ({file.CostValue} vs {otlp.CostValue})");

        string fileStatuses = file.CostStatuses.Count == 0 ? "none" : string.Join('+', file.CostStatuses);
        string otlpStatuses = otlp.CostStatuses.Count == 0 ? "none" : string.Join('+', otlp.CostStatuses);
        if (!string.Equals(fileStatuses, otlpStatuses, StringComparison.Ordinal))
            return $"cost status ({fileStatuses} vs {otlpStatuses})";

        return null;
    }

    private static string DescribeSide(string side, SideSummary summary)
    {
        string statuses = summary.CostStatuses.Count == 0 ? "none" : string.Join('+', summary.CostStatuses);
        string reasoningText = summary.Reasoning.HasValue ? summary.Reasoning.Value.ToString(CultureInfo.InvariantCulture) : "absent";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{side} path reported {summary.Turns} turn(s), freshInput={summary.FreshInput}, cacheRead={summary.CacheRead}, cacheCreation={summary.CacheCreation}, output={summary.Output}, reasoning={reasoningText}, reportedInput={summary.ReportedInput}, reportedOutput={summary.ReportedOutput}, cost={summary.CostValue} ({statuses})");
    }
}

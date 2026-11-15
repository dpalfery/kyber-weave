using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Canonical ingest pipeline translating decoded <see cref="OtlpSpan"/> batches into attributed
/// <see cref="CanonicalRecord"/> rows, quarantine entries, and problems over
/// <see cref="ICanonStoreRepository"/>.
/// </summary>
public static class CanonIngestPipeline
{
    private const string UnnamedSource = "otlp";

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
    /// Runs attribution, normalization, token validation, and persistence for a batch of decoded
    /// OTLP spans against <paramref name="store"/>.
    /// </summary>
    /// <param name="spans">Decoded OTLP spans to ingest.</param>
    /// <param name="store">Canonical store repository receiving records, quarantine, and problems.</param>
    /// <param name="adapters">Harness adapters in tie-break order (defaults to <see cref="CanonAdapters.DefaultAdapters"/>).</param>
    /// <returns>Per-batch accepted, quarantined, and rejected counters.</returns>
    public static IngestOutcome IngestBatch(
        IReadOnlyList<OtlpSpan> spans,
        ICanonStoreRepository store,
        IReadOnlyList<IHarnessAdapter>? adapters = null)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(store);

        if (spans.Count == 0)
        {
            return new IngestOutcome(0, 0, 0);
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

        for (int index = 0; index < rawSpans.Count; index++)
        {
            RawSpan raw = rawSpans[index];
            OtlpSpan otlpSpan = spans[index];
            IReadOnlyList<string> namespaces = ObservedNamespaces(raw.Attributes);

            if (opencodeVersionQuarantined.Contains(index))
            {
                store.QuarantineAndDelete(new QuarantineRow(
                    SpanId: raw.SpanId,
                    ObservedNamespaces: namespaces,
                    Reason: CanonAdapters.UnknownOpencodeContractVersionReason,
                    Source: raw.Source,
                    Name: raw.Name,
                    Timestamp: otlpSpan.Timestamp,
                    RawPayload: BoundedSpanPayload(otlpSpan)));
                quarantined++;
                continue;
            }

            if (droidVersionQuarantined.Contains(index))
            {
                store.QuarantineAndDelete(new QuarantineRow(
                    SpanId: raw.SpanId,
                    ObservedNamespaces: namespaces,
                    Reason: CanonAdapters.UnknownDroidContractVersionReason,
                    Source: raw.Source,
                    Name: raw.Name,
                    Timestamp: otlpSpan.Timestamp,
                    RawPayload: BoundedSpanPayload(otlpSpan)));
                quarantined++;
                continue;
            }

            if (!attributed.TryGetValue(raw.SpanId, out string? harness))
            {
                store.QuarantineAndDelete(new QuarantineRow(
                    SpanId: raw.SpanId,
                    ObservedNamespaces: namespaces,
                    Reason: IsStructuralSpan(raw) ? "non-model span" : "unclaimed",
                    Source: raw.Source,
                    Name: raw.Name,
                    Timestamp: otlpSpan.Timestamp,
                    RawPayload: BoundedSpanPayload(otlpSpan)));
                quarantined++;
                continue;
            }

            if (string.Equals(harness, "droid", StringComparison.Ordinal) && !CanonAdapters.IsDroidEnvelopeSpan(raw))
            {
                store.QuarantineAndDelete(new QuarantineRow(
                    SpanId: raw.SpanId,
                    ObservedNamespaces: namespaces,
                    Reason: IsStructuralSpan(raw) ? "non-model span" : "unclaimed",
                    Source: raw.Source,
                    Name: raw.Name,
                    Timestamp: otlpSpan.Timestamp,
                    RawPayload: BoundedSpanPayload(otlpSpan)));
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
            catch (Exception exception)
            {
                rejected++;
                store.RecordProblem(new SpanProblemRow(
                    SpanId: raw.SpanId,
                    Severity: CanonModelWireStrings.ProblemWireString(ProblemSeverity.Error),
                    Code: "ADAPTER_NORMALIZE_FAILED",
                    Message: exception.Message,
                    Stack: exception.StackTrace,
                    Location: raw.SpanId,
                    SessionId: CanonAdapters.CanonicalSessionId(raw.Attributes),
                    Harness: harness,
                    Timestamp: otlpSpan.Timestamp));
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
                store.QuarantineAndDelete(new QuarantineRow(
                    SpanId: raw.SpanId,
                    ObservedNamespaces: namespaces,
                    Reason: "non-model span",
                    Source: raw.Source,
                    Name: raw.Name,
                    Timestamp: otlpSpan.Timestamp,
                    RawPayload: BoundedSpanPayload(otlpSpan),
                    Harness: harness));
                quarantined++;
                continue;
            }

            Problem? problem = adapter.Validate(timed);
            if (problem is not null)
            {
                rejected++;
                store.RecordProblem(new SpanProblemRow(
                    SpanId: timed.SpanId,
                    Severity: CanonModelWireStrings.ProblemWireString(problem.Severity),
                    Code: problem.Code,
                    Message: problem.Message,
                    Stack: problem.Stack,
                    Location: problem.Location ?? timed.SpanId,
                    SessionId: timed.SessionId,
                    Harness: timed.Harness,
                    Timestamp: timed.Timestamp));
                continue;
            }

            accepted.Add(timed);
        }

        if (accepted.Count > 0)
        {
            store.PutRecords(accepted);
        }

        return new IngestOutcome(accepted.Count, quarantined, rejected);
    }

    private static string BoundedSpanPayload(OtlpSpan span)
    {
        string json = JsonSerializer.Serialize(span, RawSpanJsonOptions);
        return CanonValidation.BoundRawPayload(json);
    }
}

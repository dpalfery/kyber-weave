using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using KyberDash.Core.Exceptions;

namespace KyberDash.Domain;

/// <summary>Normalized OTLP status block carried by a decoded span.</summary>
/// <param name="Code">Normalized lowercase status code (<c>"unset"</c>, <c>"ok"</c>, or <c>"error"</c>).</param>
/// <param name="Message">Optional status message or description.</param>
public record OtlpStatus(
    [property: System.Text.Json.Serialization.JsonPropertyName("code")] string Code,
    [property: System.Text.Json.Serialization.JsonPropertyName("message"),
     System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Message = null);

/// <summary>Instrumentation scope carried by decoded spans, logs, or metrics.</summary>
/// <param name="Name">Scope name.</param>
/// <param name="Version">Scope version.</param>
public record OtlpScope(
    [property: System.Text.Json.Serialization.JsonPropertyName("name"),
     System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Name = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("version"),
     System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Version = null);

/// <summary>Decoded OTLP trace span prior to harness attribution or canonical normalization.</summary>
public record OtlpSpan(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Kind,
    string StartTimeUnixNano,
    string EndTimeUnixNano,
    string Timestamp,
    double DurationMs,
    OtlpStatus Status,
    IReadOnlyDictionary<string, JsonElement> Attributes,
    IReadOnlyDictionary<string, JsonElement> Resource,
    OtlpScope Scope);

/// <summary>Decoded OTLP log record with deterministic <see cref="LogId"/> correlation.</summary>
public record OtlpLog(
    string LogId,
    string? TraceId,
    string? SpanId,
    string? SessionId,
    string Timestamp,
    string? Body,
    IReadOnlyDictionary<string, JsonElement> Attributes,
    IReadOnlyDictionary<string, JsonElement> Resource,
    OtlpScope Scope);

/// <summary>Decoded OTLP metric data point preserving absent values as <see langword="null"/>.</summary>
public record OtlpMetricDataPoint(
    IReadOnlyDictionary<string, JsonElement> Attributes,
    string? TimeUnixNano,
    string? StartTimeUnixNano,
    double? Value,
    double? Count,
    double? Sum);

/// <summary>Decoded OTLP metric carrying its resource, scope, and data points.</summary>
public record OtlpMetric(
    string Name,
    string? Description,
    string? Unit,
    string Kind,
    int? AggregationTemporality,
    bool? IsMonotonic,
    IReadOnlyList<OtlpMetricDataPoint> DataPoints,
    IReadOnlyDictionary<string, JsonElement> Resource,
    OtlpScope Scope);

/// <summary>
/// OTLP/HTTP constants, account-identity attribute stripping, deterministic log identity
/// derivation, and streaming <see cref="Utf8JsonReader"/> decoders for traces, logs, and metrics
/// (<c>dash/src/otel/receiver.ts</c>, <c>log-id.ts</c>, <c>identity.ts</c>).
/// </summary>
public static partial class OtlpCollector
{
    /// <summary>Standard OTLP/HTTP receiver port.</summary>
    public const int DefaultOtlpPort = 4318;

    /// <summary>Maximum request payload accepted on OTLP/HTTP endpoints (64 MiB).</summary>
    public const int DefaultMaxBodyBytes = 64 * 1024 * 1024;

    /// <summary>OTLP/HTTP trace export path.</summary>
    public const string TracesPath = "/v1/traces";

    /// <summary>OTLP/HTTP log export path.</summary>
    public const string LogsPath = "/v1/logs";

    /// <summary>OTLP/HTTP metric export path.</summary>
    public const string MetricsPath = "/v1/metrics";

    /// <summary>Liveness probe path.</summary>
    public const string HealthzPath = "/healthz";

    private static readonly string[] SpanKindNames = ["unspecified", "internal", "server", "client", "producer", "consumer"];
    private static readonly string[] StatusCodeNames = ["unset", "ok", "error"];
    private static readonly string[] MetricShapes = ["gauge", "sum", "histogram", "exponentialHistogram", "summary"];

    private static readonly HashSet<string> RetainedIdentityExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "session.id",
        "conversation.id",
    };

    private static readonly HashSet<string> IdentityPrincipals = new(StringComparer.OrdinalIgnoreCase)
    {
        "user",
        "users",
        "enduser",
        "endusers",
        "org",
        "orgs",
        "organization",
        "organizations",
        "account",
        "accounts",
    };

    private static readonly HashSet<string> IdentitySuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "uuid",
        "guid",
    };

    private static readonly JsonSerializerOptions DigestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"[._-]+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SeparatorSplitRegex();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CamelBoundarySplitRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]+$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex HexOnlyRegex();

    [GeneratedRegex(@"^-?\d+$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SignedIntegerRegex();

    /// <summary>
    /// Returns whether <paramref name="key"/> names an account-identity attribute that must be
    /// stripped before persistence (<c>dash/src/otel/identity.ts</c>).
    /// </summary>
    public static bool IsIdentityAttribute(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.StartsWith("gen_ai.", StringComparison.OrdinalIgnoreCase))
            return false;
        if (RetainedIdentityExceptions.Contains(key))
            return false;

        List<string> tokens = [];
        foreach (string part in SeparatorSplitRegex().Split(key))
        {
            if (part.Length == 0)
                continue;

            foreach (string sub in CamelBoundarySplitRegex().Split(part))
            {
                if (sub.Length > 0)
                    tokens.Add(sub);
            }
        }

        if (tokens.Count == 0)
            return false;

        string last = tokens[^1];
        if (string.Equals(last, "email", StringComparison.OrdinalIgnoreCase))
            return true;

        string compact = SeparatorSplitRegex().Replace(key, string.Empty);
        if (string.Equals(compact, "email", StringComparison.OrdinalIgnoreCase)
            || compact.EndsWith("email", StringComparison.OrdinalIgnoreCase))
        {
            string head = compact[..^"email".Length];
            if (head.Length == 0 || IdentityPrincipals.Any(principal => head.EndsWith(principal, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        if (compact.EndsWith("accountid", StringComparison.OrdinalIgnoreCase)
            || compact.EndsWith("accountuuid", StringComparison.OrdinalIgnoreCase)
            || compact.EndsWith("accountguid", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IdentitySuffixes.Contains(last) && tokens.Count >= 2)
            return IdentityPrincipals.Contains(tokens[^2]);

        return false;
    }

    /// <summary>
    /// Returns a copy of <paramref name="attributes"/> with account-identity keys removed.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> StripIdentityAttributes(
        IReadOnlyDictionary<string, JsonElement> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        Dictionary<string, JsonElement> stripped = new(attributes.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> entry in attributes)
        {
            if (!IsIdentityAttribute(entry.Key))
                stripped[entry.Key] = entry.Value.Clone();
        }

        return stripped;
    }

    /// <summary>
    /// Derives a globally unique deterministic log identifier matching <c>deriveLogId</c> in
    /// <c>dash/src/otel/log-id.ts</c>.
    /// </summary>
    public static string DeriveLogId(
        string? traceId,
        string? spanId,
        string? sessionId,
        string timeUnixNano,
        JsonElement? body,
        IReadOnlyDictionary<string, JsonElement> attributes,
        string? eventName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeUnixNano);
        ArgumentNullException.ThrowIfNull(attributes);

        SortedDictionary<string, JsonElement> sorted = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> entry in attributes)
            sorted[entry.Key] = entry.Value;

        string canonicalPayload = JsonSerializer.Serialize(
            new
            {
                @event = eventName ?? string.Empty,
                body = body.HasValue && body.Value.ValueKind != JsonValueKind.Undefined ? body.Value : (JsonElement?)null,
                attributes = sorted,
            },
            DigestJsonOptions);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));
        string digest = Convert.ToHexStringLower(hash)[..16];
        string prefix = traceId ?? sessionId ?? "log";
        string spanPart = spanId ?? "none";
        return $"{prefix}:{spanPart}:{timeUnixNano}:{digest}";
    }

    /// <summary>
    /// Decodes an OTLP/JSON <c>ExportTraceServiceRequest</c> body using <see cref="Utf8JsonReader"/>,
    /// enforcing <paramref name="maxBodyBytes"/> (64 MiB default).
    /// </summary>
    public static IReadOnlyList<OtlpSpan> DecodeTracesJson(
        ReadOnlySpan<byte> utf8Json,
        int maxBodyBytes = DefaultMaxBodyBytes)
    {
        JsonElement root = ParseRootObject(utf8Json, maxBodyBytes);
        if (!root.TryGetProperty("resourceSpans", out JsonElement resourceSpans)
            || resourceSpans.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (resourceSpans.ValueKind != JsonValueKind.Array)
            throw new OtlpDecodeException("resourceSpans must be an array");

        List<OtlpSpan> spans = [];
        int rsIndex = 0;
        foreach (JsonElement rsRaw in resourceSpans.EnumerateArray())
        {
            string rsWhere = $"resourceSpans[{rsIndex.ToString(CultureInfo.InvariantCulture)}]";
            ExpectObject(rsRaw, rsWhere);
            IReadOnlyDictionary<string, JsonElement> resource = DecodeResource(rsRaw, rsWhere);

            if (rsRaw.TryGetProperty("scopeSpans", out JsonElement scopeSpansRaw)
                && scopeSpansRaw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (scopeSpansRaw.ValueKind != JsonValueKind.Array)
                    throw new OtlpDecodeException($"{rsWhere}.scopeSpans must be an array");

                int ssIndex = 0;
                foreach (JsonElement ssRaw in scopeSpansRaw.EnumerateArray())
                {
                    string ssWhere = $"{rsWhere}.scopeSpans[{ssIndex.ToString(CultureInfo.InvariantCulture)}]";
                    ExpectObject(ssRaw, ssWhere);
                    OtlpScope scope = DecodeScope(ssRaw, $"{ssWhere}.scope");

                    if (ssRaw.TryGetProperty("spans", out JsonElement spansRaw)
                        && spansRaw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    {
                        if (spansRaw.ValueKind != JsonValueKind.Array)
                            throw new OtlpDecodeException($"{ssWhere}.spans must be an array");

                        int sIndex = 0;
                        foreach (JsonElement spanRaw in spansRaw.EnumerateArray())
                        {
                            string spanWhere = $"{ssWhere}.spans[{sIndex.ToString(CultureInfo.InvariantCulture)}]";
                            spans.Add(DecodeSpan(spanRaw, resource, scope, spanWhere));
                            sIndex++;
                        }
                    }

                    ssIndex++;
                }
            }

            rsIndex++;
        }

        return spans;
    }

    /// <summary>
    /// Decodes an OTLP/JSON <c>ExportLogsServiceRequest</c> body using <see cref="Utf8JsonReader"/>,
    /// enforcing <paramref name="maxBodyBytes"/> (64 MiB default).
    /// </summary>
    public static IReadOnlyList<OtlpLog> DecodeLogsJson(
        ReadOnlySpan<byte> utf8Json,
        int maxBodyBytes = DefaultMaxBodyBytes)
    {
        JsonElement root = ParseRootObject(utf8Json, maxBodyBytes);
        if (!root.TryGetProperty("resourceLogs", out JsonElement resourceLogs)
            || resourceLogs.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (resourceLogs.ValueKind != JsonValueKind.Array)
            throw new OtlpDecodeException("resourceLogs must be an array");

        List<OtlpLog> logs = [];
        int rlIndex = 0;
        foreach (JsonElement rlRaw in resourceLogs.EnumerateArray())
        {
            string rlWhere = $"resourceLogs[{rlIndex.ToString(CultureInfo.InvariantCulture)}]";
            ExpectObject(rlRaw, rlWhere);
            IReadOnlyDictionary<string, JsonElement> resource = DecodeResource(rlRaw, rlWhere);

            if (rlRaw.TryGetProperty("scopeLogs", out JsonElement scopeLogsRaw)
                && scopeLogsRaw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (scopeLogsRaw.ValueKind != JsonValueKind.Array)
                    throw new OtlpDecodeException($"{rlWhere}.scopeLogs must be an array");

                int slIndex = 0;
                foreach (JsonElement slRaw in scopeLogsRaw.EnumerateArray())
                {
                    string slWhere = $"{rlWhere}.scopeLogs[{slIndex.ToString(CultureInfo.InvariantCulture)}]";
                    ExpectObject(slRaw, slWhere);
                    OtlpScope scope = DecodeScope(slRaw, $"{slWhere}.scope");

                    if (slRaw.TryGetProperty("logRecords", out JsonElement recordsRaw)
                        && recordsRaw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    {
                        if (recordsRaw.ValueKind != JsonValueKind.Array)
                            throw new OtlpDecodeException($"{slWhere}.logRecords must be an array");

                        int lrIndex = 0;
                        foreach (JsonElement recordRaw in recordsRaw.EnumerateArray())
                        {
                            string recWhere = $"{slWhere}.logRecords[{lrIndex.ToString(CultureInfo.InvariantCulture)}]";
                            logs.Add(DecodeLogRecord(recordRaw, resource, scope, recWhere));
                            lrIndex++;
                        }
                    }

                    slIndex++;
                }
            }

            rlIndex++;
        }

        return logs;
    }

    /// <summary>
    /// Decodes an OTLP/JSON <c>ExportMetricsServiceRequest</c> body using <see cref="Utf8JsonReader"/>,
    /// enforcing <paramref name="maxBodyBytes"/> (64 MiB default).
    /// </summary>
    public static IReadOnlyList<OtlpMetric> DecodeMetricsJson(
        ReadOnlySpan<byte> utf8Json,
        int maxBodyBytes = DefaultMaxBodyBytes)
    {
        JsonElement root = ParseRootObject(utf8Json, maxBodyBytes);
        if (!root.TryGetProperty("resourceMetrics", out JsonElement resourceMetrics)
            || resourceMetrics.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (resourceMetrics.ValueKind != JsonValueKind.Array)
            throw new OtlpDecodeException("resourceMetrics must be an array");

        List<OtlpMetric> metrics = [];
        int rmIndex = 0;
        foreach (JsonElement rmRaw in resourceMetrics.EnumerateArray())
        {
            string rmWhere = $"resourceMetrics[{rmIndex.ToString(CultureInfo.InvariantCulture)}]";
            ExpectObject(rmRaw, rmWhere);
            IReadOnlyDictionary<string, JsonElement> resource = DecodeResource(rmRaw, rmWhere);

            if (rmRaw.TryGetProperty("scopeMetrics", out JsonElement scopeMetricsRaw)
                && scopeMetricsRaw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (scopeMetricsRaw.ValueKind != JsonValueKind.Array)
                    throw new OtlpDecodeException($"{rmWhere}.scopeMetrics must be an array");

                int smIndex = 0;
                foreach (JsonElement smRaw in scopeMetricsRaw.EnumerateArray())
                {
                    string smWhere = $"{rmWhere}.scopeMetrics[{smIndex.ToString(CultureInfo.InvariantCulture)}]";
                    ExpectObject(smRaw, smWhere);
                    OtlpScope scope = DecodeScope(smRaw, $"{smWhere}.scope");

                    if (smRaw.TryGetProperty("metrics", out JsonElement metricsRaw)
                        && metricsRaw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    {
                        if (metricsRaw.ValueKind != JsonValueKind.Array)
                            throw new OtlpDecodeException($"{smWhere}.metrics must be an array");

                        int mIndex = 0;
                        foreach (JsonElement metricRaw in metricsRaw.EnumerateArray())
                        {
                            string metricWhere = $"{smWhere}.metrics[{mIndex.ToString(CultureInfo.InvariantCulture)}]";
                            metrics.Add(DecodeMetric(metricRaw, resource, scope, metricWhere));
                            mIndex++;
                        }
                    }

                    smIndex++;
                }
            }

            rmIndex++;
        }

        return metrics;
    }

    private static JsonElement ParseRootObject(ReadOnlySpan<byte> utf8Json, int maxBodyBytes)
    {
        if (utf8Json.Length > maxBodyBytes)
        {
            throw new OtlpPayloadTooLargeException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"OTLP request body of {utf8Json.Length} bytes exceeds max body limit of {maxBodyBytes} bytes"));
        }

        try
        {
            Utf8JsonReader reader = new(utf8Json);
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            JsonElement root = doc.RootElement.Clone();
            ExpectObject(root, "OTLP payload");
            return root;
        }
        catch (JsonException exception)
        {
            throw new OtlpDecodeException($"OTLP JSON body does not parse: {exception.Message}", exception);
        }
    }

    private static void ExpectObject(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new OtlpDecodeException($"{where} must be a JSON object");
    }

    private static string RequiredString(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new OtlpDecodeException($"{where} must be a non-empty string");

        string? value = element.GetString();
        if (string.IsNullOrEmpty(value))
            throw new OtlpDecodeException($"{where} must be a non-empty string");

        return value;
    }

    private static IReadOnlyDictionary<string, JsonElement> DecodeResource(JsonElement parent, string parentWhere)
    {
        if (!parent.TryGetProperty("resource", out JsonElement resourceRaw)
            || resourceRaw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        ExpectObject(resourceRaw, $"{parentWhere}.resource");
        if (!resourceRaw.TryGetProperty("attributes", out JsonElement attrsRaw))
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        return StripIdentityAttributes(AttributesFromJson(attrsRaw, $"{parentWhere}.resource.attributes"));
    }

    private static OtlpScope DecodeScope(JsonElement parent, string where)
    {
        if (!parent.TryGetProperty("scope", out JsonElement scopeRaw)
            || scopeRaw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new OtlpScope();
        }

        ExpectObject(scopeRaw, where);
        string? name = scopeRaw.TryGetProperty("name", out JsonElement nameEl)
            && nameEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? RequiredString(nameEl, $"{where}.name")
                : null;
        string? version = scopeRaw.TryGetProperty("version", out JsonElement versionEl)
            && versionEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? RequiredString(versionEl, $"{where}.version")
                : null;
        return new OtlpScope(name, version);
    }

    private static OtlpSpan DecodeSpan(
        JsonElement spanRaw,
        IReadOnlyDictionary<string, JsonElement> resource,
        OtlpScope scope,
        string where)
    {
        ExpectObject(spanRaw, where);
        BigInteger start = NanosToBigInt(GetPropertyOrThrow(spanRaw, "startTimeUnixNano", $"{where}.startTimeUnixNano"), $"{where}.startTimeUnixNano");
        BigInteger end = NanosToBigInt(GetPropertyOrThrow(spanRaw, "endTimeUnixNano", $"{where}.endTimeUnixNano"), $"{where}.endTimeUnixNano");

        string? parentSpanId = null;
        if (spanRaw.TryGetProperty("parentSpanId", out JsonElement parentEl)
            && parentEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(parentEl.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(parentEl.GetString())))
        {
            parentSpanId = IdToHex(parentEl, 8, $"{where}.parentSpanId");
        }

        string traceId = IdToHex(GetPropertyOrThrow(spanRaw, "traceId", $"{where}.traceId"), 16, $"{where}.traceId");
        string spanId = IdToHex(GetPropertyOrThrow(spanRaw, "spanId", $"{where}.spanId"), 8, $"{where}.spanId");
        string name = RequiredString(GetPropertyOrThrow(spanRaw, "name", $"{where}.name"), $"{where}.name");
        string kind = spanRaw.TryGetProperty("kind", out JsonElement kindEl)
            ? EnumName(kindEl, SpanKindNames, "span_kind_")
            : SpanKindNames[0];
        OtlpStatus status = spanRaw.TryGetProperty("status", out JsonElement statusEl)
            ? StatusFromJson(statusEl, $"{where}.status")
            : new OtlpStatus(StatusCodeNames[0]);

        Dictionary<string, JsonElement> attrs = spanRaw.TryGetProperty("attributes", out JsonElement attrsEl)
            ? AttributesFromJson(attrsEl, $"{where}.attributes")
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        return new OtlpSpan(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            Name: name,
            Kind: kind,
            StartTimeUnixNano: start.ToString(CultureInfo.InvariantCulture),
            EndTimeUnixNano: end.ToString(CultureInfo.InvariantCulture),
            Timestamp: NanosToIsoTimestamp(start),
            DurationMs: (double)(end - start) / 1_000_000.0,
            Status: status,
            Attributes: StripIdentityAttributes(attrs),
            Resource: resource,
            Scope: scope);
    }

    private static OtlpLog DecodeLogRecord(
        JsonElement recordRaw,
        IReadOnlyDictionary<string, JsonElement> resource,
        OtlpScope scope,
        string where)
    {
        ExpectObject(recordRaw, where);
        BigInteger time = NanosToBigInt(
            GetPropertyOrThrow(recordRaw, "timeUnixNano", $"{where}.timeUnixNano"),
            $"{where}.timeUnixNano");

        Dictionary<string, JsonElement> rawAttrs = recordRaw.TryGetProperty("attributes", out JsonElement attrsEl)
            ? AttributesFromJson(attrsEl, $"{where}.attributes")
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, JsonElement> attributes = StripIdentityAttributes(rawAttrs);
        string? sessionId = SessionIdFromAttributes(attributes);

        string? traceId = recordRaw.TryGetProperty("traceId", out JsonElement traceEl)
            && traceEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(traceEl.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(traceEl.GetString()))
                ? IdToHex(traceEl, 16, $"{where}.traceId")
                : null;

        string? spanId = recordRaw.TryGetProperty("spanId", out JsonElement spanEl)
            && spanEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(spanEl.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(spanEl.GetString()))
                ? IdToHex(spanEl, 8, $"{where}.spanId")
                : null;

        JsonElement? resolvedBodyEl = null;
        string? bodyText = null;
        if (recordRaw.TryGetProperty("body", out JsonElement bodyRaw)
            && bodyRaw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            JsonElement unwrapped = AnyValueFromJson(bodyRaw, $"{where}.body");
            if (unwrapped.ValueKind != JsonValueKind.Null)
            {
                resolvedBodyEl = unwrapped;
                bodyText = unwrapped.ValueKind == JsonValueKind.String
                    ? unwrapped.GetString()
                    : unwrapped.GetRawText();
            }
        }

        string? eventName = recordRaw.TryGetProperty("eventName", out JsonElement evEl)
            && evEl.ValueKind == JsonValueKind.String
                ? evEl.GetString()
                : null;

        string timeText = time.ToString(CultureInfo.InvariantCulture);
        string logId = DeriveLogId(traceId, spanId, sessionId, timeText, resolvedBodyEl, attributes, eventName);

        return new OtlpLog(
            LogId: logId,
            TraceId: traceId,
            SpanId: spanId,
            SessionId: sessionId,
            Timestamp: NanosToIsoTimestamp(time),
            Body: bodyText,
            Attributes: attributes,
            Resource: resource,
            Scope: scope);
    }

    private static OtlpMetric DecodeMetric(
        JsonElement metricRaw,
        IReadOnlyDictionary<string, JsonElement> resource,
        OtlpScope scope,
        string where)
    {
        ExpectObject(metricRaw, where);
        string name = RequiredString(GetPropertyOrThrow(metricRaw, "name", $"{where}.name"), $"{where}.name");
        string? description = metricRaw.TryGetProperty("description", out JsonElement descEl) && descEl.ValueKind == JsonValueKind.String
            ? descEl.GetString()
            : null;
        string? unit = metricRaw.TryGetProperty("unit", out JsonElement unitEl) && unitEl.ValueKind == JsonValueKind.String
            ? unitEl.GetString()
            : null;

        string? shape = null;
        JsonElement shapeData = default;
        foreach (string candidate in MetricShapes)
        {
            if (metricRaw.TryGetProperty(candidate, out JsonElement el)
                && el.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                shape = candidate;
                shapeData = el;
                break;
            }
        }

        if (shape is null)
            return new OtlpMetric(name, description, unit, "unknown", null, null, [], resource, scope);

        ExpectObject(shapeData, $"{where}.{shape}");
        List<OtlpMetricDataPoint> dataPoints = [];
        if (shapeData.TryGetProperty("dataPoints", out JsonElement pointsEl)
            && pointsEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (pointsEl.ValueKind != JsonValueKind.Array)
                throw new OtlpDecodeException($"{where}.{shape}.dataPoints must be an array");

            int index = 0;
            foreach (JsonElement ptRaw in pointsEl.EnumerateArray())
            {
                string ptWhere = $"{where}.{shape}.dataPoints[{index.ToString(CultureInfo.InvariantCulture)}]";
                dataPoints.Add(DecodeDataPoint(ptRaw, ptWhere));
                index++;
            }
        }

        int? aggregationTemporality = shapeData.TryGetProperty("aggregationTemporality", out JsonElement tempEl)
            && tempEl.ValueKind == JsonValueKind.Number
            && tempEl.TryGetInt32(out int temp)
                ? temp
                : null;

        bool? isMonotonic = shapeData.TryGetProperty("isMonotonic", out JsonElement monoEl)
            && monoEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? monoEl.GetBoolean()
                : null;

        return new OtlpMetric(name, description, unit, shape, aggregationTemporality, isMonotonic, dataPoints, resource, scope);
    }

    private static OtlpMetricDataPoint DecodeDataPoint(JsonElement ptRaw, string where)
    {
        ExpectObject(ptRaw, where);
        IReadOnlyDictionary<string, JsonElement> attrs = ptRaw.TryGetProperty("attributes", out JsonElement attrsEl)
            ? StripIdentityAttributes(AttributesFromJson(attrsEl, $"{where}.attributes"))
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        string? timeUnixNano = ptRaw.TryGetProperty("timeUnixNano", out JsonElement timeEl)
            && timeEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? NanosToBigInt(timeEl, $"{where}.timeUnixNano").ToString(CultureInfo.InvariantCulture)
                : null;

        string? startUnixNano = ptRaw.TryGetProperty("startTimeUnixNano", out JsonElement startEl)
            && startEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? NanosToBigInt(startEl, $"{where}.startTimeUnixNano").ToString(CultureInfo.InvariantCulture)
                : null;

        double? value = (ptRaw.TryGetProperty("asInt", out JsonElement intEl) ? ReadNumeric(intEl) : null)
            ?? (ptRaw.TryGetProperty("asDouble", out JsonElement dblEl) ? ReadNumeric(dblEl) : null);
        double? count = ptRaw.TryGetProperty("count", out JsonElement countEl) ? ReadNumeric(countEl) : null;
        double? sum = ptRaw.TryGetProperty("sum", out JsonElement sumEl) ? ReadNumeric(sumEl) : null;

        return new OtlpMetricDataPoint(attrs, timeUnixNano, startUnixNano, value, count, sum);
    }

    private static double? ReadNumeric(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double d) && double.IsFinite(d))
            return d;

        if (element.ValueKind == JsonValueKind.String)
        {
            string? text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && double.IsFinite(parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? SessionIdFromAttributes(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        if (attributes.TryGetValue("session_id", out JsonElement underscore)
            && underscore.ValueKind == JsonValueKind.String)
        {
            string? val = underscore.GetString();
            if (!string.IsNullOrEmpty(val))
                return val;
        }

        if (attributes.TryGetValue("session.id", out JsonElement dotted)
            && dotted.ValueKind == JsonValueKind.String)
        {
            string? val = dotted.GetString();
            if (!string.IsNullOrEmpty(val))
                return val;
        }

        return null;
    }

    private static JsonElement GetPropertyOrThrow(JsonElement obj, string name, string where)
    {
        if (!obj.TryGetProperty(name, out JsonElement element))
            throw new OtlpDecodeException($"{where} is required");

        return element;
    }

    private static OtlpStatus StatusFromJson(JsonElement value, string where)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new OtlpStatus(StatusCodeNames[0]);

        ExpectObject(value, where);
        string code = value.TryGetProperty("code", out JsonElement codeEl)
            ? EnumName(codeEl, StatusCodeNames, "status_code_")
            : StatusCodeNames[0];

        JsonElement messageEl = default;
        bool hasText = (value.TryGetProperty("message", out messageEl)
            && messageEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(messageEl.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(messageEl.GetString())))
            || (value.TryGetProperty("description", out messageEl)
                && messageEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !(messageEl.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(messageEl.GetString())));

        string? message = hasText ? RequiredString(messageEl, $"{where}.message") : null;
        return new OtlpStatus(code, message);
    }

    private static string EnumName(JsonElement value, IReadOnlyList<string> names, string prefix)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int index) && index >= 0 && index < names.Count)
            return names[index];

        if (value.ValueKind == JsonValueKind.String)
        {
            string normalized = value.GetString() ?? string.Empty;
            string stripped = normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? normalized[prefix.Length..]
                : normalized;

            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], stripped, StringComparison.OrdinalIgnoreCase))
                    return names[i];
            }
        }

        return names[0];
    }

    private static string IdToHex(JsonElement value, int byteLength, string where)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new OtlpDecodeException($"{where} is required: expected a {byteLength.ToString(CultureInfo.InvariantCulture)}-byte id as hex or base64");

        string? raw = value.GetString();
        if (string.IsNullOrEmpty(raw))
            throw new OtlpDecodeException($"{where} is required: expected a {byteLength.ToString(CultureInfo.InvariantCulture)}-byte id as hex or base64");

        if (raw.Length == byteLength * 2 && HexOnlyRegex().IsMatch(raw))
            return Convert.ToHexStringLower(Convert.FromHexString(raw));

        byte[] decoded = Base64ToBytes(raw, where);
        if (decoded.Length != byteLength)
        {
            throw new OtlpDecodeException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where} decoded to {decoded.Length} bytes, expected {byteLength}"));
        }

        return Convert.ToHexStringLower(decoded);
    }

    private static byte[] Base64ToBytes(string value, string where)
    {
        return TryBase64ToBytes(value, out byte[] bytes)
            ? bytes
            : throw new OtlpDecodeException($"{where} is neither hex nor base64");
    }

    private static bool TryBase64ToBytes(string value, out byte[] bytes)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        int mod = normalized.Length % 4;
        if (mod != 0)
            normalized += new string('=', 4 - mod);

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static BigInteger NanosToBigInt(JsonElement value, string where)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out long longVal))
                return new BigInteger(longVal);

            if (value.TryGetDouble(out double dblVal) && double.IsFinite(dblVal))
                return new BigInteger(Math.Truncate(dblVal));
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (text is not null && SignedIntegerRegex().IsMatch(text)
                && BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger big))
            {
                return big;
            }
        }

        throw new OtlpDecodeException($"{where} must be an integer nanosecond count (proto3 JSON encodes int64 as a string)");
    }

    private static string NanosToIsoTimestamp(BigInteger nanos)
    {
        BigInteger millis = nanos / 1_000_000;
        if (millis < long.MinValue || millis > long.MaxValue)
        {
            throw new OtlpDecodeException(
                "a nanosecond timestamp is outside the range a 64-bit millisecond count can hold");
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)millis)
                .UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new OtlpDecodeException(
                "a nanosecond timestamp is outside the range a calendar instant can represent", exception);
        }
    }

    private static Dictionary<string, JsonElement> AttributesFromJson(JsonElement raw, string where)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return result;

        if (raw.ValueKind != JsonValueKind.Array)
            throw new OtlpDecodeException($"{where} must be an array of key/value pairs");

        int index = 0;
        foreach (JsonElement entryRaw in raw.EnumerateArray())
        {
            string entryWhere = $"{where}[{index.ToString(CultureInfo.InvariantCulture)}]";
            ExpectObject(entryRaw, entryWhere);
            string key = RequiredString(GetPropertyOrThrow(entryRaw, "key", $"{entryWhere}.key"), $"{entryWhere}.key");
            JsonElement val = entryRaw.TryGetProperty("value", out JsonElement valEl)
                ? AnyValueFromJson(valEl, $"{entryWhere}.value")
                : JsonSerializer.SerializeToElement<object?>(null);
            result[key] = val;
            index++;
        }

        return result;
    }

    private static JsonElement AnyValueFromJson(JsonElement value, string where)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return JsonSerializer.SerializeToElement<object?>(null);

        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return value.Clone();

        ExpectObject(value, where);

        if (value.TryGetProperty("stringValue", out JsonElement strEl))
        {
            if (strEl.ValueKind != JsonValueKind.String)
                throw new OtlpDecodeException($"{where}.stringValue must be a string");

            return strEl.Clone();
        }

        if (value.TryGetProperty("boolValue", out JsonElement boolEl))
        {
            if (boolEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new OtlpDecodeException($"{where}.boolValue must be a boolean");

            return boolEl.Clone();
        }

        if (value.TryGetProperty("intValue", out JsonElement intEl))
        {
            BigInteger parsed = NanosToBigInt(intEl, $"{where}.intValue");
            if (parsed < long.MinValue || parsed > long.MaxValue)
                throw new OtlpDecodeException($"{where}.intValue is outside the signed 64-bit range");

            return JsonSerializer.SerializeToElement((long)parsed);
        }

        if (value.TryGetProperty("doubleValue", out JsonElement dblEl))
        {
            if (dblEl.ValueKind != JsonValueKind.Number)
                throw new OtlpDecodeException($"{where}.doubleValue must be a number");

            return dblEl.Clone();
        }

        if (value.TryGetProperty("arrayValue", out JsonElement arrObj))
        {
            ExpectObject(arrObj, $"{where}.arrayValue");
            if (!arrObj.TryGetProperty("values", out JsonElement valuesEl) || valuesEl.ValueKind != JsonValueKind.Array)
                throw new OtlpDecodeException($"{where}.arrayValue.values must be an array");

            List<JsonElement> items = [];
            int index = 0;
            foreach (JsonElement item in valuesEl.EnumerateArray())
            {
                items.Add(AnyValueFromJson(item, $"{where}.arrayValue.values[{index.ToString(CultureInfo.InvariantCulture)}]"));
                index++;
            }

            return JsonSerializer.SerializeToElement(items);
        }

        if (value.TryGetProperty("kvlistValue", out JsonElement kvObj))
        {
            ExpectObject(kvObj, $"{where}.kvlistValue");
            JsonElement kvValues = kvObj.TryGetProperty("values", out JsonElement valuesEl)
                ? valuesEl
                : JsonSerializer.SerializeToElement<object?>(null);
            Dictionary<string, JsonElement> map = AttributesFromJson(kvValues, $"{where}.kvlistValue.values");
            return JsonSerializer.SerializeToElement(map);
        }

        if (value.TryGetProperty("bytesValue", out JsonElement bytesEl))
        {
            if (bytesEl.ValueKind != JsonValueKind.String)
                throw new OtlpDecodeException($"{where}.bytesValue must be a base64 string");

            string rawBytes = bytesEl.GetString() ?? string.Empty;
            // The protobuf JSON mapping encodes bytesValue as base64. Decode it that way first, so
            // a base64 value that happens to use only hex digits is not read as hex bytes.
            if (TryBase64ToBytes(rawBytes, out byte[] decoded))
                return JsonSerializer.SerializeToElement(Convert.ToHexStringLower(decoded));

            if (rawBytes.Length % 2 == 0 && HexOnlyRegex().IsMatch(rawBytes))
                return JsonSerializer.SerializeToElement(Convert.ToHexStringLower(Convert.FromHexString(rawBytes)));

            throw new OtlpDecodeException($"{where}.bytesValue is neither base64 nor hex");
        }

        throw new OtlpDecodeException(
            $"{where} is an AnyValue with none of its oneof fields set " +
            "(expected stringValue, boolValue, intValue, doubleValue, arrayValue, kvlistValue or bytesValue)");
    }

    private const int MaxProtobufDepth = 32;

    /// <summary>
    /// Decodes an OTLP/Protobuf <c>ExportTraceServiceRequest</c> body into spans,
    /// enforcing <paramref name="maxBodyBytes"/> (64 MiB default).
    /// </summary>
    public static IReadOnlyList<OtlpSpan> DecodeTracesProtobuf(
        ReadOnlySpan<byte> body,
        int maxBodyBytes = DefaultMaxBodyBytes)
    {
        CheckBodyLimit(body.Length, maxBodyBytes);
        Dictionary<int, List<PbField>> request = ReadProtobufMessage(body, 0);
        List<OtlpSpan> spans = [];

        int rsIndex = 0;
        foreach (Dictionary<int, List<PbField>> resourceSpan in PbMessages(request, 1, "resource_spans", 0))
        {
            string rsWhere = $"resource_spans[{rsIndex.ToString(CultureInfo.InvariantCulture)}]";
            Dictionary<int, List<PbField>>? resourceMessage = PbMessage(resourceSpan, 1, 0);
            IReadOnlyDictionary<string, JsonElement> resource = resourceMessage is null
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : StripIdentityAttributes(PbKeyValues(resourceMessage, 1, 0));

            int ssIndex = 0;
            foreach (Dictionary<int, List<PbField>> scopeSpans in PbMessages(resourceSpan, 2, $"{rsWhere}.scope_spans", 0))
            {
                string ssWhere = $"{rsWhere}.scope_spans[{ssIndex.ToString(CultureInfo.InvariantCulture)}]";
                OtlpScope scope = PbScope(scopeSpans, 1);

                int sIndex = 0;
                foreach (Dictionary<int, List<PbField>> spanMsg in PbMessages(scopeSpans, 2, $"{ssWhere}.spans", 0))
                {
                    string spanWhere = $"{ssWhere}.spans[{sIndex.ToString(CultureInfo.InvariantCulture)}]";
                    spans.Add(SpanFromProtobuf(spanMsg, resource, scope, spanWhere));
                    sIndex++;
                }

                ssIndex++;
            }

            rsIndex++;
        }

        return spans;
    }

    /// <summary>
    /// Decodes an OTLP/Protobuf <c>ExportLogsServiceRequest</c> body into log records,
    /// enforcing <paramref name="maxBodyBytes"/> (64 MiB default).
    /// </summary>
    public static IReadOnlyList<OtlpLog> DecodeLogsProtobuf(
        ReadOnlySpan<byte> body,
        int maxBodyBytes = DefaultMaxBodyBytes)
    {
        CheckBodyLimit(body.Length, maxBodyBytes);
        Dictionary<int, List<PbField>> request = ReadProtobufMessage(body, 0);
        List<OtlpLog> logs = [];

        int rlIndex = 0;
        foreach (Dictionary<int, List<PbField>> resourceLogs in PbMessages(request, 1, "resource_logs", 0))
        {
            string rlWhere = $"resource_logs[{rlIndex.ToString(CultureInfo.InvariantCulture)}]";
            Dictionary<int, List<PbField>>? resourceMessage = PbMessage(resourceLogs, 1, 0);
            IReadOnlyDictionary<string, JsonElement> resource = resourceMessage is null
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : StripIdentityAttributes(PbKeyValues(resourceMessage, 1, 0));

            foreach (Dictionary<int, List<PbField>> scopeLogs in PbMessages(resourceLogs, 2, $"{rlWhere}.scope_logs", 0))
            {
                OtlpScope scope = PbScope(scopeLogs, 1);

                foreach (Dictionary<int, List<PbField>> logMsg in PbMessages(scopeLogs, 2, $"{rlWhere}.scope_logs.log_records", 0))
                {
                    ulong? time = PbUInt64(logMsg, 1);
                    if (!time.HasValue)
                        throw new OtlpDecodeException($"{rlWhere}.time_unix_nano is required");

                    byte[]? traceBytes = PbBytes(logMsg, 9);
                    byte[]? spanBytes = PbBytes(logMsg, 10);
                    string? traceId = traceBytes is null ? null : RequiredPbId(traceBytes, 16, $"{rlWhere}.trace_id");
                    string? spanId = spanBytes is null ? null : RequiredPbId(spanBytes, 8, $"{rlWhere}.span_id");
                    IReadOnlyDictionary<string, JsonElement> attributes = StripIdentityAttributes(PbKeyValues(logMsg, 6, 0));
                    string? sessionId = SessionIdFromAttributes(attributes);

                    Dictionary<int, List<PbField>>? bodyMessage = PbMessage(logMsg, 5, 0);
                    JsonElement? bodyElement = bodyMessage is null ? null : PbAnyValue(bodyMessage, 1);
                    string? bodyText = null;
                    if (bodyElement.HasValue && bodyElement.Value.ValueKind != JsonValueKind.Null)
                    {
                        bodyText = bodyElement.Value.ValueKind == JsonValueKind.String
                            ? bodyElement.Value.GetString()
                            : bodyElement.Value.GetRawText();
                    }

                    string? eventName = PbString(logMsg, 12);
                    string timeText = time.Value.ToString(CultureInfo.InvariantCulture);
                    string logId = DeriveLogId(traceId, spanId, sessionId, timeText, bodyElement, attributes, eventName);

                    logs.Add(new OtlpLog(
                        LogId: logId,
                        TraceId: traceId,
                        SpanId: spanId,
                        SessionId: sessionId,
                        Timestamp: NanosToIsoTimestamp(new BigInteger(time.Value)),
                        Body: bodyText,
                        Attributes: attributes,
                        Resource: resource,
                        Scope: scope));
                }
            }

            rlIndex++;
        }

        return logs;
    }

    /// <summary>
    /// Decodes an OTLP/Protobuf <c>ExportMetricsServiceRequest</c> body into metrics,
    /// enforcing <paramref name="maxBodyBytes"/> (64 MiB default).
    /// </summary>
    public static IReadOnlyList<OtlpMetric> DecodeMetricsProtobuf(
        ReadOnlySpan<byte> body,
        int maxBodyBytes = DefaultMaxBodyBytes)
    {
        CheckBodyLimit(body.Length, maxBodyBytes);
        Dictionary<int, List<PbField>> request = ReadProtobufMessage(body, 0);
        List<OtlpMetric> metrics = [];

        int rmIndex = 0;
        foreach (Dictionary<int, List<PbField>> resourceMetric in PbMessages(request, 1, "resource_metrics", 0))
        {
            string rmWhere = $"resource_metrics[{rmIndex.ToString(CultureInfo.InvariantCulture)}]";
            Dictionary<int, List<PbField>>? resourceMessage = PbMessage(resourceMetric, 1, 0);
            IReadOnlyDictionary<string, JsonElement> resource = resourceMessage is null
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : StripIdentityAttributes(PbKeyValues(resourceMessage, 1, 0));

            int smIndex = 0;
            foreach (Dictionary<int, List<PbField>> scopeMetric in PbMessages(resourceMetric, 2, $"{rmWhere}.scope_metrics", 0))
            {
                string smWhere = $"{rmWhere}.scope_metrics[{smIndex.ToString(CultureInfo.InvariantCulture)}]";
                OtlpScope scope = PbScope(scopeMetric, 1);

                int mIndex = 0;
                foreach (Dictionary<int, List<PbField>> metricMsg in PbMessages(scopeMetric, 2, $"{smWhere}.metrics", 0))
                {
                    string mWhere = $"{smWhere}.metrics[{mIndex.ToString(CultureInfo.InvariantCulture)}]";
                    metrics.Add(MetricFromProtobuf(metricMsg, resource, scope, mWhere));
                    mIndex++;
                }

                smIndex++;
            }

            rmIndex++;
        }

        return metrics;
    }

    private static void CheckBodyLimit(int length, int maxBodyBytes)
    {
        if (length > maxBodyBytes)
        {
            throw new OtlpPayloadTooLargeException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"OTLP request body of {length} bytes exceeds max body limit of {maxBodyBytes} bytes"));
        }
    }

    private static OtlpScope PbScope(Dictionary<int, List<PbField>> parent, int fieldNumber)
    {
        Dictionary<int, List<PbField>>? scopeMessage = PbMessage(parent, fieldNumber, 0);
        if (scopeMessage is null)
            return new OtlpScope();

        string? name = PbString(scopeMessage, 1);
        string? version = PbString(scopeMessage, 2);
        return new OtlpScope(
            string.IsNullOrEmpty(name) ? null : name,
            string.IsNullOrEmpty(version) ? null : version);
    }

    private static OtlpSpan SpanFromProtobuf(
        Dictionary<int, List<PbField>> message,
        IReadOnlyDictionary<string, JsonElement> resource,
        OtlpScope scope,
        string where)
    {
        string? name = PbString(message, 5);
        if (string.IsNullOrEmpty(name))
            throw new OtlpDecodeException($"{where}.name is required");

        ulong? start = PbUInt64(message, 7);
        if (!start.HasValue)
            throw new OtlpDecodeException($"{where}.start_time_unix_nano is required");

        ulong? end = PbUInt64(message, 8);
        if (!end.HasValue)
            throw new OtlpDecodeException($"{where}.end_time_unix_nano is required");

        byte[]? parent = PbBytes(message, 4);
        string? parentSpanId = parent is { Length: > 0 }
            ? RequiredPbId(parent, 8, $"{where}.parent_span_id")
            : null;

        Dictionary<int, List<PbField>>? statusMessage = PbMessage(message, 15, 0);
        int statusCode = (int)(PbUInt64(statusMessage, 3) ?? 0);
        string statusName = statusCode >= 0 && statusCode < StatusCodeNames.Length
            ? StatusCodeNames[statusCode]
            : StatusCodeNames[0];
        string? statusText = PbString(statusMessage, 2);
        OtlpStatus status = new(statusName, string.IsNullOrEmpty(statusText) ? null : statusText);

        int kindIndex = (int)(PbUInt64(message, 6) ?? 0);
        string kindName = kindIndex >= 0 && kindIndex < SpanKindNames.Length
            ? SpanKindNames[kindIndex]
            : SpanKindNames[0];

        BigInteger startBig = new(start.Value);
        BigInteger endBig = new(end.Value);

        return new OtlpSpan(
            TraceId: RequiredPbId(PbBytes(message, 1), 16, $"{where}.trace_id"),
            SpanId: RequiredPbId(PbBytes(message, 2), 8, $"{where}.span_id"),
            ParentSpanId: parentSpanId,
            Name: name,
            Kind: kindName,
            StartTimeUnixNano: start.Value.ToString(CultureInfo.InvariantCulture),
            EndTimeUnixNano: end.Value.ToString(CultureInfo.InvariantCulture),
            Timestamp: NanosToIsoTimestamp(startBig),
            DurationMs: (double)(endBig - startBig) / 1_000_000.0,
            Status: status,
            Attributes: StripIdentityAttributes(PbKeyValues(message, 9, 0)),
            Resource: resource,
            Scope: scope);
    }

    private static OtlpMetric MetricFromProtobuf(
        Dictionary<int, List<PbField>> message,
        IReadOnlyDictionary<string, JsonElement> resource,
        OtlpScope scope,
        string where)
    {
        string? name = PbString(message, 1);
        if (string.IsNullOrEmpty(name))
            throw new OtlpDecodeException($"{where}.name is required");

        string? description = PbString(message, 2);
        string? unit = PbString(message, 3);

        Dictionary<int, List<PbField>>? gaugeMsg = PbMessage(message, 5, 0);
        Dictionary<int, List<PbField>>? sumMsg = PbMessage(message, 7, 0);
        Dictionary<int, List<PbField>>? histMsg = PbMessage(message, 9, 0);
        Dictionary<int, List<PbField>>? expHistMsg = PbMessage(message, 10, 0);
        Dictionary<int, List<PbField>>? summaryMsg = PbMessage(message, 11, 0);

        int presentCount = (gaugeMsg is not null ? 1 : 0)
            + (sumMsg is not null ? 1 : 0)
            + (histMsg is not null ? 1 : 0)
            + (expHistMsg is not null ? 1 : 0)
            + (summaryMsg is not null ? 1 : 0);
        if (presentCount > 1)
            throw new OtlpDecodeException($"{where}: multiple metric data oneof fields set");

        if (gaugeMsg is not null)
        {
            List<OtlpMetricDataPoint> points = [.. PbMessages(gaugeMsg, 1, $"{where}.gauge.data_points", 0).Select(DecodePbNumberDataPoint)];
            return new OtlpMetric(name, description, unit, "gauge", null, null, points, resource, scope);
        }

        if (sumMsg is not null)
        {
            int? temporality = PbTemporality(sumMsg, $"{where}.sum");
            ulong? monoRaw = PbUInt64(sumMsg, 3);
            bool? isMonotonic = monoRaw.HasValue ? monoRaw.Value != 0 : null;
            List<OtlpMetricDataPoint> points = [.. PbMessages(sumMsg, 1, $"{where}.sum.data_points", 0).Select(DecodePbNumberDataPoint)];
            return new OtlpMetric(name, description, unit, "sum", temporality, isMonotonic, points, resource, scope);
        }

        if (histMsg is not null)
        {
            int? temporality = PbTemporality(histMsg, $"{where}.histogram");
            List<OtlpMetricDataPoint> points = [.. PbMessages(histMsg, 1, $"{where}.histogram.data_points", 0).Select(p => DecodePbBucketDataPoint(p, 9))];
            return new OtlpMetric(name, description, unit, "histogram", temporality, null, points, resource, scope);
        }

        if (expHistMsg is not null)
        {
            int? temporality = PbTemporality(expHistMsg, $"{where}.exponential_histogram");
            List<OtlpMetricDataPoint> points = [.. PbMessages(expHistMsg, 1, $"{where}.exponential_histogram.data_points", 0).Select(p => DecodePbBucketDataPoint(p, 1))];
            return new OtlpMetric(name, description, unit, "exponentialHistogram", temporality, null, points, resource, scope);
        }

        if (summaryMsg is not null)
        {
            List<OtlpMetricDataPoint> points = [.. PbMessages(summaryMsg, 1, $"{where}.summary.data_points", 0).Select(p => DecodePbBucketDataPoint(p, 7))];
            return new OtlpMetric(name, description, unit, "summary", null, null, points, resource, scope);
        }

        return new OtlpMetric(name, description, unit, "unknown", null, null, [], resource, scope);
    }

    private static OtlpMetricDataPoint DecodePbNumberDataPoint(Dictionary<int, List<PbField>> pointMsg)
    {
        double? value = null;
        ulong? asIntBits = PbUInt64(pointMsg, 6);
        if (asIntBits.HasValue)
        {
            value = unchecked((long)asIntBits.Value);
        }
        else
        {
            ulong? asDoubleBits = PbUInt64(pointMsg, 4);
            if (asDoubleBits.HasValue)
            {
                double dbl = BitConverter.UInt64BitsToDouble(asDoubleBits.Value);
                value = double.IsFinite(dbl) ? dbl : null;
            }
        }

        ulong? timeNano = PbUInt64(pointMsg, 3);
        ulong? startNano = PbUInt64(pointMsg, 2);
        return new OtlpMetricDataPoint(
            Attributes: StripIdentityAttributes(PbKeyValues(pointMsg, 7, 0)),
            TimeUnixNano: timeNano?.ToString(CultureInfo.InvariantCulture),
            StartTimeUnixNano: startNano?.ToString(CultureInfo.InvariantCulture),
            Value: value,
            Count: null,
            Sum: null);
    }

    private static OtlpMetricDataPoint DecodePbBucketDataPoint(Dictionary<int, List<PbField>> pointMsg, int attrField)
    {
        ulong? countBits = PbUInt64(pointMsg, 4);
        double? count = countBits.HasValue && double.IsFinite(countBits.Value) ? (double)countBits.Value : null;
        ulong? sumBits = PbUInt64(pointMsg, 5);
        double? sum = null;
        if (sumBits.HasValue)
        {
            double dbl = BitConverter.UInt64BitsToDouble(sumBits.Value);
            sum = double.IsFinite(dbl) ? dbl : null;
        }

        ulong? timeNano = PbUInt64(pointMsg, 3);
        ulong? startNano = PbUInt64(pointMsg, 2);
        return new OtlpMetricDataPoint(
            Attributes: StripIdentityAttributes(PbKeyValues(pointMsg, attrField, 0)),
            TimeUnixNano: timeNano?.ToString(CultureInfo.InvariantCulture),
            StartTimeUnixNano: startNano?.ToString(CultureInfo.InvariantCulture),
            Value: null,
            Count: count,
            Sum: sum);
    }

    private static int? PbTemporality(Dictionary<int, List<PbField>> message, string where)
    {
        ulong? t = PbUInt64(message, 2);
        if (!t.HasValue || t.Value == 0)
            return null;
        if (t.Value is 1 or 2)
            return (int)t.Value;

        throw new OtlpDecodeException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{where}.aggregation_temporality must be 1 (delta) or 2 (cumulative), got {t.Value}"));
    }

    private static string RequiredPbId(byte[]? bytes, int byteLength, string where)
    {
        if (bytes is null || bytes.Length != byteLength)
        {
            throw new OtlpDecodeException(
                string.Create(CultureInfo.InvariantCulture, $"{where} must be exactly {byteLength} bytes"));
        }

        return Convert.ToHexStringLower(bytes);
    }

    private readonly record struct PbField(ulong IntValue, byte[]? BytesValue);

    private static Dictionary<int, List<PbField>> ReadProtobufMessage(ReadOnlySpan<byte> buf, int depth)
    {
        if (depth > MaxProtobufDepth)
            throw new OtlpDecodeException("protobuf message exceeds maximum nesting depth");

        Dictionary<int, List<PbField>> fields = [];
        int offset = 0;
        while (offset < buf.Length)
        {
            ulong key = ReadPbVarint(buf, ref offset);
            int field = (int)(key >> 3);
            int wire = (int)(key & 7);
            if (field < 1)
                throw new OtlpDecodeException("protobuf field number 0 is reserved");

            PbField value;
            switch (wire)
            {
                case 0:
                    value = new PbField(ReadPbVarint(buf, ref offset), null);
                    break;
                case 1:
                    if (offset + 8 > buf.Length)
                        throw new OtlpDecodeException("protobuf message is truncated");
                    ulong f64 = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(offset, 8));
                    offset += 8;
                    value = new PbField(f64, null);
                    break;
                case 2:
                    ulong lenRaw = ReadPbVarint(buf, ref offset);
                    if (lenRaw > int.MaxValue || offset + (int)lenRaw > buf.Length)
                        throw new OtlpDecodeException("protobuf message is truncated");
                    int len = (int)lenRaw;
                    byte[] slice = buf.Slice(offset, len).ToArray();
                    offset += len;
                    value = new PbField(0, slice);
                    break;
                case 5:
                    if (offset + 4 > buf.Length)
                        throw new OtlpDecodeException("protobuf message is truncated");
                    uint f32 = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(offset, 4));
                    offset += 4;
                    value = new PbField(f32, null);
                    break;
                default:
                    throw new OtlpDecodeException(
                        string.Create(CultureInfo.InvariantCulture, $"unsupported protobuf wire type {wire}: OTLP never uses groups"));
            }

            if (!fields.TryGetValue(field, out List<PbField>? list))
            {
                list = [];
                fields[field] = list;
            }

            list.Add(value);
        }

        return fields;
    }

    private static ulong ReadPbVarint(ReadOnlySpan<byte> buf, ref int offset)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            if (offset >= buf.Length)
                throw new OtlpDecodeException("protobuf message is truncated");

            byte b = buf[offset++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return value;

            shift += 7;
            if (shift >= 64)
                throw new OtlpDecodeException("protobuf varint exceeds 64 bits");
        }
    }

    private static byte[]? PbBytes(Dictionary<int, List<PbField>>? message, int field)
    {
        return message is not null && message.TryGetValue(field, out List<PbField>? list) && list.Count > 0
            ? list[0].BytesValue
            : null;
    }

    private static string? PbString(Dictionary<int, List<PbField>>? message, int field)
    {
        byte[]? bytes = PbBytes(message, field);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static ulong? PbUInt64(Dictionary<int, List<PbField>>? message, int field)
    {
        return message is not null && message.TryGetValue(field, out List<PbField>? list) && list.Count > 0 && list[0].BytesValue is null
            ? list[0].IntValue
            : null;
    }

    private static Dictionary<int, List<PbField>>? PbMessage(Dictionary<int, List<PbField>>? message, int field, int depth)
    {
        byte[]? bytes = PbBytes(message, field);
        return bytes is null ? null : ReadProtobufMessage(bytes, depth + 1);
    }

    private static List<Dictionary<int, List<PbField>>> PbMessages(
        Dictionary<int, List<PbField>>? message,
        int field,
        string where,
        int depth)
    {
        List<Dictionary<int, List<PbField>>> results = [];
        if (message is null || !message.TryGetValue(field, out List<PbField>? list))
            return results;

        for (int index = 0; index < list.Count; index++)
        {
            byte[]? bytes = list[index].BytesValue;
            if (bytes is null)
            {
                throw new OtlpDecodeException(
                    string.Create(CultureInfo.InvariantCulture, $"{where}: field {field} item {index} is not length-delimited"));
            }

            results.Add(ReadProtobufMessage(bytes, depth + 1));
        }

        return results;
    }

    private static JsonElement PbAnyValue(Dictionary<int, List<PbField>> message, int depth)
    {
        string? str = PbString(message, 1);
        if (str is not null)
            return JsonSerializer.SerializeToElement(str);

        ulong? boolVal = PbUInt64(message, 2);
        if (boolVal.HasValue)
            return JsonSerializer.SerializeToElement(boolVal.Value != 0);

        ulong? intVal = PbUInt64(message, 3);
        if (intVal.HasValue)
            return JsonSerializer.SerializeToElement(unchecked((long)intVal.Value));

        ulong? doubleBits = PbUInt64(message, 4);
        if (doubleBits.HasValue)
        {
            double dbl = BitConverter.UInt64BitsToDouble(doubleBits.Value);
            return double.IsFinite(dbl)
                ? JsonSerializer.SerializeToElement(dbl)
                : JsonSerializer.SerializeToElement<object?>(null);
        }

        Dictionary<int, List<PbField>>? array = PbMessage(message, 5, depth);
        if (array is not null)
        {
            List<JsonElement> items = [.. PbMessages(array, 1, "ArrayValue.values", depth).Select(item => PbAnyValue(item, depth + 1))];
            return JsonSerializer.SerializeToElement(items);
        }

        Dictionary<int, List<PbField>>? kvList = PbMessage(message, 6, depth);
        if (kvList is not null)
            return JsonSerializer.SerializeToElement(PbKeyValues(kvList, 1, depth + 1));

        byte[]? bytes = PbBytes(message, 7);
        if (bytes is not null)
            return JsonSerializer.SerializeToElement(Convert.ToHexStringLower(bytes));

        return JsonSerializer.SerializeToElement<object?>(null);
    }

    private static Dictionary<string, JsonElement> PbKeyValues(Dictionary<int, List<PbField>> message, int field, int depth)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        foreach (Dictionary<int, List<PbField>> pair in PbMessages(message, field, "KeyValue list", depth))
        {
            string? key = PbString(pair, 1);
            if (string.IsNullOrEmpty(key))
                continue;

            Dictionary<int, List<PbField>>? valueMsg = PbMessage(pair, 2, depth);
            result[key] = valueMsg is null
                ? JsonSerializer.SerializeToElement<object?>(null)
                : PbAnyValue(valueMsg, depth + 1);
        }

        return result;
    }
}

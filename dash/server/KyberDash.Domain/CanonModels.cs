using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberDash.Domain;

/// <summary>
/// Provenance basis of a <see cref="CostBlock"/> (never blended silently across bases).
/// </summary>
[JsonConverter(typeof(CostBasisJsonConverter))]
public enum CostBasis
{
    /// <summary>No pricing basis could be established.</summary>
    Unknown,

    /// <summary>Derived from published provider model rates and canonical token counts.</summary>
    Published,

    /// <summary>Reported directly by the harness.</summary>
    Harness,
}

/// <summary>
/// Pricing status of a <see cref="CostBlock"/>; absent rates and unbilled usage stay distinct from $0.00.
/// </summary>
[JsonConverter(typeof(CostStatusJsonConverter))]
public enum CostStatus
{
    /// <summary>Fully priced on the stated basis.</summary>
    Priced,

    /// <summary>Partially priced (at least one component lacks a rate).</summary>
    Partial,

    /// <summary>No published rate exists for the model.</summary>
    NoRate,

    /// <summary>Out of scope for cost attribution.</summary>
    OutOfScope,

    /// <summary>Explicitly not billed (for example, included subscription tier).</summary>
    NotBilled,
}

/// <summary>
/// Severity level of a canonical validation or ingest <see cref="Problem"/>.
/// </summary>
[JsonConverter(typeof(ProblemSeverityJsonConverter))]
public enum ProblemSeverity
{
    /// <summary>Record failed validation or normalization and was rejected.</summary>
    Error,

    /// <summary>Non-fatal diagnostic condition observed during processing.</summary>
    Warning,
}

/// <summary>
/// Canonical cost block attached to a record or rollup.
/// </summary>
/// <param name="Basis">Provenance basis of the cost figure.</param>
/// <param name="Status">Pricing status explaining presence or absence of <paramref name="Value"/>.</param>
/// <param name="Value">Total cost when priced or partial; <see langword="null"/> otherwise.</param>
/// <param name="Currency">ISO currency code (for example <c>"USD"</c>), when known.</param>
/// <param name="ByModel">Optional per-model breakdown of <paramref name="Value"/>.</param>
public record CostBlock(
    [property: JsonPropertyName("basis")] CostBasis Basis,
    [property: JsonPropertyName("status")] CostStatus Status,
    [property: JsonPropertyName("value"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Value = null,
    [property: JsonPropertyName("currency"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency = null,
    [property: JsonPropertyName("byModel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double>? ByModel = null);

/// <summary>
/// Disjoint token usage classes plus reported totals (<c>freshInput + cacheRead + cacheCreation == reportedInput</c>).
/// </summary>
/// <param name="FreshInput">Input tokens neither read from nor written to cache.</param>
/// <param name="CacheRead">Input tokens served from cache.</param>
/// <param name="CacheCreation">Input tokens written into cache.</param>
/// <param name="Output">Generated output tokens.</param>
/// <param name="ReportedInput">Total input tokens claimed by the harness after convention conversion.</param>
/// <param name="ReportedOutput">Total output tokens claimed by the harness.</param>
/// <param name="Reasoning">Optional reasoning subset of <paramref name="Output"/>.</param>
public record TokenUsage(
    [property: JsonPropertyName("freshInput")] long FreshInput,
    [property: JsonPropertyName("cacheRead")] long CacheRead,
    [property: JsonPropertyName("cacheCreation")] long CacheCreation,
    [property: JsonPropertyName("output")] long Output,
    [property: JsonPropertyName("reportedInput")] long ReportedInput,
    [property: JsonPropertyName("reportedOutput")] long ReportedOutput,
    [property: JsonPropertyName("reasoning"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Reasoning = null);

/// <summary>
/// Validation or ingest diagnostic surfaced rather than silently guessed over.
/// </summary>
/// <param name="Severity">Severity level of the problem.</param>
/// <param name="Code">Stable machine-readable problem code.</param>
/// <param name="Message">Human-readable explanation of the failure.</param>
/// <param name="Location">Record identifier (<c>span_id</c>) where the problem occurred, if known.</param>
/// <param name="Stack">Exception stack trace when the failure originated from a throw.</param>
public record Problem(
    ProblemSeverity Severity,
    string Code,
    string Message,
    string? Location = null,
    string? Stack = null);

/// <summary>
/// Addressable piece of a turn's context preserving MCP server attribution and optional harness token count.
/// </summary>
/// <param name="Part">Canonical content bucket key (for example <c>"system_prompt"</c> or <c>"tool_definitions"</c>).</param>
/// <param name="Text">Bounded text payload for this part.</param>
/// <param name="Tokens">Optional harness-reported token count for this part.</param>
/// <param name="Server">Ground-truth MCP server name for <c>tool_definitions</c> parts, when supplied.</param>
/// <param name="Role">Optional message role (<c>"system"</c>, <c>"user"</c>, <c>"assistant"</c>).</param>
/// <param name="Order">Stable position within the turn for deterministic round-trips.</param>
/// <param name="Resident">Explicit <see langword="false"/> for deferred tools not loaded into resident context.</param>
/// <param name="Truncated">True when text was bounded at the canonical 64 KiB cap.</param>
/// <param name="Tokenizer">Tokenizer that counted <paramref name="Tokens"/> from text; <see langword="null"/> when the count is harness-reported or absent.</param>
/// <param name="TokenizerIsModelOwn">Whether <paramref name="Tokenizer"/> is the model's own tokenizer; <see langword="null"/> when <paramref name="Tokenizer"/> is.</param>
public record ContentPart(
    [property: JsonPropertyName("part")] string Part,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Tokens = null,
    [property: JsonPropertyName("server"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Server = null,
    [property: JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role = null,
    [property: JsonPropertyName("order"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Order = null,
    [property: JsonPropertyName("resident"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Resident = null,
    [property: JsonPropertyName("truncated"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated = null,
    [property: JsonPropertyName("tokenizer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tokenizer = null,
    [property: JsonPropertyName("tokenizerIsModelOwn"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? TokenizerIsModelOwn = null);

/// <summary>
/// Normalized canonical telemetry record stored in the <c>records</c> table of <c>canon.db</c>.
/// </summary>
public record CanonicalRecord
{
    /// <summary>Primary span identifier making re-ingest idempotent.</summary>
    public required string SpanId { get; init; }

    /// <summary>Trace identifier, or <see langword="null"/> for orphan spans.</summary>
    public string? TraceId { get; init; }

    /// <summary>Parent span identifier, or <see langword="null"/> for trace roots.</summary>
    public string? ParentSpanId { get; init; }

    /// <summary>Telemetry source name (<c>service.name</c>); never used as attribution evidence.</summary>
    public required string Source { get; init; }

    /// <summary>Attributed harness identifier.</summary>
    public required string Harness { get; init; }

    /// <summary>Harness-native conversation/session identifier, when present.</summary>
    public string? SessionId { get; init; }

    /// <summary>Original span name.</summary>
    public required string Name { get; init; }

    /// <summary>Canonical operation (for example <c>"llm.invoke"</c>, <c>"tool.invoke"</c>, <c>"agent.invoke"</c>, <c>"unspecified"</c>).</summary>
    public required string Op { get; init; }

    /// <summary>Normalized OTLP span kind (for example <c>"internal"</c>, <c>"client"</c>, <c>"server"</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>ISO-8601 UTC start timestamp string.</summary>
    public required string Timestamp { get; init; }

    /// <summary>Elapsed duration in milliseconds (rounded to integer in SQLite <c>duration_ms</c>).</summary>
    public long DurationMs { get; init; }

    /// <summary>Normalized status code (<c>"unset"</c>, <c>"ok"</c>, <c>"error"</c>).</summary>
    public required string Status { get; init; }

    /// <summary>Disjoint token decomposition and reported totals.</summary>
    public required TokenUsage Tokens { get; init; }

    /// <summary>Flat per-bucket content text derived from <see cref="Parts"/> or stored directly.</summary>
    public required IReadOnlyDictionary<string, string> Content { get; init; }

    /// <summary>Structured content parts when supplied by the adapter or log enrichment.</summary>
    public IReadOnlyList<ContentPart>? Parts { get; init; }

    /// <summary>Pricing cost block.</summary>
    public required CostBlock Cost { get; init; }

    /// <summary>Optional per-metric availability map matching TS <c>Measurability</c> JSON.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Measurability { get; init; }

    /// <summary>Optional raw attribute bag preserved in compressed <c>records.raw</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Raw { get; init; }
}

/// <summary>
/// Row persisted in the <c>quarantine</c> table for unclaimed, unknown-version, or non-model spans.
/// </summary>
/// <param name="SpanId">Quarantined span identifier.</param>
/// <param name="ObservedNamespaces">Sorted distinct attribute key prefixes observed verbatim.</param>
/// <param name="Reason">Canonical quarantine reason (for example <c>"unclaimed"</c> or <c>"non-model span"</c>).</param>
/// <param name="Source">Optional emitting source name.</param>
/// <param name="Name">Optional raw span name.</param>
/// <param name="Timestamp">Optional ISO timestamp.</param>
/// <param name="RawPayload">Optional bounded decoded-span JSON evidence.</param>
/// <param name="Harness">Optional attributed harness (for non-model spans claimed by an adapter).</param>
public record QuarantineRow(
    string SpanId,
    IReadOnlyList<string> ObservedNamespaces,
    string Reason,
    string? Source = null,
    string? Name = null,
    string? Timestamp = null,
    string? RawPayload = null,
    string? Harness = null);

/// <summary>
/// Row persisted in the <c>problems</c> table linking a diagnostic problem to a span.
/// </summary>
/// <param name="SpanId">Span identifier associated with the problem.</param>
/// <param name="Severity">Wire severity string (<c>"error"</c> or <c>"warning"</c>).</param>
/// <param name="Code">Stable problem code.</param>
/// <param name="Message">Diagnostic message.</param>
/// <param name="Stack">Optional exception stack trace when thrown.</param>
/// <param name="Location">Optional record location identifier.</param>
/// <param name="SessionId">Optional session identifier.</param>
/// <param name="Harness">Optional attributed harness.</param>
/// <param name="Timestamp">Optional ISO timestamp.</param>
public record SpanProblemRow(
    string SpanId,
    string Severity,
    string Code,
    string Message,
    string? Stack = null,
    string? Location = null,
    string? SessionId = null,
    string? Harness = null,
    string? Timestamp = null);

/// <summary>
/// Row persisted in the <c>ingest_log</c> audit table.
/// </summary>
/// <param name="Id">Auto-incremented row identifier (<c>0</c> before insertion).</param>
/// <param name="Source">Normalized ingest source key.</param>
/// <param name="Count">Number of records recorded in this audit entry.</param>
/// <param name="Timestamp">ISO-8601 UTC timestamp string.</param>
public record IngestLogEntry(
    int Id,
    string Source,
    int Count,
    string Timestamp);

internal static class CanonModelWireStrings
{
    public static string ToWireString(this CostBasis basis) =>
        basis switch
        {
            CostBasis.Published => "published",
            CostBasis.Harness => "harness",
            _ => "unknown",
        };

    public static CostBasis ParseCostBasis(string? value) =>
        value switch
        {
            "published" => CostBasis.Published,
            "harness" => CostBasis.Harness,
            _ => CostBasis.Unknown,
        };

    public static string ToWireString(this CostStatus status) =>
        status switch
        {
            CostStatus.Priced => "priced",
            CostStatus.Partial => "partial",
            CostStatus.NoRate => "no_rate",
            CostStatus.OutOfScope => "out_of_scope",
            CostStatus.NotBilled => "not_billed",
            _ => "no_rate",
        };

    public static CostStatus ParseCostStatus(string? value) =>
        value switch
        {
            "priced" => CostStatus.Priced,
            "partial" => CostStatus.Partial,
            "no_rate" => CostStatus.NoRate,
            "out_of_scope" => CostStatus.OutOfScope,
            "not_billed" => CostStatus.NotBilled,
            _ => CostStatus.NoRate,
        };

    public static string ToWireString(this ProblemSeverity severity) =>
        severity switch
        {
            ProblemSeverity.Warning => "warning",
            _ => "error",
        };

    public static ProblemSeverity ParseProblemSeverity(string? value) =>
        string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase)
            ? ProblemSeverity.Warning
            : ProblemSeverity.Error;
}

/// <summary>Serializes <see cref="CostBasis"/> using wire strings.</summary>
public class CostBasisJsonConverter : JsonConverter<CostBasis>
{
    /// <inheritdoc />
    public override CostBasis Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        CanonModelWireStrings.ParseCostBasis(reader.GetString());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CostBasis value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}

/// <summary>Serializes <see cref="CostStatus"/> using wire strings.</summary>
public class CostStatusJsonConverter : JsonConverter<CostStatus>
{
    /// <inheritdoc />
    public override CostStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        CanonModelWireStrings.ParseCostStatus(reader.GetString());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CostStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}

/// <summary>Serializes <see cref="ProblemSeverity"/> using wire strings.</summary>
public class ProblemSeverityJsonConverter : JsonConverter<ProblemSeverity>
{
    /// <inheritdoc />
    public override ProblemSeverity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        CanonModelWireStrings.ParseProblemSeverity(reader.GetString());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ProblemSeverity value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>Values for the ledger and decision log <c>source</c> field: who observed the record.</summary>
public static class ArbiterSources
{
    /// <summary>The <c>kyber-weave-arbiter hook</c> observed the dispatch.</summary>
    public const string Hook = "hook";

    /// <summary>The <c>kyber-weave-arbiter serve</c> process observed the dispatch.</summary>
    public const string Serve = "serve";
}

/// <summary>Values for the ledger event <c>phase</c> field.</summary>
public static class ArbiterLedgerPhases
{
    /// <summary>Recorded before the dispatch ran.</summary>
    public const string Pre = "pre";

    /// <summary>Recorded after the dispatch returned.</summary>
    public const string Post = "post";

    /// <summary>Observed at neither boundary.</summary>
    public const string Unmarked = "unmarked";
}

/// <summary>Values for the <c>caller-source</c> field: how the caller was classified.</summary>
public static class ArbiterCallerSources
{
    /// <summary>The harness reported the caller.</summary>
    public const string Harness = "harness";

    /// <summary>The caller was recovered from the rendered prompt.</summary>
    public const string Rendered = "rendered";

    /// <summary>The caller declared itself in a header.</summary>
    public const string Header = "header";

    /// <summary>The caller was asserted by the <c>serve</c> client.</summary>
    public const string Asserted = "asserted";

    /// <summary>The caller could not be classified.</summary>
    public const string None = "none";
}

/// <summary>
/// Sortable unique record ids: sixteen fixed-width hex digits of UTC ticks
/// followed by eight random hex characters, so lexicographic order is
/// chronological and ids generated in the same tick stay unique.
/// </summary>
public static class ArbiterRecordId
{
    /// <summary>Creates a new sortable unique id for the given time.</summary>
    public static string New(DateTimeOffset at) =>
        $"{at.ToUniversalTime().UtcTicks.ToString("x16", CultureInfo.InvariantCulture)}-"
        + RandomNumberGenerator.GetHexString(8, lowercase: true);
}

/// <summary>Serializer settings shared by both append-only files.</summary>
internal static class ArbiterJson
{
    /// <summary>
    /// Null optional fields are omitted rather than written: a record's
    /// observable field set is exactly the fields it populated, which keeps
    /// both files free of fields the design does not define (Req 18.4).
    /// </summary>
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new ArbiterUtcTimestampConverter());
        return options;
    }

    /// <summary>Serializes a record to one compact JSON line.</summary>
    internal static string Serialize<T>(T record)
        where T : notnull =>
        JsonSerializer.Serialize(record, Options);
}

/// <summary>
/// Writes timestamps as UTC in the round-trip <c>o</c> format with a <c>Z</c>
/// suffix, so the ledger's <c>at</c> field is unambiguously UTC; the default
/// serializer would spell a zero offset <c>+00:00</c>.
/// </summary>
internal sealed class ArbiterUtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(
            reader.GetString() ?? throw new JsonException("expected a timestamp string"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
}

/// <summary>The dispatch headers carried on a ledger event.</summary>
/// <param name="PlanFile">The <c>plan-file</c> header, when present.</param>
/// <param name="Task">The <c>task</c> header, when present.</param>
/// <param name="Lens">The <c>lens</c> header, when present.</param>
/// <param name="Refute">The <c>refute</c> header, when present.</param>
public sealed record ArbiterLedgerHeaders(
    [property: JsonPropertyName("plan-file")] string? PlanFile = null,
    [property: JsonPropertyName("task")] string? Task = null,
    [property: JsonPropertyName("lens")] string? Lens = null,
    [property: JsonPropertyName("refute")] string? Refute = null);

/// <summary>The repository state a pre event captured.</summary>
/// <param name="Head">The <c>HEAD</c> commit id at dispatch time.</param>
/// <param name="Blobs">Blob ids of dirty and untracked paths, by path.</param>
public sealed record ArbiterSnapshot(
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("blobs")] IReadOnlyDictionary<string, string> Blobs);

/// <summary>The markers a hook read from a dispatch output.</summary>
/// <param name="TaskReview">The <c>task-review</c> marker: <c>PASS</c> or <c>FAIL</c> from <c>^RESULT:\s+(PASS|FAIL)\b</c>.</param>
/// <param name="RedEvidence">The <c>red-evidence</c> marker: the <c>RED_EVIDENCE:</c> value.</param>
/// <param name="PlannerStatus">The <c>planner-status</c> marker: the <c>STATUS:</c> value.</param>
/// <param name="Attested">The <c>attested</c> markers (D31): <c>&lt;task&gt;=complete|red</c> pairs.</param>
public sealed record ArbiterReturnMarkers(
    [property: JsonPropertyName("task-review")] string? TaskReview = null,
    [property: JsonPropertyName("red-evidence")] string? RedEvidence = null,
    [property: JsonPropertyName("planner-status")] string? PlannerStatus = null,
    [property: JsonPropertyName("attested")] IReadOnlyList<string>? Attested = null);

/// <summary>
/// One ledger event (<c>ledger.jsonl</c>, schema
/// <c>kyber-arbiter.ledger/v1</c>). Only <c>kyber-weave-arbiter hook</c> and
/// <c>serve</c> append events; the record carries paths, ids, digests and the
/// one-line evidence, never fact values, prompts or code (Req 18.4).
/// </summary>
/// <param name="Id">A sortable unique id.</param>
/// <param name="At">A UTC timestamp.</param>
/// <param name="Source"><c>hook</c> or <c>serve</c>.</param>
/// <param name="Harness">The harness token.</param>
/// <param name="Session">The payload's session id, when it has one.</param>
/// <param name="Phase"><c>pre</c>, <c>post</c> or <c>unmarked</c>.</param>
public sealed record ArbiterLedgerEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("harness")] string Harness,
    [property: JsonPropertyName("session")] string? Session,
    [property: JsonPropertyName("phase")] string Phase)
{
    /// <summary>The ledger schema id.</summary>
    public const string SchemaName = "kyber-arbiter.ledger/v1";

    /// <summary>The harness's tool-call id, where the payload carries one.</summary>
    [JsonPropertyName("call-id")]
    public string? CallId { get; init; }

    /// <summary>SHA-256 of the dispatch tool's input as finally executed, after any header strip.</summary>
    [JsonPropertyName("pair-digest")]
    public string? PairDigest { get; init; }

    /// <summary>On a post event, the pre event it was matched to.</summary>
    [JsonPropertyName("pre-id")]
    public string? PreId { get; init; }

    /// <summary>The calling agent, as classified.</summary>
    [JsonPropertyName("caller")]
    public string? Caller { get; init; }

    /// <summary>How the caller was classified.</summary>
    [JsonPropertyName("caller-source")]
    public string? CallerSource { get; init; }

    /// <summary>The dispatch target, when there is one.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>The classified trigger, when there is one.</summary>
    [JsonPropertyName("trigger")]
    public string? Trigger { get; init; }

    /// <summary>The classified marker, when there is one.</summary>
    [JsonPropertyName("marker")]
    public string? Marker { get; init; }

    /// <summary>The <c>plan-file</c>, <c>task</c>, <c>lens</c> and <c>refute</c> headers.</summary>
    [JsonPropertyName("headers")]
    public ArbiterLedgerHeaders? Headers { get; init; }

    /// <summary>On a <c>delegate</c> pre event, the resolved task files, used for the in-flight facts.</summary>
    [JsonPropertyName("task-files")]
    public IReadOnlyList<string>? TaskFiles { get; init; }

    /// <summary>On a pre event, <c>HEAD</c> and the blob ids of dirty and untracked paths.</summary>
    [JsonPropertyName("snapshot")]
    public ArbiterSnapshot? Snapshot { get; init; }

    /// <summary>On a post event, the since-dispatch set of changed paths.</summary>
    [JsonPropertyName("changed-paths")]
    public IReadOnlyList<string>? ChangedPaths { get; init; }

    /// <summary>On a post event, the markers read from the dispatch output.</summary>
    [JsonPropertyName("returns")]
    public ArbiterReturnMarkers? Returns { get; init; }

    /// <summary>
    /// On a pre event to an implementation target, whether the packet body names
    /// a path under <c>config.planning-dirs</c> (<c>KW-ARB-AUDIT-004</c>).
    /// </summary>
    [JsonPropertyName("body-names-planning-path")]
    public bool? BodyNamesPlanningPath { get; init; }

    /// <summary>The decision record for this event.</summary>
    [JsonPropertyName("decision-id")]
    public string? DecisionId { get; init; }

    /// <summary>The ledger schema id.</summary>
    [JsonPropertyName("schema")]
    public string Schema => SchemaName;
}

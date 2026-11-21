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

/// <summary>The dispatch headers carried on a ledger event.</summary>
/// <param name="PlanFile">The <c>plan-file</c> header, when present.</param>
/// <param name="Task">The <c>task</c> header, when present.</param>
/// <param name="Lens">The <c>lens</c> header, when present.</param>
/// <param name="Refute">The <c>refute</c> header, when present.</param>
/// <param name="Technology">
/// The <c>technology</c> header, when present. The worker guard reads it to bind a read-only dispatch's command
/// allow-list to the technology the dispatch names.
/// </param>
public sealed record ArbiterLedgerHeaders(
    [property: JsonPropertyName("plan-file")] string? PlanFile = null,
    [property: JsonPropertyName("task")] string? Task = null,
    [property: JsonPropertyName("lens")] string? Lens = null,
    [property: JsonPropertyName("refute")] string? Refute = null,
    [property: JsonPropertyName("technology")] string? Technology = null);

/// <summary>The repository state a pre event captured.</summary>
/// <param name="Head">The <c>HEAD</c> commit id at dispatch time.</param>
/// <param name="Blobs">Blob ids of dirty and untracked paths, by path.</param>
public sealed record ArbiterSnapshot(
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("blobs")] IReadOnlyDictionary<string, string> Blobs);

/// <summary>
/// The run a dispatch is judged within: the directory the run works in and when it started. One project ledger
/// serves every run of a repository, and two runs can use the same plan path, task ids and files, so a dispatch
/// counts as in flight or complete only for the run it belongs to.
/// </summary>
/// <param name="Root">The directory the run works in: its worktree, or the checkout it branched in place.</param>
/// <param name="StartedAt">When <c>run start</c> recorded the run; null when the record carries no time.</param>
public sealed record ArbiterRunScope(string Root, DateTimeOffset? StartedAt);

/// <summary>The markers a hook read from a dispatch output.</summary>
/// <param name="TaskReview">The <c>task-review</c> marker: <c>PASS</c> or <c>FAIL</c> from <c>^RESULT:\s+(PASS|FAIL)\b</c>.</param>
/// <param name="PlannerStatus">The <c>planner-status</c> marker: the <c>STATUS:</c> value.</param>
/// <param name="Attested">The <c>attested</c> markers (D31): <c>&lt;task&gt;=complete</c> pairs.</param>
/// <param name="TaskGate">The <c>task-gate</c> marker: the result a worker's <c>TASK_GATE:</c> digest line claims.</param>
/// <remarks>Ledgers written before test-first moved to one worker also carry <c>red-evidence</c>; it is ignored on read.</remarks>
public sealed record ArbiterReturnMarkers(
    [property: JsonPropertyName("task-review")] string? TaskReview = null,
    [property: JsonPropertyName("planner-status")] string? PlannerStatus = null,
    [property: JsonPropertyName("attested")] IReadOnlyList<string>? Attested = null,
    [property: JsonPropertyName("task-gate")] string? TaskGate = null);

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

    /// <summary>On a pre event, the directory of the run the dispatch belongs to, when a run works there.</summary>
    [JsonPropertyName("run")]
    public string? Run { get; init; }

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

    /// <summary>The <c>plan-file</c>, <c>task</c>, <c>lens</c>, <c>refute</c> and <c>technology</c> headers.</summary>
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

    /// <summary>
    /// On a post event written for a return that was lost: the stop event that closed the dispatch, such as
    /// <c>subagentStop</c>. Absent on every post event a real return wrote.
    /// </summary>
    [JsonPropertyName("recovered")]
    public string? Recovered { get; init; }

    /// <summary>The decision record for this event.</summary>
    [JsonPropertyName("decision-id")]
    public string? DecisionId { get; init; }

    /// <summary>The model profile the dispatch was routed to, when a model target answered; omitted otherwise.</summary>
    [JsonPropertyName("model-profile")]
    public string? ModelProfile { get; init; }

    /// <summary>The model token the dispatch was routed to, when a model target answered; omitted otherwise.</summary>
    [JsonPropertyName("model-token")]
    public string? ModelToken { get; init; }

    /// <summary>Where the model token came from (for example <c>model-targets</c>); omitted otherwise.</summary>
    [JsonPropertyName("model-source")]
    public string? ModelSource { get; init; }

    /// <summary>The ledger schema id.</summary>
    [JsonPropertyName("schema")]
    public string Schema => SchemaName;
}

using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Arbiter.Plans;

/// <summary>
/// One keyed row of a plan's Test or verification contract table (Req 9.3). Cells are
/// kept by header name.
/// </summary>
/// <param name="TaskId">The task id held by the row's first cell, verbatim.</param>
/// <param name="TestProjectOrFile">The <c>Test project or file</c> cell.</param>
/// <param name="RunnerFilter">The <c>Runner filter</c> or <c>Runner command</c> cell.</param>
/// <param name="ObservableBehaviour">The <c>Observable behaviour</c> (or <c>behavior</c>) cell.</param>
/// <param name="RedEvidenceRequired">The <c>RED evidence required</c> cell; read by MODE-001.</param>
/// <param name="GreenAcceptance">The <c>GREEN acceptance</c> cell.</param>
/// <remarks>
/// Headers bind by containment as well as by exact name, because the archive writes
/// variants such as <c>Automated test project or file</c> and <c>Acceptance behavior</c>.
/// The latter therefore lands in <see cref="ObservableBehaviour"/>: it is the row's
/// behaviour cell under the closest header name the contract defines. Headers matching
/// nothing — the archived release-checksums plan carries an <c>Additional current
/// evidence</c> column — are dropped.
/// </remarks>
public sealed record PlanContractRow(
    string TaskId,
    string? TestProjectOrFile = null,
    string? RunnerFilter = null,
    string? ObservableBehaviour = null,
    string? RedEvidenceRequired = null,
    string? GreenAcceptance = null);

/// <summary>
/// One task of a parsed plan or spec task artifact, in either grammar (Req 9.3).
/// </summary>
/// <param name="Id">The task id: <c>T4a</c>, <c>T1-RED-provenance-headers</c>, or the spec grammar's <c>2.1</c>.</param>
/// <param name="Title">The text after the id and its title separator; empty when the heading carries none.</param>
/// <param name="Text">The raw task body: the heading's section, or the spec item's nested bullets.</param>
/// <param name="Files">The file scope declared by the Files label family, or the body's backticked paths (spec grammar). Empty when the task lists no files.</param>
/// <param name="DependsOn">Resolved dependency task ids, in the order declared.</param>
/// <param name="Skills">Skill names from the Skills label family and the heading's <c>(agent)</c> suffix.</param>
/// <param name="Checked">The spec grammar's checkbox state, recorded for information only; it is not completion.</param>
public sealed record PlanTask(
    string Id,
    string Title,
    string Text,
    IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? DependsOn = null,
    IReadOnlyList<string>? Skills = null,
    bool Checked = false)
{
    /// <summary>The task's file scope. Empty means the task lists no files (KW-ARB-PARSE-002).</summary>
    public IReadOnlyList<string> Files { get; init; } = Files ?? [];

    /// <summary>The dependency task ids, resolved against this document's tasks.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = DependsOn ?? [];

    /// <summary>The skill names the task names or requires.</summary>
    public IReadOnlyList<string> Skills { get; init; } = Skills ?? [];
}

/// <summary>
/// The parse result of a plan or spec task artifact (Req 9): the frontmatter status and
/// development mode, the tasks of either grammar, the out-of-scope paths, the contract
/// table rows, and the parse diagnostics. The parser is a pure function of the file text
/// and writes nothing (Req 9.1).
/// </summary>
/// <param name="Status">The frontmatter <c>status</c> value, verbatim; null when absent.</param>
/// <param name="DevelopmentMode">
/// The frontmatter <c>development-mode</c>, else the body's <c>**Development mode:**</c>
/// label, else <c>test-first</c>.
/// </param>
/// <param name="HasTasks">True when the document carries a Tasks section. Without one, downstream readiness answers <c>no-tasks</c> (Req 10.2).</param>
/// <param name="Tasks">The parsed tasks of both grammars, in document order.</param>
/// <param name="OutOfScope">Backticked paths under the <c>Out of scope</c> headings, taken by the path rule.</param>
/// <param name="ContractRows">The rows of the Test or verification contract table, keyed by their task-id cell.</param>
/// <param name="Diagnostics">Parse findings: KW-ARB-PARSE-001 for unknown dependency tokens, KW-ARB-PARSE-002 for tasks that list no files.</param>
public sealed record PlanDocument(
    string? Status,
    string DevelopmentMode,
    bool HasTasks,
    IReadOnlyList<PlanTask>? Tasks = null,
    IReadOnlyList<string>? OutOfScope = null,
    IReadOnlyList<PlanContractRow>? ContractRows = null,
    IReadOnlyList<Diagnostic>? Diagnostics = null)
{
    /// <summary>The parsed tasks of both grammars, in document order.</summary>
    public IReadOnlyList<PlanTask> Tasks { get; init; } = Tasks ?? [];

    /// <summary>The backticked paths under the <c>Out of scope</c> headings.</summary>
    public IReadOnlyList<string> OutOfScope { get; init; } = OutOfScope ?? [];

    /// <summary>The contract table rows, keyed by their task-id cell.</summary>
    public IReadOnlyList<PlanContractRow> ContractRows { get; init; } = ContractRows ?? [];

    /// <summary>The parse findings raised while reading the document.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Diagnostics ?? [];
}

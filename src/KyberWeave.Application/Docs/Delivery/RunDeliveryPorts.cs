using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>The ports <see cref="RunDelivery"/> reads and runs through, held together because every delivery needs all of them.</summary>
/// <param name="Files">Reads the plans, review evidence and run records, and checks the plan files.</param>
/// <param name="Process">Runs git for the plan files and bounds the production executor.</param>
/// <param name="RunState">Reads the base each run branch was started from.</param>
/// <param name="Cycles">Reads the finished review cycles under the scratchpad.</param>
/// <param name="Environment">Names the operator's home directory and the directory a relative worktree is resolved against.</param>
/// <param name="Time">Dates the review verdict.</param>
/// <param name="Paths">Resolves the run's worktree to a full path.</param>
/// <param name="Resources">Reads the embedded pull request description template.</param>
public sealed record RunDeliveryPorts(
    IRepositoryFileStore Files,
    IProcessRunner Process,
    IRunStateStore RunState,
    IReviewCycleStore Cycles,
    IProcessEnvironment Environment,
    TimeProvider Time,
    IPathResolver Paths,
    IEmbeddedResources Resources);

/// <summary>Runs <see cref="RunDelivery"/> with the production executor and the user profile's run records.</summary>
/// <param name="ports">The ports the delivery reads and runs through.</param>
public sealed class RunDeliveryRunner(RunDeliveryPorts ports) : KyberWeave.Contracts.Services.IRunDeliveryRunner
{
    /// <inheritdoc />
    public Task<DeliveryOutcome> RunAsync(string repoRoot, KyberWeave.Core.Configuration.KyberWeaveConfig config, DeliveryRequest request, CancellationToken cancellationToken = default) =>
        new RunDelivery(repoRoot, config, null, null, ports).RunAsync(request, cancellationToken);
}

/// <summary>The production executor: the repository's deadlock-safe runner, under one time limit per call.</summary>
internal sealed class BoundedDeliveryProcessRunner : IProcessRunner
{
    private readonly IProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public BoundedDeliveryProcessRunner(IProcessRunner runner, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
        _timeout = timeout;
    }

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _runner.RunAsync(request with { Timeout = request.Timeout ?? _timeout }, cancellationToken);
    }

    public bool IsAlive(int processId) => _runner.IsAlive(processId);

    public string? FindOnPath(string program) => _runner.FindOnPath(program);
}

/// <summary>Reads task gate reports, review cycles and filed todos for <see cref="RunDelivery"/>.</summary>
internal sealed class RunDeliveryEvidence(
    string repoRoot,
    KyberWeaveConfig config,
    string homeDirectory,
    ActivePlanFiles files,
    IRepositoryFileStore store,
    IReviewCycleStore cycles,
    TimeProvider time,
    Func<string[], CancellationToken, Task<ProcessResult>> runGitAsync)
{
    /// <summary>
    /// Summarizes per-task gate reports and review evidence into <paramref name="validation"/> and
    /// <paramref name="notVerified"/>, returning any pending review items.
    /// </summary>
    public async Task<IReadOnlyList<string>> CollectValidationAsync(
        List<string> validation,
        List<string> notVerified,
        string branch,
        string mergeBase,
        DiagnosticReport report,
        CancellationToken cancellationToken)
    {
        (string? taskLine, IReadOnlyList<string> taskNotVerified) = RunEvidence.SummarizeTaskGates(
            await TaskGateReportsAsync(mergeBase, report, cancellationToken).ConfigureAwait(false));
        if (taskLine is null)
            notVerified.Add("No per-task check results were recorded for this run.");
        else
            validation.Add(taskLine);
        notVerified.AddRange(taskNotVerified);

        ReviewEvidence? review = await ReviewAsync(branch, mergeBase, report, cancellationToken).ConfigureAwait(false);
        (string reviewLine, IReadOnlyList<string> pending) = RunEvidence.SummarizeReview(review);
        if (review is null)
            notVerified.Add("No code review result was recorded for this branch.");
        else
            validation.Add(reviewLine);

        return pending;
    }

    /// <summary>
    /// One line per todo this branch added, committed by now: its title and path. These are the follow-up work the run
    /// filed for what it found outside its scope.
    /// </summary>
    public async Task<List<string>> FiledTodosAsync(string mergeBase, string todoFolder, DiagnosticReport report, CancellationToken cancellationToken)
    {
        if (todoFolder.Length == 0)
            return [];

        return [.. (await runGitAsync(["diff", "--name-only", "--diff-filter=A", "-z", mergeBase, "HEAD", "--"], cancellationToken).ConfigureAwait(false))
            .StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => RunDelivery.IsPlanFile(path, todoFolder))
            .Order(StringComparer.Ordinal)
            .Select(path => (Path: path, Text: ReadTodo(path, report)))
            .Where(todo => todo.Text is not null)
            .Select(todo => $"{RunEvidence.DocumentTitle(todo.Text ?? string.Empty) ?? Path.GetFileNameWithoutExtension(todo.Path)} (`{todo.Path}`)")];
    }

    /// <summary>
    /// The passing-run gate report of each task, from this run only: written after the run started, or measured against
    /// this change's base. A test-first failing run is written to <c>task-gate-red.json</c> (or, in older runs, a
    /// <c>-red</c> folder) and is never read as a failed check.
    /// </summary>
    private async Task<List<GateReport>> TaskGateReportsAsync(string mergeBase, DiagnosticReport report, CancellationToken cancellationToken)
    {
        string scratch = Path.Combine(repoRoot, ScratchLayout.Root);
        if (!store.DirectoryExists(scratch))
            return [];

        DateTimeOffset? started = await RunStartedAtAsync(cancellationToken).ConfigureAwait(false);
        List<GateReport> reports = [];
        foreach (string folder in store.EnumerateDirectories(scratch))
        {
            string file = Path.Combine(folder, ScratchLayout.TaskGateFileName);
            if (!store.FileExists(file) || Path.GetFileName(folder).EndsWith("-red", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                GateReport gates = ReviewJson.ReadGates(store.ReadAllTextBounded(file));
                bool thisRun = started is { } at
                    ? store.GetLastWriteTimeUtc(file) >= at.UtcDateTime
                    : string.Equals(gates.Base, mergeBase, StringComparison.Ordinal);
                if (thisRun)
                    reports.Add(gates);
            }
            catch (Exception exception) when (IsEvidenceFailure(exception))
            {
                // An unreadable report is evidence the run cannot use; the description then says none was recorded, and
                // this warning says why, so "never ran" and "could not be read" are not the same silence.
                WarnUnreadable(report, $"The task gate report '{Path.GetRelativePath(repoRoot, file).Replace('\\', '/')}'", exception);
            }
        }

        return reports;
    }

    private async Task<DateTimeOffset?> RunStartedAtAsync(CancellationToken cancellationToken)
    {
        string branch = (await runGitAsync(["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        try
        {
            using JsonDocument record = JsonDocument.Parse(store.ReadAllTextBounded(RunBaseStateFormat.StatePath(homeDirectory, branch)));
            return record.RootElement.TryGetProperty("StartedAt", out JsonElement at) && at.ValueKind == JsonValueKind.String
                   && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset value)
                ? value
                : null;
        }
        catch (Exception exception) when (IsEvidenceFailure(exception))
        {
            // No readable start record: reports are matched to this change by their base. A corrupt record never
            // reaches here; delivery refuses it earlier (KW-DELIVER-002).
            return null;
        }
    }

    /// <summary>The last review cycle that belongs to this change: this branch, this base, a head HEAD descends from.</summary>
    private async Task<ReviewEvidence?> ReviewAsync(string branch, string mergeBase, DiagnosticReport report, CancellationToken cancellationToken)
    {
        string scratch = Path.Combine(repoRoot, ScratchLayout.Root);
        if (!store.DirectoryExists(scratch))
            return null;

        List<ReviewCycleRecord> belonging = await BelongingReviewCyclesAsync(scratch, branch, mergeBase, cancellationToken).ConfigureAwait(false);
        if (belonging.Count == 0)
            return null;

        ReviewCycleRecord last = belonging.OrderBy(cycle => cycle.Number).ThenBy(cycle => store.GetLastWriteTimeUtc(cycle.FindingsPath)).Last();
        return EvaluateReviewCycle(last, report);
    }

    private async Task<List<ReviewCycleRecord>> BelongingReviewCyclesAsync(string scratch, string branch, string mergeBase, CancellationToken cancellationToken)
    {
        string head = (await runGitAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        ReviewCycleIdentity current = new(branch, mergeBase, head);

        List<IReadOnlyList<ReviewCycleRecord>> finishedByFolder = [];
        foreach (string folder in store.EnumerateDirectories(scratch))
            finishedByFolder.Add(cycles.ReadFinished(Path.Combine(folder, ScratchLayout.ReviewFolderName)));

        // The ancestry question is answered once per recorded head before the planning runs over them.
        Dictionary<string, bool> ancestorOfHead = [];
        foreach (IReadOnlyList<ReviewCycleRecord> finished in finishedByFolder)
            foreach (ReviewCycleRecord cycle in finished)
                if (cycle.Head is { Length: > 0 } recordedHead && !ancestorOfHead.ContainsKey(recordedHead))
                    ancestorOfHead[recordedHead] = (await runGitAsync(["merge-base", "--is-ancestor", recordedHead, "HEAD"], cancellationToken).ConfigureAwait(false)).ExitCode == 0;

        List<ReviewCycleRecord> belonging = [];
        foreach (IReadOnlyList<ReviewCycleRecord> finished in finishedByFolder)
            belonging.AddRange(ReviewCyclePlanning.Belonging(finished, current, commit => ancestorOfHead.GetValueOrDefault(commit)).Valid);

        return belonging;
    }

    private ReviewEvidence? EvaluateReviewCycle(ReviewCycleRecord cycle, DiagnosticReport report)
    {
        try
        {
            FindingsReport findings = ReviewJson.ReadFindings(store.ReadAllTextBounded(cycle.FindingsPath));
            IReadOnlyList<GateResult> gates = cycle.Gates?.Gates ?? [];
            ReviewOutcome outcome = VerdictEngine.Evaluate(findings.Findings, gates, config.Review, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
            return new ReviewEvidence(cycle.Number, outcome, gates);
        }
        catch (Exception exception) when (IsEvidenceFailure(exception))
        {
            WarnUnreadable(report, $"The findings of review cycle {cycle.Number.ToString(CultureInfo.InvariantCulture)}", exception);
            return null;
        }
    }

    /// <summary>
    /// A todo's text, or null when it is gone. One that is there but unreadable is still follow-up work the run filed, so
    /// it is listed by its file name (empty text) rather than failing the delivery or being left out.
    /// </summary>
    private string? ReadTodo(string path, DiagnosticReport report)
    {
        try
        {
            return store.ReadTextIfPresent(files.Absolute(path));
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            WarnUnreadable(report, $"The filed todo '{path}'", exception, "it is listed by its file name alone");
            return string.Empty;
        }
    }

    /// <summary>Whether <paramref name="exception"/> is a way a scratch evidence file can fail to read: I/O, permission, malformed JSON or too large.</summary>
    internal static bool IsEvidenceFailure(Exception exception) =>
        FileFailure.Is(exception) || exception is JsonException or InvalidDataException;

    /// <summary>Records that a piece of run evidence exists but could not be used, so a reader of the report can tell that from evidence never recorded.</summary>
    private static void WarnUnreadable(DiagnosticReport report, string what, Exception exception, string? consequence = null) =>
        report.Add(new Diagnostic(RunDelivery.Step, Severity.Warning,
            $"{what} could not be read ({exception.GetType().Name}: {exception.Message}); {consequence ?? "the description says none was recorded"}.", "deliver"));
}

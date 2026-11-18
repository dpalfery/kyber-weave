using KyberWeave.Tests.Configuration;

using System.Diagnostics;
using System.Text.RegularExpressions;

using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Doctor;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Doctor;
using KyberWeave.Domain.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Data.CodeGraph;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the cadence of the docs server's own CodeGraph refresh (D23): the first tool call refreshes,
/// later calls do not, and a refresh another process is already running is reported as one note
/// rather than as a failure.
/// </summary>
/// <remarks>
/// <para>
/// The server used to leave the index to whoever happened to run <c>codegraph</c> last, so joins
/// answered from whatever was on disk when the session started. Refreshing on every call would put
/// an eight-second index behind every question. Refreshing on the first call and then at a bounded
/// interval is the compromise, and these tests pin the two ends that are observable from outside:
/// the first call does refresh, and the presence of another process's lock never turns a question
/// into an error.
/// </para>
/// <para>
/// The refresh is observed through the cross-process lock file the repository already uses for index
/// work (<see cref="CodeGraphIndexRules.LockPathFor"/>). That path is public and derived from the
/// repository root, so a temp root's lock is private to one test and no concurrent run can collide
/// with it. The lock is acquired with no sharing, which is what the provisioner does, so a busy
/// lock is the real condition rather than a simulated one.
/// </para>
/// <para>
/// D23 applies the refresh only where CodeGraph is declared, and <c>mcp.required-servers</c> is where
/// a repository declares it. Every test here that expects a refresh therefore arranges a repository
/// that requires CodeGraph (<see cref="ToolsRequiringCodeGraph"/>); the test that pins the other side
/// leaves the scaffold's own declaration — <c>kw-docs</c> alone, CodeGraph commented out — untouched.
/// </para>
/// <para>
/// The refresh's index child is scripted, not run (<see cref="ProvisionSucceeds"/>). The refresh only
/// reaches its resolver after a successful provision, so a real <c>codegraph</c> — present on a
/// developer's machine, absent on a CI runner — would otherwise choose which branch of the refresh
/// these tests exercise, and the branch a test pins would change with the machine it ran on.
/// </para>
/// </remarks>
public sealed class McpCodeGraphRefreshTests : IDisposable
{
    /// <summary>How a busy lock is reported: one line naming the index work already under way.</summary>
    private static readonly Regex BusyNote = new(
        @"(?im)^.*\b(?:codegraph|index)\b.*\b(?:busy|lock|refresh\w*|another)\b.*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// How a repository that set <c>stop-on-tool-failure: false</c> is told CodeGraph was unusable:
    /// one line opening with <c>warning:</c>.
    /// </summary>
    private static readonly Regex CodeGraphWarning = new(
        @"(?im)^warning:.*codegraph.*$",
        RegexOptions.CultureInvariant);

    /// <summary>The documented bound on one refresh, which a tool call may never exceed on top of its own work.</summary>
    private static readonly TimeSpan RefreshBound = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private string Root => _root.Path;

    /// <summary>
    /// The first tool call refreshes CodeGraph, which is why the index lock is taken during
    /// <c>Explore</c>.
    /// </summary>
    /// <remarks>
    /// D23: the first tool call refreshes. Without it a long session answers from the index as it
    /// stood when the server started, and the joins silently describe a tree that has since moved.
    /// The lock is the durable trace of that work: it is the same file the provisioner takes, so a
    /// refresh that skipped the lock would let two processes rewrite one index at once.
    /// </remarks>
    [Fact]
    public void TheFirstToolCallRefreshesCodeGraph()
    {
        DocsTools tools = ToolsRequiringCodeGraph(Root);
        string lockPath = LockPath(Root);
        RemoveStaleLock(lockPath);

        _ = tools.Explore("anything");

        Assert.True(
            File.Exists(lockPath),
            $"The first docs tool call took no CodeGraph index lock at '{lockPath}', so it did not refresh the index.");
    }

    /// <summary>
    /// While another process holds the refresh lock the tool answers with a one-line note, does not
    /// report a tool failure, and returns inside the refresh bound.
    /// </summary>
    /// <remarks>
    /// D23: a busy lock means another process is already refreshing, so the answer on disk is at
    /// worst one refresh behind — it is not a failure, and it must not become a wait. Treating it as
    /// a failure would stop a run because two agents asked at the same moment; waiting on it would
    /// put another process's eight-second index on this call's critical path.
    /// </remarks>
    [Fact]
    public void ABusyRefreshLockAnswersOneNoteInsteadOfFailingOrWaiting()
    {
        DocsTools tools = ToolsRequiringCodeGraph(Root);
        string lockPath = LockPath(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        using FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Stopwatch elapsed = Stopwatch.StartNew();
        string response = tools.Explore("anything");
        elapsed.Stop();

        TimeSpan waitBudget = TimeSpan.FromSeconds(2);
        Assert.True(
            elapsed.Elapsed < waitBudget,
            $"The tool call spent {elapsed.Elapsed.TotalSeconds:0.0}s with the refresh lock held by another "
            + $"process; a busy lock must not wait (tight budget {waitBudget.TotalSeconds:0}s).");
        Assert.True(
            elapsed.Elapsed < RefreshBound,
            $"The tool call spent {elapsed.Elapsed.TotalSeconds:0.0}s with the refresh lock held by another "
            + $"process; the refresh may wait at most {RefreshBound.TotalSeconds:0}s.");
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.True(
            BusyNote.IsMatch(response),
            $"With another process holding the refresh lock the response carries no note naming the index "
            + $"work already under way. It was:{Environment.NewLine}{response}");
    }

    /// <summary>
    /// When the refresh throws an exception (e.g. resolver construction failure), the failure is
    /// converted to STATUS: TOOL_FAILURE rather than escaping, and the throttle does not bypass the gate.
    /// </summary>
    [Fact]
    public void WhenResolverFactoryThrowsDuringRefresh_FailsToolsUnderStopRule()
    {
        DocsTestCommands.Scaffolding.Scaffold(Root);
        McpDeclarationFixture.RequireCodeGraph(Root);

        int resolverCalls = 0;
        DocsTools tools = ScaffoldedTools(
            Root,
            () =>
            {
                resolverCalls++;
                throw new InvalidOperationException("boom resolver failed");
            });

        string first = tools.Explore("anything");
        Assert.Contains("STATUS: TOOL_FAILURE", first, StringComparison.Ordinal);
        Assert.Contains("boom resolver failed", first, StringComparison.Ordinal);
        Assert.DoesNotContain("documents=0", first, StringComparison.Ordinal);

        // Subsequent call must not answer Clear as if healthy
        string second = tools.Explore("anything");
        Assert.Contains("STATUS: TOOL_FAILURE", second, StringComparison.Ordinal);
        Assert.Contains("boom resolver failed", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the resolver throws and the configuration file is unparseable,
    /// the failure defaults to the stop rule (STATUS: TOOL_FAILURE) rather than fail-open advisory.
    /// </summary>
    [Fact]
    public void WhenResolverFactoryThrowsAndConfigUnreadable_DefaultsToStopRule()
    {
        DocsTestCommands.Scaffolding.Scaffold(Root);
        McpDeclarationFixture.RequireCodeGraph(Root);

        DocsTools tools = ScaffoldedTools(
            Root,
            () =>
            {
                File.WriteAllText(Path.Combine(Root, ".kyber-weave", "kyber-weave.yml"), "unparseable: [yaml: broken");
                throw new InvalidOperationException("boom resolver failed");
            });

        string response = tools.Explore("anything");
        Assert.Contains("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.Contains("boom resolver failed", response, StringComparison.Ordinal);
        Assert.DoesNotContain("warning:", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the docs tree is unreadable and CodeGraph fails, the blocked response reports
    /// documents=unavailable rather than conflating failure with an empty corpus (documents=0).
    /// </summary>
    [Fact]
    public void WhenDocsTreeUnreadable_BlockedResponseReportsDocumentsUnavailable()
    {
        DocsTestCommands.Scaffolding.Scaffold(Root);
        McpDeclarationFixture.RequireCodeGraph(Root);

        KyberWeaveConfig config = ConfigFileReader.Load(Root);
        DocumentIndexHost host = new(
            Root,
            () => throw new InvalidOperationException("boom resolver"),
            () => throw new IOException("disk error / unreadable docs"),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);

        DocsTools tools = DocsToolsComposition.Create(
            host,
            null,
            DocsTestCommands.Freshness(Root, ProvisionSucceeds()));

        string response = tools.Explore("anything");
        Assert.Contains("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.Contains("documents=unavailable", response, StringComparison.Ordinal);
        Assert.DoesNotContain("documents=0", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only genuine sharing violations are classified as contention; non-lock IO exceptions
    /// (such as PathTooLongException or disk errors) are not treated as busy contention.
    /// </summary>
    [Fact]
    public void NonSharingViolationIOException_IsNotTreatedAsBusyLock()
    {
        Assert.False(FileCodeGraphIndexWorkspace.IsSharingViolation(new PathTooLongException("path too long")));
        Assert.False(FileCodeGraphIndexWorkspace.IsSharingViolation(new IOException("disk sector error", unchecked((int)0x80070017))));

        Assert.True(FileCodeGraphIndexWorkspace.IsSharingViolation(new IOException("sharing violation", unchecked((int)0x80070020))));
        Assert.True(FileCodeGraphIndexWorkspace.IsSharingViolation(new IOException("The process cannot access the file because it is being used by another process.")));
    }

    /// <summary>
    /// The interval suppresses a second refresh, so a burst of questions costs one index rather than
    /// one per question.
    /// </summary>
    /// <remarks>
    /// D23: at most once every 60 seconds. The proof here is that a second call made while the lock
    /// is held produces no busy note: a refresh attempt would have hit the held lock and said so, so
    /// its silence is the record that no attempt was made.
    /// </remarks>
    [Fact]
    public void TheRefreshIntervalSuppressesASecondAttempt()
    {
        DocsTools tools = ToolsRequiringCodeGraph(Root);
        string lockPath = LockPath(Root);
        RemoveStaleLock(lockPath);

        _ = tools.Explore("anything");
        Assert.True(
            File.Exists(lockPath),
            $"The first docs tool call took no CodeGraph index lock at '{lockPath}', so it did not refresh the index.");
        DateTime firstRefresh = File.GetLastWriteTimeUtc(lockPath);

        using FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string second = tools.Explore("anything");

        Assert.True(
            !BusyNote.IsMatch(second),
            $"The second tool call inside the refresh interval attempted a refresh of its own. It answered:"
            + $"{Environment.NewLine}{second}");
        Assert.Equal(firstRefresh, File.GetLastWriteTimeUtc(lockPath));
    }

    /// <summary>
    /// A repository whose declaration names no CodeGraph takes no index lock and gets ordinary docs
    /// answers, whichever way it set the stop rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D23 applies the refresh only where CodeGraph is declared, and <c>mcp.required-servers</c> is
    /// where a repository declares it. The configuration <c>docs init</c> scaffolds declares
    /// <c>kw-docs</c> and leaves the CodeGraph entry commented out, so the scaffold's own default is a
    /// repository that asked for no index. Refreshing there spends a provision on a dependency the
    /// repository never claimed, and once CodeGraph is not installed every docs question in that
    /// repository answers <c>STATUS: TOOL_FAILURE</c> — a failure about the tool's own guess rather
    /// than about anything the repository declared.
    /// </para>
    /// <para>
    /// The refresh is observed through the index lock, the same seam the tests above use: a refresh
    /// that ran would have taken it. The resolver is the production adapter over this repository,
    /// which has no index, so a refresh that ran would find CodeGraph unusable and report it in the
    /// form the stop rule selects — the failure under the default, the warning line under
    /// <c>stop-on-tool-failure: false</c>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARepositoryThatRequiresNoCodeGraphNeitherRefreshesNorFailsItsDocsTools(bool stopsOnToolFailure)
    {
        string stopRule = stopsOnToolFailure ? "the default stop rule" : "stop-on-tool-failure: false";
        string lockPath = LockPath(Root);
        RemoveStaleLock(lockPath);

        DocsTestCommands.Scaffolding.Scaffold(Root);
        if (!stopsOnToolFailure)
        {
            File.AppendAllText(
                Path.Combine(Root, ".kyber-weave", "kyber-weave.yml"),
                Environment.NewLine + "stop-on-tool-failure: false" + Environment.NewLine);
        }

        DocsTools tools = ScaffoldedTools(Root, () => CodeGraphResolverAdapter.ForRepository(Root));

        string response = tools.Explore("anything");

        Assert.False(
            File.Exists(lockPath),
            $"A repository that declares no CodeGraph took the index lock at '{lockPath}', so it refreshed an "
            + "index it never asked for.");
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.False(
            CodeGraphWarning.IsMatch(response),
            $"Under {stopRule} the response carries a CodeGraph warning although the repository declares no "
            + $"CodeGraph. It was:{Environment.NewLine}{response}");
        Assert.StartsWith("provenance:", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sync that finds CodeGraph's own index lock held once is run again, and the answer carries
    /// neither a failure nor a note once the second sync succeeds.
    /// </summary>
    /// <remarks>
    /// Two agents starting in one worktree meet on CodeGraph's lock for a fraction of a second. The
    /// index was healthy throughout, so the caller should not be able to tell the retry happened.
    /// </remarks>
    [Fact]
    public void ASyncThatFindsTheIndexLockHeldOnceIsRetriedAndAnswersNormally()
    {
        ScriptedProcessExecutor executor = SyncAnswers(HeldIndexLock, SyncSucceeded);
        List<TimeSpan> pauses = [];
        DocsTools tools = ToolsWithAnIndex(Root, executor, pauses);

        string response = tools.Explore("anything");

        Assert.Equal(2, executor.Calls.Count);
        Assert.Single(pauses);
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.DoesNotContain("note: codegraph refresh skipped", response, StringComparison.Ordinal);
        Assert.False(CodeGraphWarning.IsMatch(response), response);
        Assert.StartsWith("provenance:", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sync that finds CodeGraph's own index lock held on every attempt gives the answer with one
    /// note, under either stop rule: no failure, and no warning that tells a worker to stop.
    /// </summary>
    /// <remarks>
    /// The lock is held by CodeGraph's server, its watcher, or another sync, so the index on disk is
    /// usable and only busy. Reporting it as a failure cost a live run a dispatch.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASyncThatAlwaysFindsTheIndexLockHeldAnswersWithANoteAndNoFailure(bool stopsOnToolFailure)
    {
        ScriptedProcessExecutor executor = SyncAnswers(HeldIndexLock);
        List<TimeSpan> pauses = [];
        DocsTools tools = ToolsWithAnIndex(Root, executor, pauses, stopsOnToolFailure);

        string response = tools.Explore("anything");

        Assert.Equal(3, executor.Calls.Count);
        Assert.True(
            pauses.Count == 2 && pauses.Sum(pause => pause.TotalSeconds) <= 1.5,
            $"A held CodeGraph lock paused {string.Join(", ", pauses)}; a tool call may wait twice, 1.5 s in all.");
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.False(CodeGraphWarning.IsMatch(response), response);
        Assert.Contains("note: codegraph refresh skipped", response, StringComparison.Ordinal);
        Assert.StartsWith("provenance:", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sync that fails for any other reason is run once and fails the tool as before: the failure
    /// token under the default stop rule, the warning line under <c>stop-on-tool-failure: false</c>.
    /// </summary>
    /// <remarks>
    /// The second message is CodeGraph's "rebuild in progress". It is a lock message too, but the
    /// database file is being replaced, so there is no index on disk to answer from.
    /// </remarks>
    [Theory]
    [InlineData("✗ Failed to sync: SQLITE_ERROR: no such table: nodes", true)]
    [InlineData("✗ Failed to sync: SQLITE_ERROR: no such table: nodes", false)]
    [InlineData("✗ Failed to sync: A rebuild of this index is in progress; retry when it finishes.", true)]
    public void ASyncThatFailsForAnotherReasonStillFailsTheToolWithoutARetry(string error, bool stopsOnToolFailure)
    {
        ScriptedProcessExecutor executor = SyncAnswers(new ProcessResult(1, SyncBanner, error + "\n"));
        List<TimeSpan> pauses = [];
        DocsTools tools = ToolsWithAnIndex(Root, executor, pauses, stopsOnToolFailure);

        string response = tools.Explore("anything");

        Assert.Single(executor.Calls);
        Assert.Empty(pauses);
        Assert.DoesNotContain("note: codegraph refresh skipped", response, StringComparison.Ordinal);
        if (stopsOnToolFailure)
        {
            Assert.Contains("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
            Assert.Contains("reason: codegraph sync exited 1: " + error, response, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
            Assert.True(CodeGraphWarning.IsMatch(response), response);
        }
    }

    /// <summary>With no CodeGraph lock file the refresh syncs at once, without waiting.</summary>
    [Fact]
    public void WithNoLockFileTheRefreshSyncsAtOnce()
    {
        ScriptedProcessExecutor executor = SyncAnswers(SyncSucceeded);
        List<TimeSpan> waits = [];
        DocsTools tools = ToolsWithAnIndex(Root, executor, waits);

        _ = tools.Explore("anything");

        Assert.Single(executor.Calls);
        Assert.Empty(waits);
    }

    /// <summary>
    /// A CodeGraph lock file that goes away during the first, second or third wait lets the sync run
    /// then, and not before: no sync is started while a look still finds the file.
    /// </summary>
    /// <remarks>
    /// The owner's rule: look for the lock file before any sync; while it exists wait 1 second, then
    /// 5, then 30, looking again after each.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ALockFileThatGoesAwayDuringAWaitLetsTheSyncRunThen(int waitsBeforeRelease)
    {
        ScriptedProcessExecutor executor = SyncAnswers(SyncSucceeded);
        List<TimeSpan> waits = [];
        DocsTools tools = ToolsWithAnIndex(Root, executor, wait =>
        {
            Assert.Empty(executor.Calls);
            waits.Add(wait);
            if (waits.Count == waitsBeforeRelease)
                File.Delete(IndexLockFile);
        });
        File.WriteAllText(IndexLockFile, LivePid);

        string response = tools.Explore("anything");

        Assert.Equal(LockFileWaits.Take(waitsBeforeRelease), waits);
        Assert.Single(executor.Calls);
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.DoesNotContain("note: codegraph refresh skipped", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lock file still present after the 1, 5 and 30 second waits fails the tool without one sync.
    /// The failure names the file and the process it records, and the fix follows from whether that
    /// process is running. The verdict is not kept: the next call looks for the file again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALockFileThatOutlastsEveryWaitFailsTheToolWithoutSyncing(bool holderIsRunning)
    {
        ScriptedProcessExecutor executor = SyncAnswers(SyncSucceeded);
        List<TimeSpan> waits = [];
        DocsTools tools = ToolsWithAnIndex(Root, executor, waits);
        string pid = holderIsRunning ? LivePid : DeadPid;
        File.WriteAllText(IndexLockFile, pid);

        string response = tools.Explore("anything");

        Assert.Empty(executor.Calls);
        Assert.Equal(LockFileWaits, waits);
        Assert.Contains("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.Contains("tool: codegraph", response, StringComparison.Ordinal);
        Assert.Contains("still held after 36 seconds", response, StringComparison.Ordinal);
        Assert.Contains(IndexLockFile, response, StringComparison.Ordinal);
        Assert.Contains(
            $"PID {pid}, which is {(holderIsRunning ? "running" : "not running")}", response, StringComparison.Ordinal);
        Assert.Contains(
            holderIsRunning ? $"fix: wait for process {pid} to finish" : "fix: codegraph unlock ",
            response,
            StringComparison.Ordinal);

        File.Delete(IndexLockFile);
        string next = tools.Explore("anything");

        Assert.Single(executor.Calls);
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", next, StringComparison.Ordinal);
    }

    /// <summary>
    /// Under <c>stop-on-tool-failure: false</c> the same outlasting lock file is the warning line and
    /// the answer still comes back, again without a sync.
    /// </summary>
    [Fact]
    public void ALockFileThatOutlastsEveryWaitIsAWarningWhenTheRepositoryOptedOut()
    {
        ScriptedProcessExecutor executor = SyncAnswers(SyncSucceeded);
        List<TimeSpan> waits = [];
        DocsTools tools = ToolsWithAnIndex(Root, executor, waits, stopsOnToolFailure: false);
        File.WriteAllText(IndexLockFile, LivePid);

        string response = tools.Explore("anything");

        Assert.Empty(executor.Calls);
        Assert.Equal(LockFileWaits, waits);
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.True(CodeGraphWarning.IsMatch(response), response);
        Assert.Contains("still held after 36 seconds", response, StringComparison.Ordinal);
    }

    /// <summary>The owner's schedule: the waits between looks at CodeGraph's lock file.</summary>
    private static readonly TimeSpan[] LockFileWaits =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>This test process, which is running for as long as the test is.</summary>
    private static readonly string LivePid =
        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A process id above any the operating systems these tests run on hand out.</summary>
    private const string DeadPid = "2147483600";

    /// <summary>Where CodeGraph 1.6.2 keeps its own lock: <c>.codegraph/codegraph.lock</c>.</summary>
    private string IndexLockFile => Path.Combine(Root, ".codegraph", "codegraph.lock");

    /// <summary>What <c>codegraph sync .</c> prints on stdout before it reports anything.</summary>
    private const string SyncBanner = "┌  Syncing CodeGraph\n│\n";

    /// <summary>
    /// What CodeGraph 1.6.2 answered, captured from a real run, when <c>codegraph sync .</c> found
    /// <c>.codegraph/codegraph.lock</c> held by a live process: exit 1, and this one line on stderr.
    /// </summary>
    private static readonly ProcessResult HeldIndexLock = new(
        1,
        SyncBanner,
        "✗ Failed to sync: Sync could not acquire the file lock; retry when the index is available. "
        + "CodeGraph database is locked by another process (PID 91106). If this is stale, run "
        + "'codegraph unlock' or delete /repo/.codegraph/codegraph.lock\n");

    private static readonly ProcessResult SyncSucceeded = new(0, string.Empty, string.Empty);

    /// <summary>
    /// A CodeGraph child that gives each sync the next of <paramref name="answers"/>, and the last one
    /// to every sync after that.
    /// </summary>
    private static ScriptedProcessExecutor SyncAnswers(params ProcessResult[] answers)
    {
        int next = 0;
        return new ScriptedProcessExecutor(startInfo =>
        {
            if (Path.GetFileName(startInfo.FileName) != "codegraph" || startInfo.ArgumentList[0] != "sync")
            {
                throw new InvalidOperationException(
                    $"The refresh ran '{startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)}', not a sync.");
            }

            ProcessResult answer = answers[Math.Min(next, answers.Length - 1)];
            next++;
            return answer;
        });
    }

    /// <summary>
    /// Tools over a repository that requires CodeGraph and already has a <c>.codegraph/</c> folder, so
    /// the refresh runs <c>sync</c> rather than <c>init</c>. The waits between sync attempts are
    /// recorded in <paramref name="pauses"/> instead of being waited out.
    /// </summary>
    private static DocsTools ToolsWithAnIndex(
        string root, IProcessExecutor executor, List<TimeSpan> pauses, bool stopsOnToolFailure = true) =>
        ToolsWithAnIndex(root, executor, pauses.Add, stopsOnToolFailure);

    /// <summary>The same tools, with each wait handed to <paramref name="pause"/> instead of being waited out.</summary>
    private static DocsTools ToolsWithAnIndex(
        string root, IProcessExecutor executor, Action<TimeSpan> pause, bool stopsOnToolFailure = true)
    {
        DocsTestCommands.Scaffolding.Scaffold(root);
        McpDeclarationFixture.RequireCodeGraph(root);
        if (!stopsOnToolFailure)
        {
            File.AppendAllText(
                Path.Combine(root, ".kyber-weave", "kyber-weave.yml"),
                Environment.NewLine + "stop-on-tool-failure: false" + Environment.NewLine);
        }

        Directory.CreateDirectory(Path.Combine(root, ".codegraph"));
        return ScaffoldedTools(root, () => FakeCodeGraphResolver.WithSymbols(), executor, pause);
    }

    private static string LockPath(string root) => CodeGraphIndexRules.LockPathFor(root, Path.GetTempPath());

    /// <summary>
    /// Clears a lock file a previous run of this machine left for this repository root. The root is a
    /// fresh temp directory, so the path names no repository any other process is working in.
    /// </summary>
    private static void RemoveStaleLock(string lockPath)
    {
        if (File.Exists(lockPath))
        {
            File.Delete(lockPath);
        }
    }

    /// <summary>
    /// Tools over a repository that requires CodeGraph: the scaffold as it comes, plus the CodeGraph
    /// entry it leaves commented out. This is the repository the refresh applies to.
    /// </summary>
    private static DocsTools ToolsRequiringCodeGraph(string root)
    {
        DocsTestCommands.Scaffolding.Scaffold(root);
        McpDeclarationFixture.RequireCodeGraph(root);
        return ScaffoldedTools(root, () => FakeCodeGraphResolver.WithSymbols());
    }

    /// <summary>Tools over the corpus already scaffolded at <paramref name="root"/>.</summary>
    /// <remarks>
    /// The refresh schedule is asked for explicitly. It is opt-in: tools built without one answer
    /// from the documents on disk, so a test that observes the refresh has to be the caller that
    /// requests it. The schedule is handed <see cref="ProvisionSucceeds"/>, so the refresh does not
    /// depend on a CodeGraph binary being installed on the machine running the test.
    /// </remarks>
    private static DocsTools ScaffoldedTools(
        string root,
        Func<ICodeGraphResolver> resolverFactory,
        IProcessExecutor? executor = null,
        Action<TimeSpan>? pause = null)
    {
        KyberWeaveConfig config = ConfigFileReader.Load(root);
        DocumentIndexHost host = new(
            root,
            resolverFactory,
            () => RealDocumentLoader.Create(root, config.Ontology).Load(),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);

        return DocsToolsComposition.Create(
            host,
            null,
            DocsTestCommands.Freshness(root, executor ?? ProvisionSucceeds(), pause: pause));
    }

    /// <summary>
    /// The provisioner's index child, answered the way a working <c>codegraph</c> answers: exit zero,
    /// and nothing on either stream.
    /// </summary>
    /// <remarks>
    /// A successful provision is what lets the refresh go on to build its resolver, which is the step
    /// the two resolver-factory tests pin. Any other child is a refusal rather than a silent success:
    /// the refresh starts the index binary and nothing else, so a command this does not expect means
    /// the refresh grew a step these tests do not know about.
    /// </remarks>
    private static IProcessExecutor ProvisionSucceeds() =>
        new ScriptedProcessExecutor(startInfo =>
        {
            if (Path.GetFileName(startInfo.FileName) != "codegraph")
            {
                throw new InvalidOperationException(
                    $"The refresh started '{startInfo.FileName}', which no test in this class expects.");
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });
}

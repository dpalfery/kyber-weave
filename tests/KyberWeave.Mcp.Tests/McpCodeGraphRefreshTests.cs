using System.Diagnostics;
using System.Text.RegularExpressions;

using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Application.Doctor;
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
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using KyberWeave.Kernel.FileSystem;
using KyberWeave.Tests.Doctor;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Mcp.Tests;

/// <summary>
/// How the docs tools refresh the CodeGraph index before they answer, and what they say when the refresh cannot run:
/// a held index lock is retried and then reported as a note, any other sync failure fails the tool unless the
/// repository opted out, and a lock file left by a running or dead process is waited on and then reported with the
/// fix. The repository, the lock file and the index child are all in memory; the cases whose subject is the real
/// index lock file are in <c>McpCodeGraphRefreshRealLockTests</c> in the integration project.
/// </summary>
public sealed class McpCodeGraphRefreshTests
{
    /// <summary>
    /// How a repository that set <c>stop-on-tool-failure: false</c> is told CodeGraph was unusable:
    /// one line opening with <c>warning:</c>.
    /// </summary>
    private static readonly Regex CodeGraphWarning = new(
        @"(?im)^warning:.*codegraph.*$",
        RegexOptions.CultureInvariant);

    private readonly MemoryDocsHost _host = new();

    private string Root => _host.Repo;

    /// <summary>
    /// When the refresh throws an exception (e.g. resolver construction failure), the failure is
    /// converted to STATUS: TOOL_FAILURE rather than escaping, and the throttle does not bypass the gate.
    /// </summary>
    [Fact]
    public async Task WhenResolverFactoryThrowsDuringRefresh_FailsToolsUnderStopRule()
    {
        await ScaffoldAsync(Root);
        MemoryMcpDeclaration.RequireCodeGraph(_host.Files, Root);

        int resolverCalls = 0;
        DocsTools tools = ScaffoldedTools(
            Root,
            () =>
            {
                resolverCalls++;
                throw new InvalidOperationException("boom resolver failed");
            });

        string first = await tools.ExploreAsync("anything");
        Assert.Contains("STATUS: TOOL_FAILURE", first, StringComparison.Ordinal);
        Assert.Contains("boom resolver failed", first, StringComparison.Ordinal);
        Assert.DoesNotContain("documents=0", first, StringComparison.Ordinal);

        // Subsequent call must not answer Clear as if healthy
        string second = await tools.ExploreAsync("anything");
        Assert.Contains("STATUS: TOOL_FAILURE", second, StringComparison.Ordinal);
        Assert.Contains("boom resolver failed", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the resolver throws and the configuration file is unparseable,
    /// the failure defaults to the stop rule (STATUS: TOOL_FAILURE) rather than fail-open advisory.
    /// </summary>
    [Fact]
    public async Task WhenResolverFactoryThrowsAndConfigUnreadable_DefaultsToStopRule()
    {
        await ScaffoldAsync(Root);
        MemoryMcpDeclaration.RequireCodeGraph(_host.Files, Root);

        DocsTools tools = ScaffoldedTools(
            Root,
            () =>
            {
                _host.Files.WriteAllText(Path.Combine(Root, ".kyber-weave", "kyber-weave.yml"), "unparseable: [yaml: broken");
                throw new InvalidOperationException("boom resolver failed");
            });

        string response = await tools.ExploreAsync("anything");
        Assert.Contains("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.Contains("boom resolver failed", response, StringComparison.Ordinal);
        Assert.DoesNotContain("warning:", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the docs tree is unreadable and CodeGraph fails, the blocked response reports
    /// documents=unavailable rather than conflating failure with an empty corpus (documents=0).
    /// </summary>
    [Fact]
    public async Task WhenDocsTreeUnreadable_BlockedResponseReportsDocumentsUnavailable()
    {
        await ScaffoldAsync(Root);
        MemoryMcpDeclaration.RequireCodeGraph(_host.Files, Root);

        KyberWeaveConfig config = _host.Services().GetRequiredService<IKyberWeaveConfigRepository>().Load(Root);
        DocumentIndexHost host = new(
            Root,
            () => throw new InvalidOperationException("boom resolver"),
            () => throw new IOException("disk error / unreadable docs"),
            _host.Store,
            _host.Paths,
            _host.Environment,
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);

        using CodeGraphFreshness freshness = NewFreshness(Root, ProvisionSucceeds());
        DocsTools tools = DocsToolsComposition.Create(
            host,
            null,
            freshness);

        string response = await tools.ExploreAsync("anything");
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
    /// A sync that finds CodeGraph's own index lock held once is run again, and the answer carries
    /// neither a failure nor a note once the second sync succeeds.
    /// </summary>
    /// <remarks>
    /// Two agents starting in one worktree meet on CodeGraph's lock for a fraction of a second. The
    /// index was healthy throughout, so the caller should not be able to tell the retry happened.
    /// </remarks>
    [Fact]
    public async Task ASyncThatFindsTheIndexLockHeldOnceIsRetriedAndAnswersNormally()
    {
        ScriptedProcessExecutor executor = SyncAnswers(HeldIndexLock, SyncSucceeded);
        List<TimeSpan> pauses = [];
        DocsTools tools = await ToolsWithAnIndexAsync(Root, executor, pauses);

        string response = await tools.ExploreAsync("anything");

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
    public async Task ASyncThatAlwaysFindsTheIndexLockHeldAnswersWithANoteAndNoFailure(bool stopsOnToolFailure)
    {
        ScriptedProcessExecutor executor = SyncAnswers(HeldIndexLock);
        List<TimeSpan> pauses = [];
        DocsTools tools = await ToolsWithAnIndexAsync(Root, executor, pauses, stopsOnToolFailure);

        string response = await tools.ExploreAsync("anything");

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
    public async Task ASyncThatFailsForAnotherReasonStillFailsTheToolWithoutARetry(string error, bool stopsOnToolFailure)
    {
        ScriptedProcessExecutor executor = SyncAnswers(new ProcessResult(1, SyncBanner, error + "\n"));
        List<TimeSpan> pauses = [];
        DocsTools tools = await ToolsWithAnIndexAsync(Root, executor, pauses, stopsOnToolFailure);

        string response = await tools.ExploreAsync("anything");

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
    public async Task WithNoLockFileTheRefreshSyncsAtOnce()
    {
        ScriptedProcessExecutor executor = SyncAnswers(SyncSucceeded);
        List<TimeSpan> waits = [];
        DocsTools tools = await ToolsWithAnIndexAsync(Root, executor, waits);

        _ = await tools.ExploreAsync("anything");

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
    public async Task ALockFileThatGoesAwayDuringAWaitLetsTheSyncRunThen(int waitsBeforeRelease)
    {
        ScriptedProcessExecutor executor = SyncAnswers(SyncSucceeded);
        List<TimeSpan> waits = [];
        DocsTools tools = await ToolsWithAnIndexAsync(Root, executor, (wait, _) =>
        {
            Assert.Empty(executor.Calls);
            waits.Add(wait);
            if (waits.Count == waitsBeforeRelease)
                _host.Files.DeleteFile(IndexLockFile);
            return Task.CompletedTask;
        });
        _host.Files.WriteAllText(IndexLockFile, LivePid);
        _ = _host.Processes.WithAlive(LiveProcessId);

        string response = await tools.ExploreAsync("anything");

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
    public async Task ALockFileThatOutlastsEveryWaitFailsTheToolWithoutSyncing(bool holderIsRunning)
    {
        ScriptedProcessExecutor executor = SyncAnswers(SyncSucceeded);
        List<TimeSpan> waits = [];
        DocsTools tools = await ToolsWithAnIndexAsync(Root, executor, waits);
        string pid = holderIsRunning ? LivePid : DeadPid;
        _host.Files.WriteAllText(IndexLockFile, pid);
        _ = _host.Processes.WithAlive(LiveProcessId, holderIsRunning);

        string response = await tools.ExploreAsync("anything");

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

        _host.Files.DeleteFile(IndexLockFile);
        string next = await tools.ExploreAsync("anything");

        Assert.Single(executor.Calls);
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", next, StringComparison.Ordinal);
    }

    /// <summary>
    /// Under <c>stop-on-tool-failure: false</c> the same outlasting lock file is the warning line and
    /// the answer still comes back, again without a sync.
    /// </summary>
    [Fact]
    public async Task ALockFileThatOutlastsEveryWaitIsAWarningWhenTheRepositoryOptedOut()
    {
        ScriptedProcessExecutor executor = SyncAnswers(SyncSucceeded);
        List<TimeSpan> waits = [];
        DocsTools tools = await ToolsWithAnIndexAsync(Root, executor, waits, stopsOnToolFailure: false);
        _host.Files.WriteAllText(IndexLockFile, LivePid);
        _ = _host.Processes.WithAlive(LiveProcessId);

        string response = await tools.ExploreAsync("anything");

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
    private const int LiveProcessId = 4242;

    private static readonly string LivePid = LiveProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

    private Task<DocsTools> ToolsWithAnIndexAsync(
        string root, IProcessRunner executor, List<TimeSpan> pauses, bool stopsOnToolFailure = true) =>
        ToolsWithAnIndexAsync(root, executor, (wait, _) =>
        {
            pauses.Add(wait);
            return Task.CompletedTask;
        }, stopsOnToolFailure);

    private async Task<DocsTools> ToolsWithAnIndexAsync(
        string root, IProcessRunner executor, Func<TimeSpan, CancellationToken, Task> pause, bool stopsOnToolFailure = true)
    {
        await ScaffoldAsync(root);
        MemoryMcpDeclaration.RequireCodeGraph(_host.Files, root);
        if (!stopsOnToolFailure)
        {
            string configPath = Path.Combine(root, ".kyber-weave", "kyber-weave.yml");
            _host.Files.WriteAllText(
                configPath,
                _host.Files.ReadAllText(configPath) + Environment.NewLine + "stop-on-tool-failure: false" + Environment.NewLine);
        }

        _host.Files.CreateDirectory(Path.Combine(root, ".codegraph"));
        return ScaffoldedTools(root, () => FakeCodeGraphResolver.WithSymbols(), executor, pause);
    }

    /// <summary>The container over this host's in-memory seams, with the doctor's file system and index workspace in memory too.</summary>
    private IServiceProvider Services() =>
        _host.Services(services =>
        {
            services.AddSingleton<IDoctorFileSystem>(new InMemoryDoctorFileSystem(_host.Files));
            services.AddSingleton<ICodeGraphIndexWorkspace>(new FakeCodeGraphIndexWorkspace());
        });

    private Task ScaffoldAsync(string root) =>
        Services().GetRequiredService<IDocsScaffoldingService>().ScaffoldAsync(root);

    private ICodeGraphFreshnessGate Freshness(
        string root,
        IProcessRunner executor,
        Func<TimeSpan, CancellationToken, Task>? pause = null) =>
        NewFreshness(root, executor, pause);

    private CodeGraphFreshness NewFreshness(
        string root,
        IProcessRunner executor,
        Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        IServiceProvider provider = Services();
        return new CodeGraphFreshness(
            root,
            true,
            provider.GetRequiredService<ICodeGraphProvisioner>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IPathResolver>(),
            provider.GetRequiredService<IProcessEnvironment>(),
            provider.GetRequiredService<IProcessRunner>(),
            executor,
            pause);
    }

    private DocsTools ScaffoldedTools(
        string root,
        Func<ICodeGraphResolver> resolverFactory,
        IProcessRunner? executor = null,
        Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        KyberWeaveConfig config = _host.Services().GetRequiredService<IKyberWeaveConfigRepository>().Load(root);
        DocumentIndexHost host = new(
            root,
            resolverFactory,
            () => new DocumentLoader(_host.Store, root, _host.Paths, _host.Environment, config.Ontology).Load(),
            _host.Store,
            _host.Paths,
            _host.Environment,
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);

        return DocsToolsComposition.Create(host, null, Freshness(root, executor ?? ProvisionSucceeds(), pause));
    }

    private static ScriptedProcessExecutor ProvisionSucceeds() =>
        new(startInfo =>
        {
            if (Path.GetFileName(startInfo.FileName) != "codegraph")
            {
                throw new InvalidOperationException(
                    $"The refresh started '{startInfo.FileName}', which no test in this class expects.");
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });
}

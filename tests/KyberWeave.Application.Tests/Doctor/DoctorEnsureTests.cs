using System.Diagnostics;
using System.Reflection;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Core.Doctor;
using KyberWeave.Domain.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests;
using KyberWeave.Tests.Doctor;
using KyberWeave.Tests.Fakes;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Application.Tests.Doctor;

/// <summary>
/// Pins <c>kyber-weave doctor --ensure</c>, the one command every worker runs before it starts work
/// (D22).
/// </summary>
/// <remarks>
/// <para>
/// A worker used to begin by running a read-only check, then a provision, then a query, and each
/// step reported its own failure for the worker to judge. <c>--ensure</c> collapses that into one
/// command whose exit code is the verdict: it syncs or initialises, rewinds anything the
/// initialisation wrote into a tracked file, proves the index answers, and repairs the index once
/// before retrying once. It reads <c>stop-on-tool-failure</c> itself, so no agent decides whether a
/// degraded tool is good enough.
/// </para>
/// <para>
/// The flag does not exist yet, so these cases drive the verb through a real
/// <see cref="CommandApp"/> and assert on what the doctor then did: the argv of the CodeGraph child,
/// the git calls around the initialisation, and the exit code. That is the caller's contract — an
/// argv an operator types and an exit code a script branches on — rather than an internal method, so
/// the implementation is free to arrange the sequence however it likes.
/// </para>
/// </remarks>
public sealed class DoctorEnsureTests
{
    private const string McpExecutable = "kyber-weave-mcp";

    private readonly InMemoryDoctorWorld _world = new();

    private string Root => _world.Root;

    private string IndexDirectory => Path.Combine(Root, ".codegraph");

    /// <summary>
    /// The doctor verb offers <c>--ensure</c>, so a worker packet can name one command that either
    /// leaves the tool working or exits non-zero.
    /// </summary>
    /// <remarks>
    /// D22 names the flag as the worker's first command. Without it an agent has to chain a check, a
    /// provision, and a diagnostic itself, and then decide what a partly-failing chain means.
    /// </remarks>
    [Fact]
    public void TheDoctorVerbDeclaresTheEnsureOption()
    {
        PropertyInfo? option = typeof(DoctorSettings).GetProperties()
            .FirstOrDefault(property => property.GetCustomAttributes<CommandOptionAttribute>()
                .Any(attribute => attribute.LongNames.Contains("ensure", StringComparer.Ordinal)));

        Assert.True(
            option is not null,
            "'kyber-weave doctor' must declare an '--ensure' option: it is the single command a worker "
            + "runs before it starts work (D22).");
    }

    /// <summary>
    /// With an index already on disk, <c>--ensure</c> refreshes it with <c>sync</c> and never
    /// re-initialises it.
    /// </summary>
    /// <remarks>
    /// D22: <c>sync</c> when an index exists, <c>init</c> when it does not. Running <c>init</c> over a
    /// live index discards what is there, and every worker start pays for it.
    /// </remarks>
    [Fact]
    public void EnsureWithAnExistingIndexSyncsAndNeverInitialises()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        int exitCode = RunEnsure(executor);

        Assert.Contains(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("sync", StringComparer.Ordinal));
        DoctorHarness.AssertNoCall(executor, "init");
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// With no index at all, <c>--ensure</c> initialises one at the repository root.
    /// </summary>
    /// <remarks>
    /// D22 and D24: a fresh worktree owns no index, so the first run in it has to create one. The
    /// child runs from the root, which is the worktree, not the canonical checkout.
    /// </remarks>
    [Fact]
    public void EnsureWithNoIndexInitialisesAtTheRoot()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        int exitCode = RunEnsure(executor);

        ProcessStartInfo init = Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
        Assert.Equal(Root, init.WorkingDirectory);
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// A sync that fails is repaired with one <c>codegraph index</c> and retried once, which is
    /// enough for the transient index damage this step exists for.
    /// </summary>
    /// <remarks>
    /// D22: repair once with <c>index</c>, then retry once. A loop would turn a genuinely broken
    /// CodeGraph into a slow hang instead of a stop, and a single attempt would give up on the
    /// common case of an index left stale by an interrupted run.
    /// </remarks>
    [Fact]
    public void EnsureRepairsAFailedSyncOnceWithIndexAndRetriesOnce()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        int syncCalls = 0;
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments =>
            {
                if (arguments.Contains("sync", StringComparer.Ordinal))
                {
                    syncCalls++;
                    return syncCalls == 1
                        ? new ProcessResult(1, string.Empty, "the index is behind")
                        : DoctorHarness.Ok();
                }

                return DoctorHarness.Ok();
            });

        int exitCode = RunEnsure(executor);

        Assert.Equal(
            2,
            executor.CallsTo("codegraph").Count(call => call.ArgumentList.Contains("sync", StringComparer.Ordinal)));
        Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("index", StringComparer.Ordinal));
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// An initialisation that writes into a tracked file is rewound, and a file the operator had
    /// already modified before the run is left exactly as it was.
    /// </summary>
    /// <remarks>
    /// D22: the restore never touches a file that was already dirty. Rewinding the operator's own
    /// work is unrecoverable data loss disguised as housekeeping, so the rule is pinned here beside
    /// the restore it constrains: the run must distinguish the edits it caused from the edits that
    /// were already there.
    /// </remarks>
    [Fact]
    public void EnsureRestoresWhatInitialisationWroteAndLeavesAnAlreadyDirtyFileAlone()
    {
        const string alreadyDirty = "src/already-dirty.cs";
        const string writtenByInit = "src/init-wrote.cs";

        bool initialised = false;
        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);

                // The doctor reads the repository root from git before it does anything else, so a scripted git
                // that answers this with nothing would stop the run before the restore this case exists to pin.
                if (arguments.Contains("rev-parse", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, Root + "\n", string.Empty);
                }

                // The tracked changes before the run, then the same set plus the file init created.
                if (arguments.Contains("status", StringComparison.Ordinal)
                    || arguments.Contains("diff", StringComparison.Ordinal))
                {
                    return new ProcessResult(
                        0,
                        initialised
                            ? $" M {alreadyDirty}\n M {writtenByInit}\n"
                            : $" M {alreadyDirty}\n",
                        string.Empty);
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
            {
                if (startInfo.ArgumentList.Contains("init", StringComparer.Ordinal))
                {
                    initialised = true;
                }

                return DoctorHarness.Ok();
            }

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        RunEnsure(executor);

        string[] rewinds = executor.CallsTo("git")
            .Select(call => string.Join(' ', call.ArgumentList))
            .Where(arguments => arguments.Contains("checkout", StringComparison.Ordinal)
                                || arguments.Contains("restore", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            rewinds.Length > 0,
            $"'doctor --ensure' ran no git restore after initialising the index, so the tracked file "
            + $"'{writtenByInit}' it wrote stays modified in the working tree.");
        Assert.Contains(rewinds, arguments => arguments.Contains(writtenByInit, StringComparison.Ordinal));
        Assert.DoesNotContain(rewinds, arguments => arguments.Contains(alreadyDirty, StringComparison.Ordinal));
    }

    /// <summary>
    /// A failed sync wrote no tracked file either, and neither did the repair that followed it. A file that turned
    /// dirty meanwhile is still someone else's edit.
    /// </summary>
    [Fact]
    public void EnsureLeavesAFileEditedDuringAFailedSyncAlone()
    {
        const string editedMeanwhile = "src/edited-during-failed-sync.cs";
        _world.Files.CreateDirectory(IndexDirectory);

        bool synced = false;
        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);
                if (arguments.Contains("rev-parse", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, Root + "\n", string.Empty);
                }

                if (arguments.Contains("status", StringComparison.Ordinal)
                    || arguments.Contains("diff", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, synced ? $" M {editedMeanwhile}\n" : string.Empty, string.Empty);
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
            {
                if (startInfo.ArgumentList.Contains("sync", StringComparer.Ordinal))
                {
                    synced = true;
                    return new ProcessResult(1, string.Empty, "boom");
                }

                return startInfo.ArgumentList.Contains("index", StringComparer.Ordinal)
                    ? new ProcessResult(1, string.Empty, "boom")
                    : DoctorHarness.Ok();
            }

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        RunEnsure(executor);

        Assert.DoesNotContain(
            executor.CallsTo("git").Select(call => string.Join(' ', call.ArgumentList)),
            arguments => arguments.Contains("checkout", StringComparison.Ordinal)
                         || arguments.Contains("restore", StringComparison.Ordinal));
    }

    /// <summary>
    /// A sync writes no tracked file, so a file that turns dirty while it runs is someone else's edit in the same
    /// tree. Restoring it would revert their work.
    /// </summary>
    [Fact]
    public void EnsureLeavesAFileEditedDuringASyncAlone()
    {
        const string editedMeanwhile = "src/edited-during-sync.cs";
        _world.Files.CreateDirectory(IndexDirectory);

        bool synced = false;
        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);
                if (arguments.Contains("rev-parse", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, Root + "\n", string.Empty);
                }

                if (arguments.Contains("status", StringComparison.Ordinal)
                    || arguments.Contains("diff", StringComparison.Ordinal))
                {
                    return new ProcessResult(0, synced ? $" M {editedMeanwhile}\n" : string.Empty, string.Empty);
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
            {
                if (startInfo.ArgumentList.Contains("sync", StringComparer.Ordinal))
                {
                    synced = true;
                }

                return DoctorHarness.Ok();
            }

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        RunEnsure(executor);

        Assert.Contains(executor.CallsTo("codegraph"), call => call.ArgumentList.Contains("sync", StringComparer.Ordinal));
        Assert.DoesNotContain(
            executor.CallsTo("git").Select(call => string.Join(' ', call.ArgumentList)),
            arguments => arguments.Contains("checkout", StringComparison.Ordinal)
                         || arguments.Contains("restore", StringComparison.Ordinal));
    }

    /// <summary>
    /// When reading baseline tracked changes fails (e.g. git error), ensure must not assume the tree
    /// was clean and must skip restore so operator work is never wiped.
    /// </summary>
    [Fact]
    public void EnsureDoesNotRestoreModifiedFilesWhenBaselineGitStatusFails()
    {
        const string alreadyDirty = "src/already-dirty.cs";
        int statusCalls = 0;

        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);
                if (arguments.Contains("rev-parse", StringComparison.Ordinal))
                    return new ProcessResult(0, Root + "\n", string.Empty);

                if (arguments.Contains("status", StringComparison.Ordinal))
                {
                    statusCalls++;
                    return statusCalls == 1
                        ? new ProcessResult(1, string.Empty, "git error")
                        : new ProcessResult(0, $" M {alreadyDirty}\n", string.Empty);
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
                return DoctorHarness.Ok();

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        RunEnsure(executor);

        string[] rewinds = executor.CallsTo("git")
            .Select(call => string.Join(' ', call.ArgumentList))
            .Where(arguments => arguments.Contains("checkout", StringComparison.Ordinal)
                                || arguments.Contains("restore", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(rewinds, arguments => arguments.Contains(alreadyDirty, StringComparison.Ordinal));
    }

    /// <summary>
    /// When restore fails on a path with spaces, the printed remedy quotes the path so it is a valid
    /// shell command.
    /// </summary>
    [Fact]
    public void EnsureRemedyQuotesPathsWithSpacesWhenRestoreFails()
    {
        const string fileWithSpace = "src/My File.cs";
        bool initialised = false;

        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);
                if (arguments.Contains("rev-parse", StringComparison.Ordinal))
                    return new ProcessResult(0, Root + "\n", string.Empty);

                if (arguments.Contains("status", StringComparison.Ordinal))
                {
                    return new ProcessResult(
                        0,
                        initialised ? $" M {fileWithSpace}\n" : string.Empty,
                        string.Empty);
                }

                if (arguments.Contains("restore", StringComparison.Ordinal))
                {
                    return new ProcessResult(1, string.Empty, "cannot restore");
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
            {
                if (startInfo.ArgumentList.Contains("init", StringComparer.Ordinal))
                    initialised = true;

                return DoctorHarness.Ok();
            }

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        _world.HealthyMcp(Root);
        DoctorReport report = _world.Doctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(McpExecutable),
            executor,
            _ => null,
            _world.Locks);

        DoctorCheck restoreCheck = Assert.Single(report.Checks, check => check.Id == "codegraph.restore");
        Assert.False(restoreCheck.Passed);
        Assert.NotNull(restoreCheck.Fix);
        Assert.Contains($"'{fileWithSpace}'", restoreCheck.Fix, StringComparison.Ordinal);
    }

    /// <summary>
    /// When restore fails on a path with shell metacharacters like '$' or single quotes,
    /// the remedy wraps the path in single quotes with escaped quotes to prevent shell expansion.
    /// </summary>
    [Fact]
    public void EnsureRemedySingleQuotesShellMetacharactersWhenRestoreFails()
    {
        const string evilFile = "src/$evil'quote.cs";
        bool initialised = false;

        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);
                if (arguments.Contains("rev-parse", StringComparison.Ordinal))
                    return new ProcessResult(0, Root + "\n", string.Empty);

                if (arguments.Contains("status", StringComparison.Ordinal))
                {
                    return new ProcessResult(
                        0,
                        initialised ? $" M {evilFile}\n" : string.Empty,
                        string.Empty);
                }

                if (arguments.Contains("restore", StringComparison.Ordinal))
                {
                    return new ProcessResult(1, string.Empty, "cannot restore");
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
            {
                if (startInfo.ArgumentList.Contains("init", StringComparer.Ordinal))
                    initialised = true;

                return DoctorHarness.Ok();
            }

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        _world.HealthyMcp(Root);
        DoctorReport report = _world.Doctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(McpExecutable),
            executor,
            _ => null,
            _world.Locks);

        DoctorCheck restoreCheck = Assert.Single(report.Checks, check => check.Id == "codegraph.restore");
        Assert.False(restoreCheck.Passed);
        Assert.NotNull(restoreCheck.Fix);
        Assert.Contains("'src/$evil'\"'\"'quote.cs'", restoreCheck.Fix, StringComparison.Ordinal);
    }

    /// <summary>
    /// When restore fails on a clean path without spaces or metacharacters,
    /// the remedy emits the path without quotes so Windows and POSIX shells both accept it.
    /// </summary>
    [Fact]
    public void EnsureRemedyDoesNotQuoteCleanPathsWhenRestoreFails()
    {
        const string cleanFile = "src/foo.cs";
        bool initialised = false;

        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);
                if (arguments.Contains("rev-parse", StringComparison.Ordinal))
                    return new ProcessResult(0, Root + "\n", string.Empty);

                if (arguments.Contains("status", StringComparison.Ordinal))
                {
                    return new ProcessResult(
                        0,
                        initialised ? $" M {cleanFile}\n" : string.Empty,
                        string.Empty);
                }

                if (arguments.Contains("restore", StringComparison.Ordinal))
                {
                    return new ProcessResult(1, string.Empty, "cannot restore");
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
            {
                if (startInfo.ArgumentList.Contains("init", StringComparer.Ordinal))
                    initialised = true;

                return DoctorHarness.Ok();
            }

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        _world.HealthyMcp(Root);
        DoctorReport report = _world.Doctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(McpExecutable),
            executor,
            _ => null,
            _world.Locks);

        DoctorCheck restoreCheck = Assert.Single(report.Checks, check => check.Id == "codegraph.restore");
        Assert.False(restoreCheck.Passed);
        Assert.NotNull(restoreCheck.Fix);
        Assert.Contains($"-- {cleanFile}", restoreCheck.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain($"'{cleanFile}'", restoreCheck.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{cleanFile}\"", restoreCheck.Fix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clean paths without spaces or shell metacharacters are left unquoted across all platforms,
    /// while paths with spaces or metacharacters use single-quote wrapping on POSIX and double-quote
    /// wrapping on Windows (so cmd.exe does not treat single quotes as literal characters).
    /// </summary>
    [Theory]
    [InlineData("src/foo.cs", "src/foo.cs")]
    [InlineData("src/dir/bar.cs", "src/dir/bar.cs")]
    [InlineData("src/My File.cs", "'src/My File.cs'")]
    [InlineData("src/$evil.cs", "'src/$evil.cs'")]
    [InlineData("src/$evil'quote.cs", "'src/$evil'\"'\"'quote.cs'")]
    [InlineData("src/`cmd`.cs", "'src/`cmd`.cs'")]
    public void QuoteShellPath_QuotesOnlyWhenNecessary_UsingSingleQuotesOnPosix(string input, string expected)
    {
        Assert.Equal(expected, CodeGraphIndexRules.QuoteShellPath(input, isWindows: false));
    }

    [Theory]
    [InlineData("src/foo.cs", "src/foo.cs")]
    [InlineData("src/dir/bar.cs", "src/dir/bar.cs")]
    [InlineData("src/My File.cs", "\"src/My File.cs\"")]
    [InlineData("src/My\"File.cs", "\"src/My\\\"File.cs\"")]
    [InlineData("src/$evil.cs", "\"src/$evil.cs\"")]
    public void QuoteShellPath_QuotesOnlyWhenNecessary_UsingDoubleQuotesOnWindows(string input, string expected)
    {
        Assert.Equal(expected, CodeGraphIndexRules.QuoteShellPath(input, isWindows: true));
    }

    [Fact]
    public void EnsureDoesNotRepairAnIndexWhoseLockIsHeldAndPassesWithAWarning()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("sync", StringComparer.Ordinal) ? HeldIndexLock : DoctorHarness.Ok());

        DoctorReport report = RunEnsureWithoutWaiting(executor);

        DoctorHarness.AssertNoCall(executor, "index");
        Assert.Equal(3, CodeGraphCalls(executor, "sync"));
        Assert.Equal(1, CodeGraphCalls(executor, "explore"));
        DoctorCheck index = Assert.Single(report.Checks, check => check.Id == "codegraph.index");
        Assert.True(index.Passed);
        Assert.StartsWith("warning: ", index.Reason, StringComparison.Ordinal);
        Assert.Contains("lock held", index.Reason, StringComparison.Ordinal);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// A sync that finds the lock held once and free on the retry is an ordinary pass: no repair and
    /// no warning.
    /// </summary>
    [Fact]
    public void EnsureRetriesASyncThatFoundTheLockHeldAndPassesNormally()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        int syncCalls = 0;
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("sync", StringComparer.Ordinal) && ++syncCalls == 1
                ? HeldIndexLock
                : DoctorHarness.Ok());

        DoctorReport report = RunEnsureWithoutWaiting(executor);

        DoctorHarness.AssertNoCall(executor, "index");
        Assert.Equal(2, CodeGraphCalls(executor, "sync"));
        DoctorCheck index = Assert.Single(report.Checks, check => check.Id == "codegraph.index");
        Assert.Equal("codegraph sync completed", index.Reason);
        Assert.Equal(0, report.ExitCode);
    }

    private static readonly ProcessResult HeldIndexLock = new(
        1,
        "┌  Syncing CodeGraph\n│\n",
        "✗ Failed to sync: Sync could not acquire the file lock; retry when the index is available. "
        + "CodeGraph database is locked by another process (PID 91106). If this is stale, run "
        + "'codegraph unlock' or delete /repo/.codegraph/codegraph.lock\n");

    private static int CodeGraphCalls(ScriptedProcessExecutor executor, string verb) =>
        executor.CallsTo("codegraph").Count(call => call.ArgumentList.Contains(verb, StringComparer.Ordinal));

    /// <summary>Runs the ensure doctor with the pauses between sync attempts set to nothing.</summary>
    private DoctorReport RunEnsureWithoutWaiting(ScriptedProcessExecutor executor)
    {
        _world.HealthyMcp(Root);
        return _world.Doctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(McpExecutable) with
            {
                HeldLockRetryPauses = [TimeSpan.Zero, TimeSpan.Zero]
            },
            executor,
            _ => null,
            _world.Locks);
    }

    private int RunEnsure(ScriptedProcessExecutor executor)
    {
        _world.HealthyMcp(Root);
        DoctorCommand command = _world.Command(false, executor, McpExecutable);
        return DoctorAppHarness.Run(command, "doctor", "--ensure", Root);
    }
}

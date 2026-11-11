using System.Diagnostics;
using System.Reflection;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

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
public sealed class DoctorEnsureTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly TempDirectory _stubs = new();

    public void Dispose()
    {
        _stubs.Dispose();
        _root.Dispose();
    }

    private string Root => _root.Path;

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
        Directory.CreateDirectory(IndexDirectory);
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
        Directory.CreateDirectory(IndexDirectory);
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
        Directory.CreateDirectory(IndexDirectory);

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
        Directory.CreateDirectory(IndexDirectory);

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

        DoctorCommand command = new()
        {
            Executor = executor,
            IsTerminal = () => false,
            ReadEnvironment = _ => null,
            McpExecutable = DoctorHarness.HealthyStub(_stubs.Path, Root)
        };

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(command.McpExecutable),
            executor,
            _ => null);

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

        DoctorCommand command = new()
        {
            Executor = executor,
            IsTerminal = () => false,
            ReadEnvironment = _ => null,
            McpExecutable = DoctorHarness.HealthyStub(_stubs.Path, Root)
        };

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(command.McpExecutable),
            executor,
            _ => null);

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

        DoctorCommand command = new()
        {
            Executor = executor,
            IsTerminal = () => false,
            ReadEnvironment = _ => null,
            McpExecutable = DoctorHarness.HealthyStub(_stubs.Path, Root)
        };

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(command.McpExecutable),
            executor,
            _ => null);

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
        Assert.Equal(expected, CodeGraphProvisioner.QuoteShellPath(input, isWindows: false));
    }

    [Theory]
    [InlineData("src/foo.cs", "src/foo.cs")]
    [InlineData("src/dir/bar.cs", "src/dir/bar.cs")]
    [InlineData("src/My File.cs", "\"src/My File.cs\"")]
    [InlineData("src/My\"File.cs", "\"src/My\\\"File.cs\"")]
    [InlineData("src/$evil.cs", "\"src/$evil.cs\"")]
    public void QuoteShellPath_QuotesOnlyWhenNecessary_UsingDoubleQuotesOnWindows(string input, string expected)
    {
        Assert.Equal(expected, CodeGraphProvisioner.QuoteShellPath(input, isWindows: true));
    }

    /// <summary>
    /// Baseline tracked changes must be sampled AFTER lock acquisition so edits made while waiting
    /// are not classified as newly written by the index preparation and restored.
    /// </summary>
    [Fact]
    public void EnsureSamplesBaselineTrackedChangesAfterLockAcquisition()
    {
        string lockPath = CodeGraphProvisioner.LockPathFor(Root, Path.GetTempPath());
        bool lockHeldDuringBaselineStatus = false;
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
                    if (statusCalls == 1)
                    {
                        if (File.Exists(lockPath))
                        {
                            try
                            {
                                using FileStream testOpen = new(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                                lockHeldDuringBaselineStatus = false;
                            }
                            catch (IOException)
                            {
                                lockHeldDuringBaselineStatus = true;
                            }
                        }
                    }

                    return new ProcessResult(0, string.Empty, string.Empty);
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
                return DoctorHarness.Ok();

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        RunEnsure(executor);

        Assert.True(
            lockHeldDuringBaselineStatus,
            "Baseline tracked changes must be sampled AFTER lock acquisition so edits made while waiting are not lost.");
    }

    /// <summary>
    /// Runs <c>doctor --ensure &lt;root&gt;</c> through the real CLI parser with the scripted child
    /// executor, and returns its exit code.
    /// </summary>
    private int RunEnsure(ScriptedProcessExecutor executor)
    {
        DoctorCommand command = new()
        {
            Executor = executor,
            IsTerminal = () => false,
            ReadEnvironment = _ => null,
            McpExecutable = DoctorHarness.HealthyStub(_stubs.Path, Root)
        };

        return DoctorAppHarness.Run(command, "doctor", "--ensure", Root);
    }
}

/// <summary>
/// The in-process <c>doctor</c> boundary: the real parser, strict so an option the command does not
/// declare is refused rather than ignored, with the tested <see cref="DoctorCommand"/> instance
/// supplied in place of the one the default resolver would construct.
/// </summary>
/// <remarks>
/// Strict parsing is the point. A lax app silently ignores an option it does not know, so a RED case
/// would quietly run a different verb and the failure would name the wrong cause.
/// </remarks>
internal static class DoctorAppHarness
{
    public static int Run(DoctorCommand command, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(args);

        CommandApp app = new(new DoctorCommandRegistrar(command));
        app.Configure(configuration =>
        {
            configuration.SetApplicationName("kyber-weave");
            configuration.UseStrictParsing();
            configuration.AddCommand<DoctorCommand>("doctor");
        });

        return ProcessConsoleCapture.Run(() => app.Run(args)).Result;
    }

    private sealed class DoctorCommandRegistrar(DoctorCommand command) : ITypeRegistrar
    {
        private readonly Dictionary<Type, object> _instances = new()
        {
            [typeof(DoctorCommand)] = command,
            [typeof(IAnsiConsole)] = AnsiConsole.Console
        };

        private readonly Dictionary<Type, Func<object>> _factories = [];

        public void Register(Type service, Type implementation) =>
            _factories.TryAdd(service, () => Activator.CreateInstance(implementation)!);

        public void RegisterInstance(Type service, object instance) => _instances[service] = instance;

        public void RegisterLazy(Type service, Func<object> factory) => _factories.TryAdd(service, factory);

        public ITypeResolver Build() => new DoctorCommandResolver(_instances, _factories);
    }

    private sealed class DoctorCommandResolver(
        Dictionary<Type, object> instances,
        Dictionary<Type, Func<object>> factories) : ITypeResolver
    {
        public object? Resolve(Type? type)
        {
            if (type is null)
            {
                return null;
            }

            if (instances.TryGetValue(type, out object? instance))
            {
                return instance;
            }

            if (factories.TryGetValue(type, out Func<object>? factory))
            {
                try
                {
                    object created = factory();
                    instances[type] = created;
                    return created;
                }
                catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or TargetInvocationException)
                {
                    // A registration this harness cannot satisfy is left unresolved, exactly as the
                    // default resolver leaves a type it has no registration for.
                    return null;
                }
            }

            // Spectre asks for every extension point it supports as an IEnumerable<T> and iterates the
            // answer while it composes the command tree. A null answer throws there, before Execute is
            // ever reached, and app.Run reports that as -1 — a value indistinguishable from the verb
            // refusing the call, which is why a harness that answered null made every case read as
            // "the option does not exist". The harness registers no extension point, so the honest
            // answer is an empty sequence rather than null.
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return Array.CreateInstance(type.GetGenericArguments()[0], 0);
            }

            if (!type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                return Activator.CreateInstance(type);
            }

            return null;
        }
    }
}

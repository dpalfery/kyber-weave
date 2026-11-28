using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The docs server's CodeGraph refresh decided from the file, path, process, environment and time seams alone:
/// no disk, no child process and no real waiting.
/// </summary>
public sealed class CodeGraphFreshnessTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFileSystem _files;
    private readonly FakePathResolver _paths;
    private readonly FakeKyberWeaveConfigRepository _config = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly RecordingProvisioner _provisioner = new();
    private readonly List<TimeSpan> _pauses = [];

    public CodeGraphFreshnessTests()
    {
        _files = new InMemoryFileSystem(_time);
        _paths = new FakePathResolver(_files);
        DeclareCodeGraph(Root, stopOnToolFailure: true);
    }

    private string Root => Path.Combine(_files.Root, "kw", "repo");

    private string Temp => Path.Combine(_files.Root, "kw", "temp");

    private string Work => Path.Combine(_files.Root, "kw", "work");

    [Fact]
    public async Task FirstCallProvisionsTheResolvedRootUnderTheTemporaryDirectory()
    {
        using CodeGraphFreshness gate = Gate("repo", currentDirectory: Path.GetDirectoryName(Root)!);

        CodeGraphGate verdict = await gate.EvaluateAsync(Resolver());

        Assert.Equal(CodeGraphGate.Clear, verdict);
        ProvisionCall call = Assert.Single(_provisioner.Calls);
        Assert.Equal(Root, call.Root);
        Assert.Equal(Temp, call.LockDirectory);
    }

    [Fact]
    public async Task SecondCallInsideTheIntervalReusesTheVerdictWithoutProvisioning()
    {
        using CodeGraphFreshness gate = Gate();
        _ = await gate.EvaluateAsync(Resolver());

        _time.Advance(TimeSpan.FromSeconds(59));
        _ = await gate.EvaluateAsync(Resolver());
        Assert.Single(_provisioner.Calls);

        _time.Advance(TimeSpan.FromSeconds(2));
        _ = await gate.EvaluateAsync(Resolver());
        Assert.Equal(2, _provisioner.Calls.Count);
    }

    [Fact]
    public async Task DisabledRefreshAndUndeclaredCodeGraphNeverProvision()
    {
        using CodeGraphFreshness disabled = Gate(refreshEnabled: false);
        Assert.Equal(CodeGraphGate.Clear, await disabled.EvaluateAsync(Resolver()));

        FakeKyberWeaveConfigRepository none = new();
        none.WriteRepositoryConfig(Root, "stop-on-tool-failure: true\n");
        using CodeGraphFreshness undeclared = new(
            Root, true, _provisioner, none, _time, _files, _paths, new FakeProcessEnvironment(Work, tempDirectory: Temp),
            _processes, pause: (wait, _) =>
            {
                _pauses.Add(wait);
                return Task.CompletedTask;
            });

        Assert.Equal(CodeGraphGate.Clear, await undeclared.EvaluateAsync(Resolver()));
        Assert.Empty(_provisioner.Calls);
    }

    [Fact]
    public async Task LockFileThatNeverClearsFailsAfterEveryWaitAndNamesTheRunningHolder()
    {
        WriteLockFile("4242\n");
        _ = _processes.WithAlive(4242);

        using CodeGraphFreshness gate = Gate();
        CodeGraphGate verdict = await gate.EvaluateAsync(Resolver());

        Assert.Equal(CodeGraphIndexRules.LockFileWaits, _pauses);
        Assert.Empty(_provisioner.Calls);
        Assert.NotNull(verdict.Failure);
        Assert.Contains("still held after 36 seconds", verdict.Failure, StringComparison.Ordinal);
        Assert.Contains("it records PID 4242, which is running", verdict.Failure, StringComparison.Ordinal);
        Assert.Contains("fix: wait for process 4242 to finish its index work, or stop it", verdict.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockFileHeldByAnEndedProcessSuggestsUnlockingWithThePlatformsQuoting()
    {
        WriteLockFile("4242");
        _ = _processes.WithAlive(4242, alive: false);

        using CodeGraphFreshness posixGate = Gate(operatingSystem: OperatingSystemKind.Linux);
        using CodeGraphFreshness windowsGate = Gate(operatingSystem: OperatingSystemKind.Windows);
        CodeGraphGate posix = await posixGate.EvaluateAsync(Resolver());
        CodeGraphGate windows = await windowsGate.EvaluateAsync(Resolver());

        Assert.Contains("which is not running", posix.Failure, StringComparison.Ordinal);
        Assert.Contains($"fix: codegraph unlock {Root}", posix.Failure, StringComparison.Ordinal);
        Assert.Contains($"fix: codegraph unlock {Root}", windows.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockFileOnARootWithASpaceIsQuotedForTheShellOfTheHostPlatform()
    {
        string spaced = Path.Combine(_files.Root, "kw", "my repo");
        Assert.StartsWith(_files.Root, spaced, StringComparison.Ordinal);
        DeclareCodeGraph(spaced, stopOnToolFailure: true);
        WriteLockFileAt(spaced, "no number");

        using CodeGraphFreshness linuxGate = Gate(spaced, operatingSystem: OperatingSystemKind.Linux);
        using CodeGraphFreshness windowsGate = Gate(spaced, operatingSystem: OperatingSystemKind.Windows);
        CodeGraphGate linux = await linuxGate.EvaluateAsync(Resolver());
        CodeGraphGate windows = await windowsGate.EvaluateAsync(Resolver());

        Assert.Contains("it records no PID", linux.Failure, StringComparison.Ordinal);
        Assert.Contains($"fix: codegraph unlock '{spaced}'", linux.Failure, StringComparison.Ordinal);
        Assert.Contains($"fix: codegraph unlock \"{spaced}\"", windows.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessLookupThatIsRefusedCountsAsRunning()
    {
        WriteLockFile("4242");
        RefusingProcessRunner refusing = new();
        using CodeGraphFreshness gate = new(
            Root, true, _provisioner, _config, _time, _files, _paths, new FakeProcessEnvironment(Work, tempDirectory: Temp),
            refusing, pause: (wait, _) =>
            {
                _pauses.Add(wait);
                return Task.CompletedTask;
            });

        CodeGraphGate verdict = await gate.EvaluateAsync(Resolver());

        Assert.Contains("which is running", verdict.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockFileThatClearsDuringTheWaitsLetsTheRefreshProceed()
    {
        WriteLockFile("4242");
        using CodeGraphFreshness gate = Gate(pause: (wait, _) =>
        {
            _pauses.Add(wait);
            _files.DeleteFile(Path.Combine(Root, ".codegraph", "codegraph.lock"));
            return Task.CompletedTask;
        });

        CodeGraphGate verdict = await gate.EvaluateAsync(Resolver());

        Assert.Equal(CodeGraphGate.Clear, verdict);
        Assert.Equal([TimeSpan.FromSeconds(1)], _pauses);
        Assert.Single(_provisioner.Calls);
    }

    [Fact]
    public async Task DefaultPauseWaitsOnTheInjectedClockNotTheWallClock()
    {
        WriteLockFile("4242");
        using CodeGraphFreshness gate = new(
            Root, true, _provisioner, _config, _time, _files, _paths, new FakeProcessEnvironment(Work, tempDirectory: Temp),
            _processes);

        Task<CodeGraphGate> evaluation = gate.EvaluateAsync(Resolver());
        int advanced = 0;
        while (advanced < 120 && await Task.WhenAny(evaluation, Task.Delay(5, TestContext.Current.CancellationToken)) != evaluation)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            advanced++;
        }

        CodeGraphGate verdict = await evaluation;
        Assert.InRange(advanced, 36, 120);
        Assert.Contains("still held after 36 seconds", verdict.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultExecutorRunsTheChildThroughTheBoundedProcessSeamWithTheRefreshBound()
    {
        _ = _processes.Script("codegraph", new ProcessRunResult(0, string.Empty, TimedOut: false) { StandardOutput = string.Empty, StandardError = string.Empty });
        _provisioner.OnProvision = async (_, options, executor, _, cancellationToken) =>
        {
            System.Diagnostics.ProcessStartInfo start = new("codegraph")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _ = await executor.RunAsync(start, string.Empty, options.IndexTimeout, cancellationToken);
            return new DoctorCheck("codegraph.index", true, null, null);
        };

        using CodeGraphFreshness gate = Gate();
        _ = await gate.EvaluateAsync(Resolver());

        ProcessRunRequest request = Assert.Single(_processes.Calls);
        Assert.Equal("codegraph", request.Program);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
    }

    [Fact]
    public async Task ProvisioningFailureBlocksUnderTheStopRuleAndWarnsWithoutIt()
    {
        _provisioner.OnProvision = (_, _, _, _, _) => Task.FromResult(new DoctorCheck("codegraph.index", false, "codegraph is not installed", "install codegraph"));

        using CodeGraphFreshness blockedGate = Gate();
        CodeGraphGate blocked = await blockedGate.EvaluateAsync(Resolver());
        Assert.StartsWith("STATUS: TOOL_FAILURE", blocked.Failure, StringComparison.Ordinal);
        Assert.Contains("reason: codegraph is not installed", blocked.Failure, StringComparison.Ordinal);

        DeclareCodeGraph(Root, stopOnToolFailure: false);
        using CodeGraphFreshness warnedGate = Gate();
        CodeGraphGate warned = await warnedGate.EvaluateAsync(Resolver());
        Assert.Null(warned.Failure);
        Assert.StartsWith("warning: codegraph is unavailable: codegraph is not installed", warned.Note, StringComparison.Ordinal);
    }

    private static Func<ICodeGraphResolver> Resolver() => () => FakeCodeGraphResolver.WithSymbols();

    private CodeGraphFreshness Gate(
        string? root = null,
        bool refreshEnabled = true,
        string? currentDirectory = null,
        OperatingSystemKind operatingSystem = OperatingSystemKind.Linux,
        Func<TimeSpan, CancellationToken, Task>? pause = null) =>
        new(
            root ?? Root,
            refreshEnabled,
            _provisioner,
            _config,
            _time,
            _files,
            _paths,
            new FakeProcessEnvironment(currentDirectory ?? Work, tempDirectory: Temp, operatingSystem: operatingSystem),
            _processes,
            pause: pause ?? ((wait, _) =>
            {
                _pauses.Add(wait);
                return Task.CompletedTask;
            }));

    private void WriteLockFile(string contents) => WriteLockFileAt(Root, contents);

    private void WriteLockFileAt(string root, string contents)
    {
        _files.CreateDirectory(Path.Combine(root, ".codegraph"));
        _files.WriteAllText(Path.Combine(root, ".codegraph", "codegraph.lock"), contents);
    }

    private void DeclareCodeGraph(string root, bool stopOnToolFailure) =>
        _config.WriteRepositoryConfig(
            root,
            $"stop-on-tool-failure: {(stopOnToolFailure ? "true" : "false")}\n"
            + "mcp:\n"
            + "  required-servers:\n"
            + "    codegraph:\n"
            + "      command: [codegraph, serve, --mcp]\n"
            + "      provenance: third-party\n"
            + "      level: required\n");

    private sealed record ProvisionCall(string Root, string LockDirectory);

    private sealed class RecordingProvisioner : ICodeGraphProvisioner
    {
        public List<ProvisionCall> Calls { get; } = [];

        public Func<string, DoctorOptions, IProcessRunner, string, CancellationToken, Task<DoctorCheck>>? OnProvision { get; set; }

        public CodeGraphOutcome Run(
            string root,
            DoctorMode mode,
            DoctorOptions options,
            IProcessExecutor executor,
            string lockDirectory,
            CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles) =>
            throw new NotSupportedException("The refresh uses Provision only.");

        public Task<CodeGraphOutcome> RunAsync(
            string root,
            DoctorMode mode,
            DoctorOptions options,
            IProcessRunner executor,
            string lockDirectory,
            CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The refresh uses Provision only.");

        public Task<DoctorCheck> ProvisionAsync(
            string root,
            DoctorOptions options,
            IProcessRunner executor,
            string lockDirectory,
            TimeSpan lockWait,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ProvisionCall(root, lockDirectory));
            if (OnProvision is null)
                return Task.FromResult(new DoctorCheck("codegraph.index", true, null, null));
            return OnProvision(root, options, executor, lockDirectory, cancellationToken);
        }
    }

    private sealed class RefusingProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool IsAlive(int processId) => throw new UnauthorizedAccessException("The lookup was refused.");

        public string? FindOnPath(string program) => null;
    }
}

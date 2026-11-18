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
    public void FirstCallProvisionsTheResolvedRootUnderTheTemporaryDirectory()
    {
        CodeGraphFreshness gate = Gate("repo", currentDirectory: Path.GetDirectoryName(Root)!);

        CodeGraphGate verdict = gate.Evaluate(Resolver());

        Assert.Equal(CodeGraphGate.Clear, verdict);
        ProvisionCall call = Assert.Single(_provisioner.Calls);
        Assert.Equal(Root, call.Root);
        Assert.Equal(Temp, call.LockDirectory);
    }

    [Fact]
    public void SecondCallInsideTheIntervalReusesTheVerdictWithoutProvisioning()
    {
        CodeGraphFreshness gate = Gate();
        _ = gate.Evaluate(Resolver());

        _time.Advance(TimeSpan.FromSeconds(59));
        _ = gate.Evaluate(Resolver());
        Assert.Single(_provisioner.Calls);

        _time.Advance(TimeSpan.FromSeconds(2));
        _ = gate.Evaluate(Resolver());
        Assert.Equal(2, _provisioner.Calls.Count);
    }

    [Fact]
    public void DisabledRefreshAndUndeclaredCodeGraphNeverProvision()
    {
        Assert.Equal(CodeGraphGate.Clear, Gate(refreshEnabled: false).Evaluate(Resolver()));

        FakeKyberWeaveConfigRepository none = new();
        none.WriteRepositoryConfig(Root, "stop-on-tool-failure: true\n");
        CodeGraphFreshness undeclared = new(
            Root, true, _provisioner, none, _time, _files, _paths, new FakeProcessEnvironment(Work, tempDirectory: Temp),
            _processes, _processes, pause: _pauses.Add);

        Assert.Equal(CodeGraphGate.Clear, undeclared.Evaluate(Resolver()));
        Assert.Empty(_provisioner.Calls);
    }

    [Fact]
    public void LockFileThatNeverClearsFailsAfterEveryWaitAndNamesTheRunningHolder()
    {
        WriteLockFile("4242\n");
        _ = _processes.WithAlive(4242);

        CodeGraphGate verdict = Gate().Evaluate(Resolver());

        Assert.Equal(CodeGraphIndexRules.LockFileWaits, _pauses);
        Assert.Empty(_provisioner.Calls);
        Assert.NotNull(verdict.Failure);
        Assert.Contains("still held after 36 seconds", verdict.Failure, StringComparison.Ordinal);
        Assert.Contains("it records PID 4242, which is running", verdict.Failure, StringComparison.Ordinal);
        Assert.Contains("fix: wait for process 4242 to finish its index work, or stop it", verdict.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void LockFileHeldByAnEndedProcessSuggestsUnlockingWithThePlatformsQuoting()
    {
        WriteLockFile("4242");
        _ = _processes.WithAlive(4242, alive: false);

        CodeGraphGate posix = Gate(operatingSystem: OperatingSystemKind.Linux).Evaluate(Resolver());
        CodeGraphGate windows = Gate(operatingSystem: OperatingSystemKind.Windows).Evaluate(Resolver());

        Assert.Contains("which is not running", posix.Failure, StringComparison.Ordinal);
        Assert.Contains($"fix: codegraph unlock {Root}", posix.Failure, StringComparison.Ordinal);
        Assert.Contains($"fix: codegraph unlock {Root}", windows.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void LockFileOnARootWithASpaceIsQuotedForTheShellOfTheHostPlatform()
    {
        string spaced = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "kw", "my repo"));
        DeclareCodeGraph(spaced, stopOnToolFailure: true);
        WriteLockFileAt(spaced, "no number");

        CodeGraphGate linux = Gate(spaced, operatingSystem: OperatingSystemKind.Linux).Evaluate(Resolver());
        CodeGraphGate windows = Gate(spaced, operatingSystem: OperatingSystemKind.Windows).Evaluate(Resolver());

        Assert.Contains("it records no PID", linux.Failure, StringComparison.Ordinal);
        Assert.Contains($"fix: codegraph unlock '{spaced}'", linux.Failure, StringComparison.Ordinal);
        Assert.Contains($"fix: codegraph unlock \"{spaced}\"", windows.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessLookupThatIsRefusedCountsAsRunning()
    {
        WriteLockFile("4242");
        RefusingProcessRunner refusing = new();
        CodeGraphFreshness gate = new(
            Root, true, _provisioner, _config, _time, _files, _paths, new FakeProcessEnvironment(Work, tempDirectory: Temp),
            refusing, _processes, pause: _pauses.Add);

        CodeGraphGate verdict = gate.Evaluate(Resolver());

        Assert.Contains("which is running", verdict.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void LockFileThatClearsDuringTheWaitsLetsTheRefreshProceed()
    {
        WriteLockFile("4242");
        CodeGraphFreshness gate = Gate(pause: wait =>
        {
            _pauses.Add(wait);
            _files.DeleteFile(Path.Combine(Root, ".codegraph", "codegraph.lock"));
        });

        CodeGraphGate verdict = gate.Evaluate(Resolver());

        Assert.Equal(CodeGraphGate.Clear, verdict);
        Assert.Equal([TimeSpan.FromSeconds(1)], _pauses);
        Assert.Single(_provisioner.Calls);
    }

    [Fact]
    public async Task DefaultPauseWaitsOnTheInjectedClockNotTheWallClock()
    {
        WriteLockFile("4242");
        CodeGraphFreshness gate = new(
            Root, true, _provisioner, _config, _time, _files, _paths, new FakeProcessEnvironment(Work, tempDirectory: Temp),
            _processes, _processes);

        Task<CodeGraphGate> evaluation = Task.Run(() => gate.Evaluate(Resolver()));
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
    public void DefaultExecutorRunsTheChildThroughTheBoundedProcessSeamWithTheRefreshBound()
    {
        _ = _processes.Script("codegraph", new ProcessRunResult(0, string.Empty, TimedOut: false) { StandardOutput = string.Empty, StandardError = string.Empty });
        _provisioner.OnProvision = (_, _, executor, _) =>
        {
            System.Diagnostics.ProcessStartInfo start = new("codegraph")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _ = executor.Run(start, string.Empty);
            return new DoctorCheck("codegraph.index", true, null, null);
        };

        _ = Gate().Evaluate(Resolver());

        ProcessRunRequest request = Assert.Single(_processes.Calls);
        Assert.Equal("codegraph", request.Program);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
    }

    [Fact]
    public void ProvisioningFailureBlocksUnderTheStopRuleAndWarnsWithoutIt()
    {
        _provisioner.OnProvision = (_, _, _, _) => new DoctorCheck("codegraph.index", false, "codegraph is not installed", "install codegraph");

        CodeGraphGate blocked = Gate().Evaluate(Resolver());
        Assert.StartsWith("STATUS: TOOL_FAILURE", blocked.Failure, StringComparison.Ordinal);
        Assert.Contains("reason: codegraph is not installed", blocked.Failure, StringComparison.Ordinal);

        DeclareCodeGraph(Root, stopOnToolFailure: false);
        CodeGraphGate warned = Gate().Evaluate(Resolver());
        Assert.Null(warned.Failure);
        Assert.StartsWith("warning: codegraph is unavailable: codegraph is not installed", warned.Note, StringComparison.Ordinal);
    }

    private static Func<ICodeGraphResolver> Resolver() => () => FakeCodeGraphResolver.WithSymbols();

    private CodeGraphFreshness Gate(
        string? root = null,
        bool refreshEnabled = true,
        string? currentDirectory = null,
        OperatingSystemKind operatingSystem = OperatingSystemKind.Linux,
        Action<TimeSpan>? pause = null) =>
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
            _processes,
            pause: pause ?? _pauses.Add);

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

        public Func<string, DoctorOptions, IProcessExecutor, string, DoctorCheck>? OnProvision { get; set; }

        public CodeGraphOutcome Run(
            string root,
            DoctorMode mode,
            DoctorOptions options,
            IProcessExecutor executor,
            string lockDirectory,
            CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles) =>
            throw new NotSupportedException("The refresh uses Provision only.");

        public DoctorCheck Provision(
            string root,
            DoctorOptions options,
            IProcessExecutor executor,
            string lockDirectory,
            TimeSpan lockWait)
        {
            Calls.Add(new ProvisionCall(root, lockDirectory));
            return OnProvision?.Invoke(root, options, executor, lockDirectory)
                ?? new DoctorCheck("codegraph.index", true, null, null);
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

using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The gate runner over the shared fakes: the council deadline, each gate's own timeout and its recorded duration all
/// follow the time provider, and the paths a gate names resolve against the repository root, with nothing started and
/// no clock waited on.
/// </summary>
public sealed class GateRunServiceTests
{
    private const string Root = "/repo";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFileSystem _files;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: Root);
    private readonly RecordingPrograms _programs;
    private readonly GateRunService _service;

    public GateRunServiceTests()
    {
        _files = new InMemoryFileSystem(_clock);
        _files.CreateDirectory(Root);
        _programs = new RecordingPrograms(_clock);
        _service = new GateRunService(
            _programs,
            new NoCoverage(),
            _files,
            _clock,
            FakePathResolver.OverEmptyFileSystem(),
            _environment);
    }

    [Fact]
    public void AGatesDurationIsTheTimeTheClockAdvancedWhileItRan()
    {
        _programs.Takes("build", TimeSpan.FromMilliseconds(1500));

        GateReport report = _service.Run(Config(Gate("build")), Root);

        Assert.Equal(1500, Assert.Single(report.Gates).DurationMilliseconds);
    }

    [Fact]
    public void AGateTheDeadlinePassedBeforeItStartsIsReportedWithoutRunningIt()
    {
        _programs.Takes("first", TimeSpan.FromSeconds(6));

        GateReport report = _service.Run(Config(Gate("first"), Gate("second")), Root, deadline: TimeSpan.FromSeconds(5));

        Assert.Equal(GateOutcome.Completed, report.Gates[0].Outcome);
        Assert.Equal(GateOutcome.DeadlinePassed, report.Gates[1].Outcome);
        Assert.Equal(["first"], _programs.Started);
    }

    [Fact]
    public void EachGateRunsForTheShorterOfItsOwnTimeoutAndTheTimeLeft()
    {
        _programs.Takes("first", TimeSpan.FromSeconds(4));
        _programs.Takes("second", TimeSpan.FromSeconds(1));

        _ = _service.Run(Config(Gate("first", timeoutSeconds: 60), Gate("second", timeoutSeconds: 3)), Root, deadline: TimeSpan.FromSeconds(10));

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)], _programs.Timeouts);
    }

    [Fact]
    public void AGateCutAtTheDeadlineIsReportedAsDeadlinePassedWithTheTimeItRan()
    {
        _programs.TimesOut("slow", TimeSpan.FromSeconds(2));

        GateReport report = _service.Run(Config(Gate("slow", timeoutSeconds: 60)), Root, deadline: TimeSpan.FromSeconds(2));

        GateResult result = Assert.Single(report.Gates);
        Assert.Equal(GateOutcome.DeadlinePassed, result.Outcome);
        Assert.Equal(2000, result.DurationMilliseconds);
    }

    [Fact]
    public void AGateOverItsOwnTimeoutIsReportedAsTimedOut()
    {
        _programs.TimesOut("slow", TimeSpan.FromSeconds(3));

        GateReport report = _service.Run(Config(Gate("slow", timeoutSeconds: 3)), Root);

        GateResult result = Assert.Single(report.Gates);
        Assert.Equal(GateOutcome.TimedOut, result.Outcome);
        Assert.Equal(3000, result.DurationMilliseconds);
    }

    [Fact]
    public void ARequiredPathIsResolvedAgainstTheRepositoryRoot()
    {
        ReviewGate gate = new("web", ["web"]) { RequiresPaths = ["dash/node_modules"] };

        GateReport missing = _service.Run(Config(gate), Root);
        _files.CreateDirectory("/repo/dash/node_modules");
        GateReport present = _service.Run(Config(gate), Root);

        Assert.Equal(GateOutcome.NotStarted, Assert.Single(missing.Gates).Outcome);
        Assert.Equal(GateOutcome.Completed, Assert.Single(present.Gates).Outcome);
    }

    [Fact]
    public void AGatesWorkingDirectoryIsResolvedAgainstTheRepositoryRoot()
    {
        _files.CreateDirectory("/repo/dash");

        _ = _service.RunTaskGates(
            [new PlannedTaskGate("lint", true, 30, [], ["lint"], "dash")],
            Root,
            fix: false);

        Assert.Equal(["/repo/dash"], _programs.WorkingDirectories);
    }

    [Fact]
    public void ARelativeRepositoryRootIsResolvedAgainstTheProcessWorkingDirectory()
    {
        _files.CreateDirectory("/repo/dash");

        _ = _service.RunTaskGates(
            [new PlannedTaskGate("lint", true, 30, [], ["lint"], "dash")],
            ".",
            fix: false);

        Assert.Equal(["/repo/dash"], _programs.WorkingDirectories);
    }

    private static ReviewConfig Config(params ReviewGate[] gates) => new() { Gates = gates };

    private static ReviewGate Gate(string id, int timeoutSeconds = 900) => new(id, [id], TimeoutSeconds: timeoutSeconds);

    private sealed class RecordingPrograms(FakeTimeProvider clock) : IProgramRunner
    {
        private readonly Dictionary<string, (TimeSpan Elapsed, bool TimeOut)> _scripts = [];

        public List<string> Started { get; } = [];

        public List<TimeSpan?> Timeouts { get; } = [];

        public List<string> WorkingDirectories { get; } = [];

        public void Takes(string program, TimeSpan elapsed) => _scripts[program] = (elapsed, false);

        public void TimesOut(string program, TimeSpan elapsed) => _scripts[program] = (elapsed, true);

        public ProcessResult Run(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan? timeout = null,
            long? maxOutputBytes = null,
            CancellationToken cancellationToken = default)
        {
            Started.Add(program);
            Timeouts.Add(timeout);
            WorkingDirectories.Add(workingDirectory);
            (TimeSpan elapsed, bool timeOut) = _scripts.GetValueOrDefault(program);
            clock.Advance(elapsed);
            return timeOut ? throw new TimeoutException($"{program} outlived its limit") : new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class NoCoverage : ICoverageReportSource
    {
        public CoverageResult? ReadNewest(string workingDirectory, DateTime notBeforeUtc) => null;

        public IReadOnlyList<CoverageReport> ReadAll(string workingDirectory, DateTime notBeforeUtc) => [];

        public CoverageResult? ReadFile(string path) => null;
    }
}

using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The gate report reader over the repository file-store seam: every report lives in memory, so the claim check, the
/// containment rules and the failing-run-first proof are decided without a disk or a wall clock.
/// </summary>
public sealed class GateReportFactsOverSeamsTests
{
    private const string Root = "/repo";
    private const string ReportPath = "/repo/artifacts/task-gate.json";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFileSystem _files;
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/kw/work");

    public GateReportFactsOverSeamsTests()
    {
        _files = new InMemoryFileSystem(_clock);
        _paths = new FakePathResolver(_files);
    }

    private GateReportFacts Facts() => new(new InMemoryRepositoryFileStore(_files), _paths, _environment);

    private static GateReport Passing() => new(
        GateReport.CurrentSchema,
        [new GateResult("build", true, 0, "pass"), new GateResult("tests", true, 0, "12 passed")]);

    private static GateReport Failing() => new(
        GateReport.CurrentSchema,
        [new GateResult("build", true, 0, "pass"), new GateResult("tests", false, 1, "2 failed")]);

    private static GateReport FailingRun() => new(
        GateReport.CurrentSchema,
        [new GateResult("tests", true, 0, GateReportFacts.RedAcceptedSummaryPrefix + ": 2 tests failed")]);

    private static string Claim(string path, string status = "PASS") => $"TASK_GATE: {status} report={path}";

    private string Write(string path, GateReport report)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, ReviewJson.Write(report));
        return path;
    }

    [Fact]
    public void ARelativeRepositoryRootResolvesAgainstTheInjectedCurrentDirectory()
    {
        _ = Write("/kw/work/repo/artifacts/task-gate.json", Passing());

        Assert.True(Facts().Agrees(Claim("artifacts/task-gate.json"), "repo"));
    }

    [Fact]
    public void AFailingClaimForTheSameReportInARelativeSpellingInvalidatesThePass()
    {
        _ = Write(ReportPath, Passing());
        string text = Claim("artifacts/task-gate.json", "FAIL") + "\n" + Claim(ReportPath, "PASS");

        Assert.False(Facts().Agrees(text, Root));
    }

    [Fact]
    public void AReportInTheRepositoryRootIsReadFromItsDefaultPath()
    {
        _ = Write("/repo/artifacts/gates.json", Failing());

        Assert.True(Facts().FailedGateCorroborates(Root));
    }

    [Fact]
    public void AMissingEmptyOrOversizedReportReadsAsAbsent()
    {
        _files.CreateDirectory("/repo/artifacts");
        _files.WriteAllText("/repo/artifacts/gates.json", string.Empty);
        Assert.Null(Facts().TryRead(Root));

        _files.WriteAllText("/repo/artifacts/gates.json", new string(' ', (1024 * 1024) + 1));
        Assert.Null(Facts().TryRead(Root));

        Assert.Null(Facts().TryRead("/no-such-repo"));
    }

    [Fact]
    public void ADamagedReportReadsAsAbsent()
    {
        _files.CreateDirectory("/repo/artifacts");
        _files.WriteAllText("/repo/artifacts/gates.json", "{ not json");

        Assert.Null(Facts().TryRead(Root));
    }

    [Fact]
    public void AReportThatCannotBeOpenedReadsAsAbsentNotAsAFailure()
    {
        _ = Write(ReportPath, Passing());
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.OpenRead), new IOException("locked"));

        Assert.Null(Facts().Agrees(Claim(ReportPath), Root));
    }

    [Fact]
    public void ACleanReportCorroboratesAPassClaimAndAFailingOneContradictsIt()
    {
        _ = Write(ReportPath, Passing());
        Assert.True(Facts().Agrees(Claim(ReportPath), Root));

        _ = Write(ReportPath, Failing());
        Assert.False(Facts().Agrees(Claim(ReportPath), Root));
    }

    [Fact]
    public void ARelativeReportPathResolvesAgainstTheRepositoryRoot()
    {
        _ = Write(ReportPath, Passing());

        Assert.True(Facts().Agrees(Claim("artifacts/task-gate.json"), Root));
    }

    [Fact]
    public void AReportPathWithASpaceIsFoundByTheWholeLineReading()
    {
        string spaced = Write("/repo/my artifacts/task gate.json", Passing());

        Assert.True(Facts().Agrees(Claim(spaced), Root));
    }

    [Fact]
    public void ANoClaimAndAMissingReportAreUndecidable()
    {
        Assert.Null(Facts().Agrees("no digest here", Root));
        Assert.Null(Facts().Agrees(Claim(ReportPath), Root));
    }

    [Fact]
    public void AReportOutsideTheRepositoryIsNeverRead()
    {
        _ = Write("/elsewhere/gates.json", Passing());

        Assert.Null(Facts().Agrees(Claim("/elsewhere/gates.json"), Root));
        Assert.Null(Facts().Agrees(Claim("../elsewhere/gates.json"), Root));
    }

    [Fact]
    public void AReportReachedThroughALinkOutOfTheRepositoryIsNeverRead()
    {
        _ = Write("/elsewhere/gates.json", Passing());
        _files.CreateDirectory(Root);
        _files.CreateFileLink("/repo/linked.json", "/elsewhere/gates.json");

        Assert.Null(Facts().Agrees(Claim("/repo/linked.json"), Root));
    }

    [Fact]
    public void AFailingRunReportOlderThanTheClaimedPassIsTheProof()
    {
        _ = Write("/repo/artifacts/task-gate-red.json", FailingRun());
        _clock.Advance(TimeSpan.FromMinutes(20));
        _ = Write(ReportPath, Passing());

        Assert.True(Facts().FailingRunFirst(Claim(ReportPath), Root));
    }

    [Fact]
    public void AClaimedPassWithNoOlderFailingRunBesideItIsNotTheProof()
    {
        _ = Write(ReportPath, Passing());
        _clock.Advance(TimeSpan.FromMinutes(20));
        _ = Write("/repo/artifacts/task-gate-red.json", FailingRun());

        Assert.False(Facts().FailingRunFirst(Claim(ReportPath), Root));
    }

    [Fact]
    public void TheFailingRunReportItselfIsNotTheProof()
    {
        string red = Write("/repo/artifacts/task-gate-red.json", FailingRun());

        Assert.False(Facts().FailingRunFirst(Claim(red), Root));
    }

    [Fact]
    public void AFolderThatCannotBeListedLeavesTheProofUndecided()
    {
        _ = Write(ReportPath, Passing());
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.EnumerateFiles), new IOException("denied"));

        Assert.Null(Facts().FailingRunFirst(Claim(ReportPath), Root));
    }
}

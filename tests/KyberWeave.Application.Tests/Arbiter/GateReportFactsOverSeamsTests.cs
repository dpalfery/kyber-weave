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
    private readonly FakeProcessEnvironment _environment;

    public GateReportFactsOverSeamsTests()
    {
        _files = new InMemoryFileSystem(_clock);
        _paths = new FakePathResolver(_files);
        // The environment's case rule is the file system's, so the two seams agree about the world under test.
        _environment = new FakeProcessEnvironment(
            currentDirectory: "/kw/work",
            isFileSystemCaseInsensitive: !_files.CaseSensitive);
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

    /// <summary>
    /// Run 14: a worker wrote "TASK_GATE: PASS, agent=..." and the comma made the claim read as "PASS,", so a passing
    /// report was judged to disagree and the run stalled. Punctuation or emphasis around the status word is not part of it.
    /// </summary>
    [Theory]
    [InlineData("TASK_GATE: PASS, agent=csharp-dev gates=1 failed=none skipped=0 report={0} runs=3")]
    [InlineData("TASK_GATE: **PASS** report={0}")]
    [InlineData("TASK_GATE: PASS; report={0}")]
    [InlineData("TASK_GATE: `PASS` report={0}")]
    [InlineData("TASK_GATE: PASS \u2014 agent=csharp-dev report={0}")]
    [InlineData("TASK_GATE: PASS\u2014 report={0}")]
    [InlineData("TASK_GATE: \u201cPASS\u201d report={0}")]
    [InlineData("TASK_GATE: <PASS> report={0}")]
    public void APassClaimWithPunctuationAroundTheStatusAgreesWithAPassingReport(string format)
    {
        string path = Write(ReportPath, Passing());

        Assert.True(Facts().Agrees(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, path), Root));
    }

    [Fact]
    public void ARelativeRepositoryRootResolvesAgainstTheInjectedCurrentDirectory()
    {
        _ = Write("/kw/work/repo/artifacts/task-gate.json", Passing());

        Assert.True(Facts().Agrees(Claim("artifacts/task-gate.json"), "repo"));
    }

    [Fact]
    public void ARelativeRootFindsTheDefaultReportToo()
    {
        _ = Write("/kw/work/repo/artifacts/gates.json", Failing());

        Assert.NotNull(Facts().TryRead("repo"));
        Assert.True(Facts().FailedGateCorroborates("repo", null));
    }

    [Fact]
    public void AFailingClaimForTheSameReportInARelativeSpellingInvalidatesThePass()
    {
        _ = Write(ReportPath, Passing());
        string text = Claim("artifacts/task-gate.json", "FAIL") + "\n" + Claim(ReportPath, "PASS");

        Assert.False(Facts().Agrees(text, Root));
    }

    [Fact]
    public void AMalformedReportPathIsUndecidableRatherThanAThrow()
    {
        Assert.Null(Facts().Agrees("TASK_GATE: PASS report=a\0b", Root));
        Assert.Null(Facts().TryReadFile("a\0b"));
    }

    [Fact]
    public void AClaimInAnotherCaseIsTheSameReportOnACaseInsensitiveFileSystem()
    {
        _ = Write(ReportPath, Passing());

        Assert.True(Facts().Agrees(Claim("/REPO/artifacts/task-gate.json"), Root));
    }

    [Fact]
    public void AReportInTheRepositoryRootIsReadFromItsDefaultPath()
    {
        _ = Write("/repo/artifacts/gates.json", Failing());

        Assert.True(Facts().FailedGateCorroborates(Root, null));
    }

    /// <summary>
    /// A council dispatch names its cycle in <c>REVIEW_RUN</c>, and the cycle's report lives under the scratch folder,
    /// not under <c>artifacts/</c>. A finding must be corroborated by the gate run the council actually made.
    /// </summary>
    [Fact]
    public void ACouncilDispatchReadsTheNamedCyclesGateReport()
    {
        _ = Write("/repo/.agent_scratchpad/t7/review/cycle-2/gates.json", Failing());

        Assert.True(Facts().FailedGateCorroborates(Root, "t7-cycle-2"));
    }

    /// <summary>
    /// The task part of <c>REVIEW_RUN</c> names one folder under the scratch root. A rooted value, or one carrying
    /// a parent segment, must not name a cycle report; the dispatch falls back to the repository's default report.
    /// </summary>
    [Theory]
    [InlineData("/etc/x-cycle-1")]
    [InlineData("../../outside/y-cycle-1")]
    public void AReviewRunWhoseTaskNamesAPathNeverReadsACycleReport(string reviewRun)
    {
        _ = Write("/repo/artifacts/gates.json", Passing());
        _ = Write("/repo/.agent_scratchpad/etc/x/review/cycle-1/gates.json", Failing());
        _ = Write("/outside/y/review/cycle-1/gates.json", Failing());

        Assert.False(Facts().FailedGateCorroborates(Root, reviewRun));
    }

    /// <summary>A dispatch that names no cycle keeps reading the repository's default report.</summary>
    [Fact]
    public void ADispatchWithNoReviewRunStillReadsTheDefaultReport()
    {
        _ = Write("/repo/artifacts/gates.json", Failing());

        Assert.True(Facts().FailedGateCorroborates(Root, null));
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

    /// <summary>
    /// Containment follows every hop: the first link can point inside the repository at a second link that
    /// leaves it, so only the final target says whether the report is inside the tree.
    /// </summary>
    [Fact]
    public void AReportReachedThroughAChainOfLinksOutOfTheRepositoryIsNeverRead()
    {
        _ = Write("/elsewhere/gates.json", Passing());
        _files.CreateDirectory(Root);
        _files.CreateFileLink("/repo/second.json", "/elsewhere/gates.json");
        _files.CreateFileLink("/repo/first.json", "/repo/second.json");

        Assert.Null(Facts().Agrees(Claim("/repo/first.json"), Root));
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

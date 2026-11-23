using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// A council or <c>--all</c> run is refused before any gate or index work when a gate it would start requires a path that
/// is missing, listing each path with its fix; a scoped run keeps reporting the gate as not started. The runner's
/// <c>MissingRequirements</c> is what the refusal reads, so "missing" means exactly "would not start".
/// </summary>
public sealed class ReviewGatesRequiredPathsPreflightTests : IDisposable
{
    private const string NotStarted = "KW-REVIEW-033";

    private const string Config = """
        review:
          gates:
            - id: dash-lint
              run: [dash-lint]
              requires-paths: [dash/node_modules]
            - id: web-build
              run: [web-build]
              requires-paths: [dash/web/node_modules]
            - id: plain
              run: [plain]
        """;

    private readonly InMemoryReviewServices _services = new();
    private readonly FakeProcessExecutor _index = new();
    private readonly string _repo;

    public ReviewGatesRequiredPathsPreflightTests()
    {
        _repo = Path.Combine(_services.Root, "repo");
        _services.Files.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        _services.Files.CreateDirectory(Path.Combine(_repo, "docs"));
        _services.Files.WriteAllText(Path.Combine(_repo, "docs", "README.md"), "# docs\n");
        _services.Files.WriteAllText(Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"), Config);
        _services.ScriptProgram("git", TimeSpan.Zero);
        _services.ScriptProgram("dash-lint", TimeSpan.Zero);
        _services.ScriptProgram("web-build", TimeSpan.Zero);
        _services.ScriptProgram("plain", TimeSpan.Zero);
    }

    private string Out => Path.Combine(_repo, ".agent_scratchpad", "t", "review", "cycle-1", "gates.json");

    public void Dispose() => _services.Dispose();

    [Fact]
    public void NothingMissingIsAnEmptyList()
    {
        MakeAllPresent();

        Assert.Empty(_services.Gates.MissingRequirements(Declared(), _repo, null));
    }

    [Fact]
    public void EveryGateMissingAPathIsListedWithItsIdPathAndFix()
    {
        IReadOnlyList<MissingGateRequirement> missing = _services.Gates.MissingRequirements(Declared(), _repo, null);

        Assert.Equal(
            [
                new MissingGateRequirement("dash-lint", "dash/node_modules", "Install the dependencies with 'npm install --prefix dash'"),
                new MissingGateRequirement("web-build", "dash/web/node_modules", "Install the dependencies with 'npm install --prefix dash/web'"),
            ],
            missing);
    }

    [Fact]
    public void AnyOtherPathIsFixedByInstallingWhateverProvidesIt()
    {
        ReviewConfig config = new() { Gates = [new ReviewGate("tool", ["tool"]) { RequiresPaths = ["tools/x"] }] };

        MissingGateRequirement missing = Assert.Single(_services.Gates.MissingRequirements(config, _repo, null));

        Assert.Equal("Install whatever provides it", missing.Fix);
    }

    [Fact]
    public void AGateWhoseAppliesWhenExcludesTheChangeIsNotListed()
    {
        ReviewConfig config = new()
        {
            Gates =
            [
                new ReviewGate("dash-lint", ["dash-lint"], AppliesWhen: new ReviewGateAppliesWhen(["dash/**"]))
                {
                    RequiresPaths = ["dash/node_modules"],
                },
            ],
        };

        Assert.Empty(_services.Gates.MissingRequirements(config, _repo, ["src/Program.cs"]));
        Assert.Single(_services.Gates.MissingRequirements(config, _repo, ["dash/web/app.ts"]));
    }

    [Fact]
    public void AGateWithNoRequiredPathsIsNeverListed()
    {
        ReviewConfig config = new() { Gates = [new ReviewGate("plain", ["plain"])] };

        Assert.Empty(_services.Gates.MissingRequirements(config, _repo, ["anything"]));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ACouncilOrAllRunWithAMissingPathIsRefusedBeforeAnyWork(bool council, bool all)
    {
        ReviewCommandResult result = Run(council, all);

        Assert.Equal(1, result.ExitCode);
        Diagnostic[] errors = [.. result.Report!.Items.Where(item => item.Code == NotStarted)];
        Assert.Equal(2, errors.Length);
        Assert.All(errors, error => Assert.Equal(Severity.Error, error.Severity));
        Assert.Contains(errors, error => error.Message.Contains("dash/node_modules", StringComparison.Ordinal)
                                         && error.Message.Contains("npm install --prefix dash'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("dash/web/node_modules", StringComparison.Ordinal)
                                         && error.Message.Contains("npm install --prefix dash/web'", StringComparison.Ordinal));
        Assert.DoesNotContain(_services.Processes.Calls, call => call.Program is "dash-lint" or "web-build" or "plain");
        Assert.Empty(_index.Calls);
        GateReport written = ReviewJson.ReadGates(_services.Files.ReadAllText(Out));
        Assert.Empty(written.Gates);
    }

    [Fact]
    public void AScopedRunStillStartsTheOtherGatesAndReportsTheMissingOneNotStarted()
    {
        ReviewCommandResult result = Run(council: false, all: false);

        Assert.Contains(_services.Processes.Calls, call => call.Program == "plain");
        Assert.DoesNotContain(_services.Processes.Calls, call => call.Program is "dash-lint" or "web-build");
        Assert.Equal(2, result.Report!.Items.Count(item => item.Code == NotStarted));
    }

    [Fact]
    public void ACouncilRunWithEveryPathPresentRunsItsGates()
    {
        MakeAllPresent();

        ReviewCommandResult result = Run(council: true, all: false);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(_index.Calls);
        Assert.Contains(_services.Processes.Calls, call => call.Program == "dash-lint");
        Assert.Contains(_services.Processes.Calls, call => call.Program == "web-build");
        Assert.Contains(_services.Processes.Calls, call => call.Program == "plain");
    }

    [Fact]
    public void ARefusedCouncilRunFollowedByARerunIsTheSameCycleInFullMode()
    {
        ReviewCommandResult refused = Run(council: true, all: false);
        MakeAllPresent();

        ReviewCommandResult rerun = Run(council: true, all: false);

        Assert.Equal(1, refused.ExitCode);
        Assert.Equal(0, rerun.ExitCode);
        Assert.Equal(1, rerun.Report!.Metrics["cycle"]);
        Assert.Equal("full", rerun.Report.Metrics["mode"]);
    }

    private static ReviewConfig Declared() => new()
    {
        Gates =
        [
            new ReviewGate("dash-lint", ["dash-lint"]) { RequiresPaths = ["dash/node_modules"] },
            new ReviewGate("web-build", ["web-build"]) { RequiresPaths = ["dash/web/node_modules"] },
            new ReviewGate("plain", ["plain"]),
        ],
    };

    private void MakeAllPresent()
    {
        _services.Files.CreateDirectory(Path.Combine(_repo, "dash", "node_modules"));
        _services.Files.CreateDirectory(Path.Combine(_repo, "dash", "web", "node_modules"));
    }

    private ReviewCommandResult Run(bool council, bool all) =>
        _services.ReviewGates.Run(
            new ReviewGatesRequest(
                _repo, null, Out, null, null, [], [], All: all, Deadline: false, Council: council,
                StopOnFailure: false, Full: false, TaskSlug: null, Fix: false, Red: false,
                HumanOutput: false, Interactive: false),
            _index,
            TextWriter.Null);
}

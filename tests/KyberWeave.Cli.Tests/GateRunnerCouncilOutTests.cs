using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>review gates --council</c> writes the full council report, so it needs <c>--out</c> to name the file. Without it
/// the run is refused as bad input (KW-REVIEW-028, exit 2) naming <c>--out</c> and <c>review seats</c>, before any gate
/// runs and before the service is called. With <c>--out</c> the council run reaches the service. <c>--out</c> without
/// <c>--council</c> reaches the service, and a task-gate run for one agent is not asked for <c>--out</c>.
/// </summary>
public sealed class GateRunnerCouncilOutTests : IDisposable
{
    private const string InvalidInput = "KW-REVIEW-028";
    private const int ClientInputExitCode = 2;
    private const string Marker = "ran.marker";

    private readonly InMemoryReviewServices _services = new();
    private readonly string _repo;
    private readonly string _report;

    public GateRunnerCouncilOutTests()
    {
        _repo = Path.Combine(_services.Root, "repo");
        _services.Files.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        _report = Path.Combine(_services.Root, "gates.json");

        // The gate writes a marker when it runs, so a refusal that comes too late is visible as the process seam's calls.
        _services.Files.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            "review:\n  gates:\n    - id: probe\n      run: [touch, " + Marker + "]\n");
        _services.ScriptProgram("touch", TimeSpan.Zero);
        _services.ScriptProgram("git", TimeSpan.Zero);
    }

    /// <summary>Releases the services.</summary>
    public void Dispose()
    {
        _services.Dispose();
    }

    [Fact]
    public void CouncilWithoutOutIsRefusedNamingOutAndReviewSeatsBeforeAnyGateRuns()
    {
        CapturedConsoleExecution<int> run = _services.Run(
            _services.ReviewGatesApp(), ["review", "gates", _repo, "--council", "--format", "json"]);

        Assert.Equal(ClientInputExitCode, run.Result);
        Assert.Contains(InvalidInput, run.Output, StringComparison.Ordinal);
        Assert.Contains("--out", run.Output, StringComparison.Ordinal);
        Assert.Contains("review seats", run.Output, StringComparison.Ordinal);
        Assert.Empty(_services.Processes.Calls);
        Assert.False(_services.Files.FileExists(_report));
    }

    [Fact]
    public void CouncilWithOutReachesTheServiceAndWritesTheReport()
    {
        CapturedConsoleExecution<int> run = _services.Run(
            _services.ReviewGatesApp(), ["review", "gates", _repo, "--council", "--format", "json", "--out", _report]);

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain(InvalidInput, run.Output, StringComparison.Ordinal);
        Assert.True(_services.Files.FileExists(_report));
    }

    [Fact]
    public void OutWithoutCouncilReachesTheServiceAndWritesTheReport()
    {
        CapturedConsoleExecution<int> run = _services.Run(
            _services.ReviewGatesApp(), ["review", "gates", _repo, "--format", "json", "--out", _report]);

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain(InvalidInput, run.Output, StringComparison.Ordinal);
        Assert.True(_services.Files.FileExists(_report));
    }

    [Fact]
    public void AnAgentRunWithCouncilAndWithoutOutIsNotRefusedForOut()
    {
        CapturedConsoleExecution<int> run = _services.Run(
            _services.ReviewGatesApp(), ["review", "gates", _repo, "--agent", "dev-easy", "--council", "--format", "json"]);

        Assert.NotEqual(ClientInputExitCode, run.Result);
        Assert.DoesNotContain("--out", run.Output, StringComparison.Ordinal);
    }
}

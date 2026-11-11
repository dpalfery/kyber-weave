using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for the review fix to <c>kyber-weave arbiter tune</c>: a step-1 confidence that is not a
/// finite number between 0 and 1 is never tallied. It is skipped and counted as unreadable, so it
/// cannot land in histogram bin 0 or flip at a threshold, and the count is reported.
/// </summary>
/// <remarks>
/// Written through the production <see cref="DecisionLog" /> writer, so the record is the one the
/// command reads. JSON cannot carry NaN through the default serializer, so the reachable bad values
/// are the out-of-range numbers 1.5 and -0.2; the finiteness check covers NaN as well.
/// </remarks>
public sealed class ArbiterTuneUnreadableConfidenceTests : IDisposable
{
    private const string LensRule = "KW-ARB-LENS-001";
    private const string Thresholds = "0.5,0.6,0.7,0.8,0.9";
    private const double CurrentThreshold = 0.6;

    private static readonly int[] ExpectedHistogram = [0, 0, 0, 0, 0, 0, 0, 0, 0, 1];

    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private string ArbiterDirectory => Path.Combine(_repo.Path, "artifacts", "arbiter");

    /// <summary>One valid shadow score of 0.95 and two out-of-range scores, 1.5 and -0.2.</summary>
    private async Task WriteFixtureAsync()
    {
        Directory.CreateDirectory(ArbiterDirectory);
        DecisionLog decisions = new(ArbiterDirectory);
        await decisions.AppendAsync(Decision("d-1", "ledger-1", 0.95));
        await decisions.AppendAsync(Decision("d-2", "ledger-2", 1.5));
        await decisions.AppendAsync(Decision("d-3", "ledger-3", -0.2));
    }

    private static ArbiterDecisionRecord Decision(string id, string ledgerId, double confidence)
    {
        ArbiterDecisionRule ruleRecord = new(
            LensRule,
            1,
            "applies",
            null,
            confidence,
            CurrentThreshold,
            "allow",
            [],
            null)
        {
            Shadow = true,
        };
        ArbiterProviderRecord provider = new(
            "systemone", null, "jev-1.13.0", null, 12, ArbiterProviderStatuses.Answered);

        return new ArbiterDecisionRecord(
            id,
            At,
            "hook",
            "claude",
            null,
            ledgerId,
            "lens.spawn",
            "code-reviewer",
            "header",
            "review-lens",
            null,
            null,
            null,
            "shipped",
            [ruleRecord],
            provider,
            "allow",
            0,
            5);
    }

    private static CommandExecution RunTune(string repo, params string[] extra)
    {
        string[] arguments = ["arbiter", "tune", repo, .. extra];
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            CommandAppFixture.ArbiterTune().Run(arguments));
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    /// <summary>Out-of-range scores are not tallied: only the valid 0.95 reaches the histogram and the count.</summary>
    [Fact]
    public async Task Tune_JsonSkipsOutOfRangeConfidencesAndCountsThemUnreadable()
    {
        await WriteFixtureAsync();

        CommandExecution execution = RunTune(_repo.Path, "--thresholds", Thresholds, "--json");

        Assert.Equal(0, execution.ExitCode);
        using JsonDocument document = JsonDocument.Parse(execution.Output);
        JsonElement lens = document.RootElement.GetProperty("rules").EnumerateArray().Single();

        Assert.Equal(1, lens.GetProperty("scored").GetInt32());
        Assert.Equal(2, lens.GetProperty("unreadable").GetInt32());
        Assert.Equal(ExpectedHistogram, lens.GetProperty("histogram").EnumerateArray().Select(bin => bin.GetInt32()));
    }

    /// <summary>The text report names the unreadable count so the skipped records are visible to the operator.</summary>
    [Fact]
    public async Task Tune_TextReportsTheUnreadableCount()
    {
        await WriteFixtureAsync();

        CommandExecution execution = RunTune(_repo.Path, "--thresholds", Thresholds);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("unreadable 2", execution.Output, StringComparison.Ordinal);
    }
}

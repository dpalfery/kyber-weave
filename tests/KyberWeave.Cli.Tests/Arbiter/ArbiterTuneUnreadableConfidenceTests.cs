using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Cli.Commands.Arbiter;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

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

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private string ArbiterDirectory => Path.Combine(_host.WorkingDirectory, "artifacts", "arbiter");

    /// <summary>One valid shadow score of 0.95 and two out-of-range scores, 1.5 and -0.2.</summary>
    private async Task WriteFixtureAsync()
    {
        DecisionLog decisions = _host.State.Decisions(ArbiterDirectory);
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

    private ArbiterCommandRun RunTune(string repo, params string[] extra)
    {
        string[] arguments = ["arbiter", "tune", repo, .. extra];
        CommandApp app = _host.App(config =>
            config.AddBranch("arbiter", arbiter => arbiter.AddCommand<ArbiterTuneCommand>("tune")));
        return _host.Run(() => app.Run(arguments));
    }

    /// <summary>Out-of-range scores are not tallied: only the valid 0.95 reaches the histogram and the count.</summary>
    [Fact]
    public async Task Tune_JsonSkipsOutOfRangeConfidencesAndCountsThemUnreadable()
    {
        await WriteFixtureAsync();

        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory, "--thresholds", Thresholds, "--json");

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

        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory, "--thresholds", Thresholds);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("unreadable 2", execution.Output, StringComparison.Ordinal);
    }
}

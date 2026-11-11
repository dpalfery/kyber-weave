using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for task 1.17: <c>kyber-weave arbiter tune</c> scores the step-1 answers in the
/// decision log. For each step-1 rule it counts the scored answers, histograms their confidence in
/// tenths, and counts how many answers would change side at each candidate threshold, with shadow
/// and enforced answers counted apart. A decision whose ledger event carries a return shows that
/// return's <c>RESULT:</c> beside its score.
/// </summary>
/// <remarks>
/// <para>
/// The logs are written through the production <see cref="DecisionLog" /> and
/// <see cref="InFlightLedger" /> writers, so a field the command reads under a different name
/// fails here rather than in a hand-written fixture. The command is driven through a real
/// <see cref="Spectre.Console.Cli.CommandApp" /> built by <see cref="CommandAppFixture" />, so the
/// argument surface is the one a user types.
/// </para>
/// <para>
/// The tune arithmetic is fixed by the fixture: the current threshold is 0.6 and no confidence
/// equals a candidate threshold, so each candidate's flip count is unambiguous. Scores of
/// 0.45, 0.65 and 0.66 are shadow; 0.95 and 0.12 are enforced. A side flips when the score is at
/// or above the threshold on one side and below it on the other.
/// </para>
/// <para>
/// RED: <c>arbiter tune</c> is not registered and <c>ArbiterTuneCommand</c> does not exist yet.
/// The JSON key names used below (<c>scored</c>, <c>shadow</c>, <c>enforced</c>, <c>histogram</c>,
/// <c>thresholds</c>, <c>threshold</c>, <c>changed</c>) are the contract this file pins; the task
/// packet names the behaviour, not the keys. The RESULT pairing is asserted in the text report only.
/// </para>
/// </remarks>
public sealed class ArbiterTuneCommandTests : IDisposable
{
    private const string LensRule = "KW-ARB-LENS-001";
    private const string QuoteRule = "KW-ARB-QUOTE-001";
    private const string ScopeRule = "KW-ARB-SCOPE-001";
    private const string Thresholds = "0.5,0.6,0.7,0.8,0.9";
    private const double CurrentThreshold = 0.6;
    private const string ReturnedLedgerId = "ledger-1";
    private const string NoScoredAnswers = "no scored answers";

    private static readonly int[] ExpectedHistogram = [0, 1, 0, 0, 1, 0, 2, 0, 0, 1];

    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private string ArbiterDirectory => Path.Combine(_repo.Path, "artifacts", "arbiter");

    /// <summary>
    /// Writes five step-1 LENS-001 answers (three shadow, two enforced), one step-1 QUOTE-001
    /// answer, one step-0 answer that must not be scored, and one return for the first LENS-001
    /// dispatch.
    /// </summary>
    private async Task WriteStandardFixtureAsync()
    {
        Directory.CreateDirectory(ArbiterDirectory);
        DecisionLog decisions = new(ArbiterDirectory);
        await decisions.AppendAsync(Decision("d-1", "ledger-1", LensRule, 1, 0.45, shadow: true));
        await decisions.AppendAsync(Decision("d-2", "ledger-2", LensRule, 1, 0.65, shadow: true));
        await decisions.AppendAsync(Decision("d-3", "ledger-3", LensRule, 1, 0.66, shadow: true));
        await decisions.AppendAsync(Decision("d-4", "ledger-4", LensRule, 1, 0.95, shadow: false));
        await decisions.AppendAsync(Decision("d-5", "ledger-5", LensRule, 1, 0.12, shadow: false));
        await decisions.AppendAsync(Decision("d-6", "ledger-6", QuoteRule, 1, 0.9, shadow: false));
        await decisions.AppendAsync(Decision("d-7", "ledger-7", ScopeRule, 0, null, shadow: false));

        InFlightLedger ledger = new(ArbiterDirectory);
        await ledger.AppendAsync(new ArbiterLedgerEvent(
            "post-1", At, "hook", "claude", null, ArbiterLedgerPhases.Post)
        {
            PreId = ReturnedLedgerId,
            Returns = new ArbiterReturnMarkers(TaskReview: "PASS"),
        });
    }

    /// <summary>One decision record, with a step-1 score when <paramref name="confidence"/> is given.</summary>
    private static ArbiterDecisionRecord Decision(
        string id,
        string ledgerId,
        string rule,
        int step,
        double? confidence,
        bool shadow)
    {
        ArbiterDecisionRule ruleRecord = new(
            rule,
            step,
            "applies",
            null,
            confidence,
            confidence is null ? null : CurrentThreshold,
            "allow",
            [],
            null)
        {
            Shadow = shadow ? true : null,
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

    private static JsonElement RuleWithId(JsonElement report, string id)
    {
        foreach (JsonElement rule in report.GetProperty("rules").EnumerateArray())
        {
            if (string.Equals(rule.GetProperty("id").GetString(), id, StringComparison.Ordinal))
            {
                return rule;
            }
        }

        throw new Xunit.Sdk.XunitException($"the tune report has no rule '{id}'.");
    }

    /// <summary>
    /// The LENS-001 counts are exact: five scored answers, three shadow and two enforced, their
    /// tenths histogram, and the flips at each candidate threshold against the current 0.6.
    /// </summary>
    [Fact]
    public async Task Tune_JsonReportsScoredCountsHistogramAndThresholdFlipsForLens001()
    {
        await WriteStandardFixtureAsync();

        CommandExecution execution = RunTune(_repo.Path, "--thresholds", Thresholds, "--json");

        Assert.Equal(0, execution.ExitCode);
        using JsonDocument document = JsonDocument.Parse(execution.Output);
        JsonElement lens = RuleWithId(document.RootElement, LensRule);

        Assert.Equal(5, lens.GetProperty("scored").GetInt32());
        Assert.Equal(3, lens.GetProperty("shadow").GetInt32());
        Assert.Equal(2, lens.GetProperty("enforced").GetInt32());
        Assert.Equal(ExpectedHistogram, lens.GetProperty("histogram").EnumerateArray().Select(bin => bin.GetInt32()));

        Dictionary<double, int> changed = lens.GetProperty("thresholds").EnumerateArray()
            .ToDictionary(
                candidate => candidate.GetProperty("threshold").GetDouble(),
                candidate => candidate.GetProperty("changed").GetInt32());
        Assert.Equal(0, changed[0.5]);
        Assert.Equal(0, changed[0.6]);
        Assert.Equal(2, changed[0.7]);
        Assert.Equal(2, changed[0.8]);
        Assert.Equal(2, changed[0.9]);
    }

    /// <summary>Every step-1 rule with a scored answer is reported, and the step-0 answer is not scored.</summary>
    [Fact]
    public async Task Tune_JsonIncludesEveryStep1RuleWithScoredAnswers()
    {
        await WriteStandardFixtureAsync();

        CommandExecution execution = RunTune(_repo.Path, "--json");

        Assert.Equal(0, execution.ExitCode);
        using JsonDocument document = JsonDocument.Parse(execution.Output);
        Assert.Equal(1, RuleWithId(document.RootElement, QuoteRule).GetProperty("scored").GetInt32());
        Assert.Equal(5, RuleWithId(document.RootElement, LensRule).GetProperty("scored").GetInt32());
        Assert.DoesNotContain(
            document.RootElement.GetProperty("rules").EnumerateArray(),
            rule => string.Equals(rule.GetProperty("id").GetString(), ScopeRule, StringComparison.Ordinal));
    }

    /// <summary>With <c>--rule</c> the report names only that rule.</summary>
    [Fact]
    public async Task Tune_RuleFilterLimitsTheReportToThatRule()
    {
        await WriteStandardFixtureAsync();

        CommandExecution execution = RunTune(_repo.Path, "--rule", LensRule, "--json");

        Assert.Equal(0, execution.ExitCode);
        using JsonDocument document = JsonDocument.Parse(execution.Output);
        JsonElement rules = document.RootElement.GetProperty("rules");
        Assert.Equal(1, rules.GetArrayLength());
        Assert.Equal(LensRule, rules[0].GetProperty("id").GetString());
    }

    /// <summary>The text report names the rule and the return's <c>RESULT:</c> beside the decision it pairs with.</summary>
    [Fact]
    public async Task Tune_TextReportNamesTheRuleAndThePairedResult()
    {
        await WriteStandardFixtureAsync();

        CommandExecution execution = RunTune(_repo.Path);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(LensRule, execution.Output, StringComparison.Ordinal);
        Assert.Contains("PASS", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>With no logs the command says so on one line and exits zero, and creates nothing.</summary>
    [Fact]
    public void Tune_WithNoLogsSaysNoScoredAnswersOnOneLineAndExitsZero()
    {
        CommandExecution execution = RunTune(_repo.Path);

        Assert.Equal(0, execution.ExitCode);
        string[] lines = execution.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string line = Assert.Single(lines);
        Assert.Contains(NoScoredAnswers, line, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(ArbiterDirectory), "tune must not create the arbiter directory.");
    }

    /// <summary>A log with only step-0 answers has nothing scored, so the same one-line message applies.</summary>
    [Fact]
    public async Task Tune_WithOnlyStepZeroAnswersSaysNoScoredAnswersOnOneLine()
    {
        Directory.CreateDirectory(ArbiterDirectory);
        DecisionLog decisions = new(ArbiterDirectory);
        await decisions.AppendAsync(Decision("d-7", "ledger-7", ScopeRule, 0, null, shadow: false));

        CommandExecution execution = RunTune(_repo.Path);

        Assert.Equal(0, execution.ExitCode);
        string[] lines = execution.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string line = Assert.Single(lines);
        Assert.Contains(NoScoredAnswers, line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tune only reads. Every existing log keeps its bytes and its modification time, and no file
    /// appears beside them. The ledger's <c>.lock</c> is excluded because a read may take it.
    /// </summary>
    [Fact]
    public async Task Tune_WritesNothing()
    {
        await WriteStandardFixtureAsync();
        DateTime fixedTime = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.GetFiles(ArbiterDirectory))
        {
            File.SetLastWriteTimeUtc(file, fixedTime);
        }

        Dictionary<string, (byte[] Bytes, DateTime Written)> before = Snapshot();

        CommandExecution text = RunTune(_repo.Path);
        CommandExecution json = RunTune(_repo.Path, "--json");

        Assert.Equal(0, text.ExitCode);
        Assert.Equal(0, json.ExitCode);
        Dictionary<string, (byte[] Bytes, DateTime Written)> after = Snapshot();
        Assert.Equal(before.Keys.OrderBy(name => name, StringComparer.Ordinal), after.Keys.OrderBy(name => name, StringComparer.Ordinal));
        foreach ((string name, (byte[] bytes, DateTime written)) in before)
        {
            Assert.Equal(bytes, after[name].Bytes);
            Assert.Equal(written, after[name].Written);
        }
    }

    private Dictionary<string, (byte[] Bytes, DateTime Written)> Snapshot()
    {
        Dictionary<string, (byte[] Bytes, DateTime Written)> files = new(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(ArbiterDirectory))
        {
            string name = Path.GetFileName(file);
            if (string.Equals(name, ".lock", StringComparison.Ordinal))
            {
                continue;
            }

            files[name] = (File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file));
        }

        return files;
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}

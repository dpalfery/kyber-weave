using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Cli.Commands.Arbiter;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

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
/// <see cref="Spectre.Console.Cli.CommandApp" /> built by the in-memory host, so the
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

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private string ArbiterDirectory => Path.Combine(_host.WorkingDirectory, "artifacts", "arbiter");

    /// <summary>
    /// Writes five step-1 LENS-001 answers (three shadow, two enforced), one step-1 QUOTE-001
    /// answer, one step-0 answer that must not be scored, and one return for the first LENS-001
    /// dispatch.
    /// </summary>
    private async Task WriteStandardFixtureAsync()
    {
        DecisionLog decisions = _host.State.Decisions(ArbiterDirectory);
        await decisions.AppendAsync(Decision("d-1", "ledger-1", LensRule, 1, 0.45, shadow: true));
        await decisions.AppendAsync(Decision("d-2", "ledger-2", LensRule, 1, 0.65, shadow: true));
        await decisions.AppendAsync(Decision("d-3", "ledger-3", LensRule, 1, 0.66, shadow: true));
        await decisions.AppendAsync(Decision("d-4", "ledger-4", LensRule, 1, 0.95, shadow: false));
        await decisions.AppendAsync(Decision("d-5", "ledger-5", LensRule, 1, 0.12, shadow: false));
        await decisions.AppendAsync(Decision("d-6", "ledger-6", QuoteRule, 1, 0.9, shadow: false));
        await decisions.AppendAsync(Decision("d-7", "ledger-7", ScopeRule, 0, null, shadow: false));

        InFlightLedger ledger = _host.State.Ledger(ArbiterDirectory);
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

    private ArbiterCommandRun RunTune(string repo, params string[] extra)
    {
        string[] arguments = ["arbiter", "tune", repo, .. extra];
        CommandApp app = _host.App(config =>
            config.AddBranch("arbiter", arbiter => arbiter.AddCommand<ArbiterTuneCommand>("tune")));
        return _host.Run(() => app.Run(arguments));
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

        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory, "--thresholds", Thresholds, "--json");

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

        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory, "--json");

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

        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory, "--rule", LensRule, "--json");

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

        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(LensRule, execution.Output, StringComparison.Ordinal);
        Assert.Contains("PASS", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Task 2.9 fixture: two <c>delegate.model</c> decisions on DIFFICULTY-001, one shadow (not delivered,
    /// reason <c>shadow</c>) and one enforced (delivered to <c>general</c> on token <c>t-general</c>), each with
    /// a paired return. The TASK_GATE outcome is not in this fixture: the gate report the return names is
    /// not written, so the TASK_GATE assertion is deferred (see the task 2.9 coverage gap).
    /// </summary>
    private async Task WriteDelegateModelFixtureAsync()
    {
        DecisionLog decisions = _host.State.Decisions(ArbiterDirectory);
        await decisions.AppendAsync(DelegateModelDecision(
            "d-shadow", "ledger-shadow", shadow: true, delivered: false, reason: "shadow"));
        await decisions.AppendAsync(DelegateModelDecision(
            "d-enforced", "ledger-enforced", shadow: false, delivered: true, reason: null));

        InFlightLedger ledger = _host.State.Ledger(ArbiterDirectory);
        await ledger.AppendAsync(new ArbiterLedgerEvent(
            "post-shadow", At, "hook", "claude", null, ArbiterLedgerPhases.Post)
        {
            PreId = "ledger-shadow",
            Returns = new ArbiterReturnMarkers(TaskReview: "FAIL"),
        });
        await ledger.AppendAsync(new ArbiterLedgerEvent(
            "post-enforced", At, "hook", "claude", null, ArbiterLedgerPhases.Post)
        {
            PreId = "ledger-enforced",
            Returns = new ArbiterReturnMarkers(TaskReview: "PASS"),
        });
    }

    /// <summary>One <c>delegate.model</c> decision: a DIFFICULTY-001 step-1 score, with its model block.</summary>
    private static ArbiterDecisionRecord DelegateModelDecision(
        string id,
        string ledgerId,
        bool shadow,
        bool delivered,
        string? reason)
    {
        ArbiterDecisionRule ruleRecord = new(
            "KW-ARB-DIFFICULTY-001",
            1,
            "hard",
            null,
            0.9,
            CurrentThreshold,
            "keep",
            [],
            null)
        {
            Shadow = shadow ? true : null,
            WouldEffect = shadow ? "retarget" : null,
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
            "delegate.model",
            "conductor",
            "header",
            "csharp-dev",
            null,
            null,
            null,
            "shipped",
            [ruleRecord],
            provider,
            delivered ? "retarget" : "keep",
            0,
            5,
            model: new ArbiterModelBlock("general", "t-general", "claude-sonnet-5-5", "difficulty", delivered, reason));
    }

    /// <summary>
    /// The delegate.model rows: the profile, token and <c>delivered</c> sit beside the joined
    /// <c>RESULT:</c>, with shadow and enforced rows kept apart. Shape asserted in the <c>--json</c>
    /// report: each rule carries a <c>model</c> array of
    /// <c>{decision, shadow, profile, token, delivered, result}</c>.
    /// </summary>
    [Fact]
    public async Task Tune_DelegateModelRowsShowProfileTokenAndDeliveredBesideResult()
    {
        await WriteDelegateModelFixtureAsync();

        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory);

        Assert.Equal(0, execution.ExitCode);
        string[] lines = execution.Output.Split('\n');
        string shadowLine = Assert.Single(lines, line => line.Contains("d-shadow", StringComparison.Ordinal));
        string enforcedLine = Assert.Single(lines, line => line.Contains("d-enforced", StringComparison.Ordinal));
        Assert.Contains("profile general", shadowLine, StringComparison.Ordinal);
        Assert.Contains("token t-general", shadowLine, StringComparison.Ordinal);
        Assert.Contains("delivered false", shadowLine, StringComparison.Ordinal);
        Assert.Contains("RESULT: FAIL", shadowLine, StringComparison.Ordinal);
        Assert.Contains("profile general", enforcedLine, StringComparison.Ordinal);
        Assert.Contains("token t-general", enforcedLine, StringComparison.Ordinal);
        Assert.Contains("delivered true", enforcedLine, StringComparison.Ordinal);
        Assert.Contains("RESULT: PASS", enforcedLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tune_JsonDelegateModelRowsKeepShadowAndEnforcedApart()
    {
        await WriteDelegateModelFixtureAsync();

        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory, "--json");

        Assert.Equal(0, execution.ExitCode);
        JsonElement rule = RuleWithId(JsonDocument.Parse(execution.Output).RootElement, "KW-ARB-DIFFICULTY-001");
        List<JsonElement> rows = rule.GetProperty("model").EnumerateArray().ToList();
        JsonElement shadow = Assert.Single(rows, row => row.GetProperty("decision").GetString() == "d-shadow");
        JsonElement enforced = Assert.Single(rows, row => row.GetProperty("decision").GetString() == "d-enforced");

        Assert.True(shadow.GetProperty("shadow").GetBoolean());
        Assert.Equal("general", shadow.GetProperty("profile").GetString());
        Assert.Equal("t-general", shadow.GetProperty("token").GetString());
        Assert.False(shadow.GetProperty("delivered").GetBoolean());
        Assert.Equal("FAIL", shadow.GetProperty("result").GetString());

        Assert.False(enforced.GetProperty("shadow").GetBoolean());
        Assert.Equal("general", enforced.GetProperty("profile").GetString());
        Assert.Equal("t-general", enforced.GetProperty("token").GetString());
        Assert.True(enforced.GetProperty("delivered").GetBoolean());
        Assert.Equal("PASS", enforced.GetProperty("result").GetString());
    }

    /// <summary>With no logs the command says so on one line and exits zero, and creates nothing.</summary>
    [Fact]
    public void Tune_WithNoLogsSaysNoScoredAnswersOnOneLineAndExitsZero()
    {
        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory);

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
        DecisionLog decisions = _host.State.Decisions(ArbiterDirectory);
        await decisions.AppendAsync(Decision("d-7", "ledger-7", ScopeRule, 0, null, shadow: false));

        ArbiterCommandRun execution = RunTune(_host.WorkingDirectory);

        Assert.Equal(0, execution.ExitCode);
        string[] lines = execution.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string line = Assert.Single(lines);
        Assert.Contains(NoScoredAnswers, line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tune only reads. Every log keeps its lines, nothing is appended, and no file or directory appears in the file
    /// system beside them.
    /// </summary>
    [Fact]
    public async Task Tune_WritesNothing()
    {
        await WriteStandardFixtureAsync();
        string[] logs = ["decisions.jsonl", "ledger.jsonl"];
        List<IReadOnlyList<string>> before = [.. logs.Select(name => _host.State.Log.LinesOf(ArbiterDirectory, name))];
        int appends = _host.State.Log.AppendCount;
        int entries = _host.Files.EnumerateEntries(_host.WorkingDirectory).Count();

        ArbiterCommandRun text = RunTune(_host.WorkingDirectory);
        ArbiterCommandRun json = RunTune(_host.WorkingDirectory, "--json");

        Assert.Equal(0, text.ExitCode);
        Assert.Equal(0, json.ExitCode);
        Assert.NotEmpty(before[0]);
        Assert.NotEmpty(before[1]);
        Assert.Equal(appends, _host.State.Log.AppendCount);
        for (int index = 0; index < logs.Length; index++)
        {
            Assert.Equal(before[index], _host.State.Log.LinesOf(ArbiterDirectory, logs[index]));
        }

        Assert.Equal(entries, _host.Files.EnumerateEntries(_host.WorkingDirectory).Count());
        Assert.False(_host.Files.DirectoryExists(ArbiterDirectory), "tune must not create the arbiter directory.");
    }
}

using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The rules read the ledger through the facts this feed supplies: a worker's report
/// completes its task for the tasks that depend on it, and an unfinished dispatch holds
/// its files.
/// </summary>
public sealed class ArbiterLedgerFeedTests : IDisposable
{
    private const string Plan = "docs/plans/x.md";

    private const string PassReport =
        "All done.\nSTATUS: PASS\nRESULT: PASS\n";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-ledger-feed-" + Guid.NewGuid().ToString("N"));

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    public ArbiterLedgerFeedTests() => Directory.CreateDirectory(ArbiterDirectory);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// The answer a rule records when step 0 could not decide it from the ledger and the question went to a model
    /// that did not answer: the prose reading is the model's, never step 0's (ADR 0072).
    /// </summary>
    private const string Asked = ArbiterStep1BatchResult.NotEvaluated;

    /// <summary>Answers the scripted rules with high confidence and leaves every other question unevaluated.</summary>
    private sealed class ScriptedProvider : IArbiterProvider
    {
        public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);

        /// <summary>The facts sent with each call, in call order.</summary>
        public List<ArbiterFactSet> FactsSent { get; } = [];

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            FactsSent.Add(facts);
            return Task.FromResult(new ArbiterStep1BatchResult(
                rules.ToDictionary(
                    rule => rule.Id,
                    rule => Answers.TryGetValue(rule.Id, out string? answer) ? answer : ArbiterStep1BatchResult.NotEvaluated,
                    StringComparer.Ordinal),
                "stub",
                null,
                true,
                rules.ToDictionary(
                    rule => rule.Id,
                    rule => new ArbiterStep1Score(null, 0.95, rule.Ask?.ConfidenceAtLeast),
                    StringComparer.Ordinal)));
        }
    }

    private readonly ScriptedProvider _provider = new();

    private sealed class NoGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    /// <summary>Stands in for the plan: T1 and T5 are free, T2 depends on T1.</summary>
    private sealed class PlanFacts : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification)
        {
            if (!classification.Headers.TryGetValue("TASK", out string? task))
            {
                return facts;
            }

            List<string> dependsOn = task == "T2" ? ["T1"] : [];
            return facts
                .With("plan.exists", true, ArbiterFactLabel.Derived)
                .With("plan.tasks.count", 3, ArbiterFactLabel.Derived)
                .With("plan.development-mode", "test-first", ArbiterFactLabel.Derived)
                .With("plan.task", task, ArbiterFactLabel.Derived)
                .With("plan.task.files", new List<string> { "src/shared.cs" }, ArbiterFactLabel.Derived)
                .With("plan.task.depends-on", dependsOn, ArbiterFactLabel.Derived)
                .With("plan.test-contract.row", "A failing test", ArbiterFactLabel.Derived);
        }
    }

    private ArbiterEvaluator Evaluator() => new(
        _provider,
        _ => null,
        new InFlightLedger(ArbiterDirectory),
        new DecisionLog(ArbiterDirectory),
        new NoGitFacts(),
        new PlanFacts(),
        TimeProvider.System);

    private static KyberWeaveConfig Config() => new()
    {
        Arbiter = new ArbiterConfig { Enabled = true, Rules = ArbiterConfig.ProductDefaults.Rules },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private static string Prompt(string task, string plan = Plan) =>
        $"KYBER-ARBITER: true\nPLAN_FILE: {plan}\nTASK: {task}\n\nWork on {task}.";

    private ArbiterEvent Event(string phase, string target, string prompt, string session = "s1", string? output = null) =>
        new()
        {
            Harness = "factory",
            Phase = phase,
            Target = target,
            Prompt = prompt,
            HarnessCaller = "conductor",
            Session = session,
            Cwd = _root,
            ToolOutput = output,
            IsDispatch = true,
        };

    private Task Dispatch(string target, string prompt, string session = "s1") =>
        Evaluator().EvaluateAsync(Event("pre", target, prompt, session), Config(), null, TestContext.Current.CancellationToken);

    /// <summary>The return of a specialist, whose prompt ran with its headers stripped.</summary>
    private Task Return(string target, string prompt, string report, string session = "s1") =>
        Evaluator().EvaluateAsync(
            Event("post", target, HeaderBlock.Strip(prompt), session, report),
            Config(),
            null,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The last primary decision. A dispatch also writes a <c>delegate.model</c> secondary record
    /// after it, so reading the newest record alone would look at the secondary, not the primary.
    /// </summary>
    private ArbiterDecisionRecord LastPrimary() =>
        new DecisionLog(ArbiterDirectory).ReadAll().Last(record => record.Trigger != "delegate.model");

    private void AssertLastAllowed()
    {
        ArbiterDecisionRecord last = LastPrimary();
        string blockers = string.Join(", ", last.Rules.Where(rule => rule.Effect != "allow").Select(rule => $"{rule.Id}={rule.Answer}"));
        Assert.True(last.Outcome == "allow", $"outcome {last.Outcome}: {blockers}");
    }

    private string Answer(string ruleId) =>
        Assert.Single(LastPrimary().Rules, rule => rule.Id == ruleId).Answer;

    [Fact]
    public async Task WithoutAReturn_DependentTaskWaits()
    {
        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal(Asked, Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task AReturnsReportTail_ReachesTheDependentTasksQuestion()
    {
        // The report carries no RESULT: marker, so the ledger cannot show T1 complete; the model reads the tail.
        await Dispatch("csharp-dev", Prompt("T1"));
        await Return("csharp-dev", Prompt("T1"), "Implemented the parser; all tests pass.");
        await Dispatch("csharp-dev", Prompt("T2"));

        List<string> tails = [.. _provider.FactsSent
            .Select(facts => facts.TryGet("ledger.task-returns", out ArbiterFact? fact) ? fact.Value as IEnumerable<string> : null)
            .Where(value => value is not null)
            .SelectMany(value => value!)];
        string stored = Assert.Single(tails);
        Assert.StartsWith("task: T1\n", stored, StringComparison.Ordinal);
        Assert.Contains("all tests pass", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADryRunReturn_StoresNoReportTail()
    {
        await Dispatch("csharp-dev", Prompt("T1"));
        await Evaluator().EvaluateAsync(
            Event("post", "csharp-dev", HeaderBlock.Strip(Prompt("T1")), "s1", "Implemented the parser."),
            Config(),
            new ArbiterEvaluatorOptions(DryRun: true),
            TestContext.Current.CancellationToken);

        string stores = Path.Combine(ArbiterDirectory, "stores");
        Assert.False(
            Directory.Exists(stores) && Directory.EnumerateDirectories(stores, "returns-*").Any(),
            "A dry run must not store a report tail.");
    }

    [Fact]
    public async Task WorkerReport_CompletesTheTaskForItsDependents()
    {
        await Dispatch("test-dev", Prompt("T1"));
        await Return("test-dev", Prompt("T1"), PassReport);

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal("ready", Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task FailedReport_LeavesTheDependentWaiting()
    {
        await Dispatch("test-dev", Prompt("T1"));
        await Return("test-dev", Prompt("T1"), "STATUS: ESCALATION\nRESULT: FAIL\n");

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal(Asked, Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task ReportForAnotherPlan_DoesNotCount()
    {
        await Dispatch("test-dev", Prompt("T1", "docs/plans/other.md"));
        await Return("test-dev", Prompt("T1", "docs/plans/other.md"), PassReport);

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal(Asked, Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task AnAbsolutePlanPath_IsNoPlanHeader_SoItsReportCountsForNoPlan()
    {
        await Dispatch("test-dev", Prompt("T1", "/repo/" + Plan));
        await Return("test-dev", Prompt("T1", "/repo/" + Plan), PassReport);

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal(Asked, Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task APlanSharingOnlyAPathSuffix_IsAnotherPlan()
    {
        await Dispatch("test-dev", Prompt("T1", "plans/x.md"));
        await Return("test-dev", Prompt("T1", "plans/x.md"), PassReport);

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal(Asked, Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task AnAttestationNamingNoPlan_CountsForNoPlan()
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        DateTimeOffset at = DateTimeOffset.UtcNow;
        await ledger.AppendAsync(
            new ArbiterLedgerEvent(ArbiterRecordId.New(at), at, ArbiterSources.Hook, "factory", "s1", ArbiterLedgerPhases.Post)
            {
                Returns = new ArbiterReturnMarkers(Attested: ["T1=complete"]),
            });

        Assert.Empty(ledger.CompletedTasks(Plan));
        Assert.Equal(["T1"], ledger.CompletedTasks());
    }

    [Fact]
    public async Task UnfinishedDispatch_HoldsItsFiles()
    {
        await Dispatch("test-dev", Prompt("T1"));
        AssertLastAllowed();

        await Dispatch("test-dev", Prompt("T5"));

        Assert.Equal("overlaps-in-flight", Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task FinishedDispatch_ReleasesItsFiles()
    {
        await Dispatch("test-dev", Prompt("T1"));
        AssertLastAllowed();
        await Return("test-dev", Prompt("T1"), PassReport);

        await Dispatch("test-dev", Prompt("T5"));

        Assert.Equal("ready", Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task RefusedDispatch_HoldsNothing()
    {
        // A confident "adds work the task does not describe" from the model refuses the dispatch.
        _provider.Answers["KW-ARB-SCOPE-002"] = "adds-work";
        await Dispatch("csharp-dev", Prompt("T5"));
        _provider.Answers.Clear();
        Assert.Equal("escalate", LastPrimary().Outcome);

        await Dispatch("test-dev", Prompt("T1"));

        Assert.Equal("ready", Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task DispatchInAnotherSession_HoldsNothing()
    {
        await Dispatch("test-dev", Prompt("T1"), session: "other");
        AssertLastAllowed();

        await Dispatch("test-dev", Prompt("T5"), session: "s1");

        Assert.Equal("ready", Answer("KW-ARB-READY-001"));
    }

    [Theory]
    [InlineData("n/a", "Not applicable")]
    [InlineData("N/A", "Not applicable")]
    [InlineData("Not applicable", "Not applicable")]
    [InlineData("none — read-only check", "Not applicable")]
    [InlineData("\u2014", "Not applicable")]
    [InlineData("-", "Not applicable")]
    [InlineData("N/A (docs)", "Not applicable")]
    [InlineData("None \u2014 documentation task", "Not applicable")]
    [InlineData("Not a test task \u2014 read-only verification", "Not applicable")]
    [InlineData("No-test task: closeout", "Not applicable")]
    [InlineData("No RED.", "Not applicable")]
    [InlineData("No product RED.", "Not applicable")]
    [InlineData("not required", "Not applicable")]
    [InlineData("none of the listed tests pass", "none of the listed tests pass")]
    [InlineData("Not a separate RED \u2014 T1 is the evidence", "Not a separate RED \u2014 T1 is the evidence")]
    [InlineData("Test fails with X", "Test fails with X")]
    public void RedEvidenceRequirement_ReadsEveryNoTestSpelling(string cell, string expected)
    {
        PlanDocument document = new(
            "Ready", "test-first", true,
            ContractRows: [new PlanContractRow("T5", RedEvidenceRequired: cell)]);

        Assert.Equal(expected, document.RedEvidenceRequirement("T5"));
    }

    private string WriteReport(string name, string summary, DateTime writtenAt)
    {
        string folder = Path.Combine(_root, ".agent_scratchpad", "T5");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, ReviewJson.Write(new GateReport(GateReport.CurrentSchema, [new GateResult("tests", true, 0, summary)])));
        File.SetLastWriteTimeUtc(path, writtenAt);
        return path;
    }

    /// <summary>
    /// A return whose prompt still carries its routing headers reaches <c>KW-ARB-MODE-002</c> with the task, the mode
    /// and the claimed report, and the two reports on disk decide the answer.
    /// </summary>
    [Theory]
    [InlineData(true, "failing-run-first")]
    [InlineData(false, "no-failing-run")]
    public async Task AReturnIsJudgedByTheTaskGatesTwoReports(bool failedFirst, string expected)
    {
        DateTime earlier = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        if (failedFirst)
        {
            WriteReport("task-gate-red.json", GateReportFacts.RedAcceptedSummaryPrefix + ": 1 test failed", earlier);
        }

        string pass = WriteReport("task-gate.json", "3 passed", earlier.AddMinutes(10));
        await Dispatch("csharp-dev", Prompt("T5"));
        await Evaluator().EvaluateAsync(
            Event("post", "csharp-dev", Prompt("T5"), output: $"{PassReport}TASK_GATE: PASS — agent=csharp-dev gates=1 failed=none skipped=0 report={pass} runs=2\n"),
            Config(),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, Answer("KW-ARB-MODE-002"));
    }

    /// <summary>
    /// A return whose headers the harness stripped names no plan task, so the return-side plan rules answer
    /// <c>no-task</c>. Recovering the task from the paired dispatch would also start <c>KW-ARB-DIFF-001</c>'s model
    /// question on those harnesses, so it is left for a decision of its own.
    /// </summary>
    [Fact]
    public async Task AReturnWithItsHeadersStrippedIsNotJudgedByTheFailingRunRule()
    {
        await Dispatch("csharp-dev", Prompt("T5"));
        await Return("csharp-dev", Prompt("T5"), PassReport);

        Assert.Equal("no-task", Answer("KW-ARB-MODE-002"));
    }

    [Fact]
    public void RedEvidenceRequirement_IsNullWithoutARow()
    {
        PlanDocument document = new("Ready", "test-first", true);

        Assert.Null(document.RedEvidenceRequirement("T5"));
    }

    [Fact]
    public void Markers_ReadThePlainReport()
    {
        ArbiterReturnMarkers? markers = ReturnMarkerParser.Parse(
            "prose\nSTATUS: PASS\nRESULT: PASS\nRED_EVIDENCE: 6 failed\n", readAttestations: false);

        Assert.Equal("PASS", markers?.TaskReview);
        Assert.Equal("PASS", markers?.PlannerStatus);
    }

    [Fact]
    public void Markers_ReadAJsonEnvelope()
    {
        ArbiterReturnMarkers? markers = ReturnMarkerParser.Parse(
            """{"status":"completed","content":[{"type":"text","text":"STATUS: PASS\nRESULT: PASS\n"}]}""",
            readAttestations: false);

        Assert.Equal("PASS", markers?.TaskReview);
    }

    [Fact]
    public void Markers_TakeTheLastValue_SoAQuotedTemplateDoesNotWin()
    {
        ArbiterReturnMarkers? markers = ReturnMarkerParser.Parse(
            "RESULT: FAIL\nthe digest:\nRESULT: PASS\n", readAttestations: false);

        Assert.Equal("PASS", markers?.TaskReview);
    }

    [Fact]
    public void Markers_MidLineTextIsNotAMarker()
    {
        Assert.Null(ReturnMarkerParser.Parse("the line RESULT: PASS is required", readAttestations: false));
    }

    [Fact]
    public void Markers_OnlyAPlannerReturnMayAttest()
    {
        const string output = "STATUS: ESCALATION_RESOLVED\nATTESTED: T1=complete, T2=red\n";

        Assert.Null(ReturnMarkerParser.Parse(output, readAttestations: false)?.Attested);
        Assert.Equal(
            ["T1=complete", "T2=red"],
            ReturnMarkerParser.Parse(output, readAttestations: true)?.Attested);
    }
}

using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The rules read the ledger through the facts this feed supplies: a worker's report
/// completes its task and carries RED evidence to the tasks that depend on it, and an
/// unfinished dispatch holds its files.
/// </summary>
public sealed class ArbiterLedgerFeedTests : IDisposable
{
    private const string Plan = "docs/plans/x.md";

    private const string PassReport =
        "All done.\nSTATUS: PASS\nRESULT: PASS\nRED_EVIDENCE: dotnet test failed 6 of 6\n";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-ledger-feed-" + Guid.NewGuid().ToString("N"));

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    public ArbiterLedgerFeedTests() => Directory.CreateDirectory(ArbiterDirectory);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FirstAnswerProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArbiterStep1BatchResult(
                rules.ToDictionary(rule => rule.Id, rule => rule.Answers[0], StringComparer.Ordinal), "stub", null, true));
    }

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
        new FirstAnswerProvider(),
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

    private void AssertLastAllowed()
    {
        ArbiterDecisionRecord last = new DecisionLog(ArbiterDirectory).ReadAll()[^1];
        string blockers = string.Join(", ", last.Rules.Where(rule => rule.Effect != "allow").Select(rule => $"{rule.Id}={rule.Answer}"));
        Assert.True(last.Outcome == "allow", $"outcome {last.Outcome}: {blockers}");
    }

    private string Answer(string ruleId) =>
        Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll()[^1].Rules, rule => rule.Id == ruleId).Answer;

    [Fact]
    public async Task WithoutAReturn_DependentTaskWaitsAndHasNoRedEvidence()
    {
        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal("waiting", Answer("KW-ARB-READY-001"));
        Assert.Equal("missing", Answer("KW-ARB-MODE-001"));
    }

    [Fact]
    public async Task WorkerReport_CompletesTheTaskAndCarriesRedEvidenceToDependents()
    {
        await Dispatch("test-dev", Prompt("T1"));
        await Return("test-dev", Prompt("T1"), PassReport);

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal("ready", Answer("KW-ARB-READY-001"));
        Assert.Equal("present", Answer("KW-ARB-MODE-001"));
    }

    [Fact]
    public async Task FailedReport_LeavesTheDependentWaiting()
    {
        await Dispatch("test-dev", Prompt("T1"));
        await Return("test-dev", Prompt("T1"), "STATUS: ESCALATION\nRESULT: FAIL\nRED_EVIDENCE: none\n");

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal("waiting", Answer("KW-ARB-READY-001"));
        Assert.Equal("missing", Answer("KW-ARB-MODE-001"));
    }

    [Fact]
    public async Task ReportForAnotherPlan_DoesNotCount()
    {
        await Dispatch("test-dev", Prompt("T1", "docs/plans/other.md"));
        await Return("test-dev", Prompt("T1", "docs/plans/other.md"), PassReport);

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal("waiting", Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task AnAbsolutePlanPath_IsNoPlanHeader_SoItsReportCountsForNoPlan()
    {
        await Dispatch("test-dev", Prompt("T1", "/repo/" + Plan));
        await Return("test-dev", Prompt("T1", "/repo/" + Plan), PassReport);

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal("waiting", Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public async Task APlanSharingOnlyAPathSuffix_IsAnotherPlan()
    {
        await Dispatch("test-dev", Prompt("T1", "plans/x.md"));
        await Return("test-dev", Prompt("T1", "plans/x.md"), PassReport);

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal("waiting", Answer("KW-ARB-READY-001"));
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

    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("n/a")]
    [InlineData("N/A \u2014 docs only")]
    public async Task ReportWithoutEvidence_IsNotRedEvidence(string value)
    {
        await Dispatch("test-dev", Prompt("T1"));
        await Return("test-dev", Prompt("T1"), $"STATUS: PASS\nRESULT: PASS\nRED_EVIDENCE: {value}\n");

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal("missing", Answer("KW-ARB-MODE-001"));
    }

    [Fact]
    public async Task EvidenceThatStartsWithNone_IsStillEvidence()
    {
        await Dispatch("test-dev", Prompt("T1"));
        await Return("test-dev", Prompt("T1"), "STATUS: PASS\nRESULT: PASS\nRED_EVIDENCE: none of the 6 tests pass\n");

        await Dispatch("csharp-dev", Prompt("T2"));

        Assert.Equal("present", Answer("KW-ARB-MODE-001"));
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
        await Dispatch("csharp-dev", Prompt("T5"));
        Assert.Equal("escalate", new DecisionLog(ArbiterDirectory).ReadAll()[^1].Outcome);

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

    [Fact]
    public void RedEvidenceRequirement_IsNullWithoutARow()
    {
        PlanDocument document = new("Ready", "test-first", true);

        Assert.Null(document.RedEvidenceRequirement("T5"));
    }

    [Fact]
    public void NoTestRow_IsExemptFromMode001()
    {
        ArbiterRule mode = Assert.Single(ArbiterConfig.ProductDefaults.Rules, rule => rule.Id == "KW-ARB-MODE-001");
        ArbiterFactSet facts = new ArbiterFactSet()
            .With("plan.development-mode", "test-first", ArbiterFactLabel.Derived)
            .With("delegation.target", "typescript-dev", ArbiterFactLabel.Derived)
            .With("plan.test-contract.row", "Not applicable", ArbiterFactLabel.Derived);

        Assert.Equal("not-required", RuleEngine.Decide(mode, facts));
    }

    [Fact]
    public void Markers_ReadThePlainReport()
    {
        ArbiterReturnMarkers? markers = ReturnMarkerParser.Parse(
            "prose\nSTATUS: PASS\nRESULT: PASS\nRED_EVIDENCE: 6 failed\n", readAttestations: false);

        Assert.Equal("PASS", markers?.TaskReview);
        Assert.Equal("6 failed", markers?.RedEvidence);
        Assert.Equal("PASS", markers?.PlannerStatus);
    }

    [Fact]
    public void Markers_BlankRedEvidenceIsAbsent()
    {
        Assert.Null(ReturnMarkerParser.Parse("RED_EVIDENCE:   \n", readAttestations: false));
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

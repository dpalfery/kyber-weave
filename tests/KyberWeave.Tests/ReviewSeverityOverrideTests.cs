using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The host's severity overrides: the <c>Severity overrides</c> table in the review guidance, its validation, which
/// rows reach which seat, how the verdict applies a cited row, and the optional decision-model match.
/// </summary>
public sealed class ReviewSeverityOverrideTests : IDisposable
{
    private const string Table = """
        # Review guidance

        ## Severity overrides

        | Id | Finding class | Severity | Applies to | Why |
        |---|---|---|---|---|
        | SO-1 | File or type placed against the documented project, layer or folder structure | major | model-placement, csharp | Placement is maintainability |
        | SO-2 | Missing XML doc comment on a private member | nitpick | | Style only |
        | SO-3 | Workflow step without a pinned action | critical | github-actions | Supply chain |

        ## Technology map

        | Technology | Files | Take these sections |
        |---|---|---|
        | csharp | `*.cs`, `*.csproj` | 2 |
        | github-actions | `.github/workflows/*.yml` | 7 |
        | azure | Azure resource definitions | 12 |
        """;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---------------------------------------------------------------- parser

    [Fact]
    public void AGuidanceFileWithNoSectionHasNoRows()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse("# Review guidance\n\n## Ground rules\n\n| a | b |\n|---|---|\n| x | y |\n");

        Assert.Empty(overrides.Rows);
        Assert.Empty(overrides.Problems);
    }

    [Fact]
    public void EachRowIsReadWithItsIdSeverityScopeAndReason()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse(Table);

        Assert.Empty(overrides.Problems);
        Assert.Equal(["SO-1", "SO-2", "SO-3"], overrides.Rows.Select(row => row.Id));
        SeverityOverrideRow placement = overrides.Rows[0];
        Assert.Equal(ReviewSeverity.Major, placement.Severity);
        Assert.Equal(["model-placement"], placement.Lenses);
        Assert.Equal(["csharp"], placement.Technologies);
        Assert.Equal("Placement is maintainability", placement.Why);
        Assert.Equal(7, placement.Line);
        Assert.Empty(overrides.Rows[1].Lenses);
        Assert.Empty(overrides.Rows[1].Technologies);
        Assert.Equal(ReviewSeverity.Nitpick, overrides.Rows[1].Severity);
    }

    [Fact]
    public void ACommentedOrFencedExampleIsNotARow()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse("""
            ## Severity overrides

            | Id | Finding class | Severity | Applies to | Why |
            |---|---|---|---|---|
            <!-- | SO-1 | Missing XML doc comment | nitpick | | Style | -->
            <!--
            | SO-2 | Another | major | | x |
            -->

            ```markdown
            | SO-3 | Fenced | major | | x |
            ```
            """);

        Assert.Empty(overrides.Rows);
        Assert.Empty(overrides.Problems);
    }

    [Fact]
    public void ColumnsAreFoundByHeaderAndARowWithNoIdGetsItsPosition()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse("""
            ### Severity overrides

            | Severity | Finding class |
            |---|---|
            | MAJOR | Public API change without a documentation update |
            """);

        SeverityOverrideRow row = Assert.Single(overrides.Rows);
        Assert.Equal("row-1", row.Id);
        Assert.Equal(ReviewSeverity.Major, row.Severity);
        Assert.Equal("Public API change without a documentation update", row.FindingClass);
    }

    // ---------------------------------------------------------------- validation

    [Theory]
    [InlineData("| SO-1 | Some class | blocker | | |", SeverityOverrides.UnknownSeverityCode)]
    [InlineData("| SO-1 | Some class | never-report | | |", SeverityOverrides.UnknownSeverityCode)]
    [InlineData("| SO-1 |  | major | | |", SeverityOverrides.EmptyClassCode)]
    [InlineData("| SO-1 | Some class | major | model-placment | |", SeverityOverrides.UnknownScopeCode)]
    public void AnInvalidRowIsReportedAsAnErrorAndLeftOut(string row, string code)
    {
        SeverityOverrides overrides = SeverityOverrides.Parse(Section(row));

        Assert.Empty(overrides.Rows);
        Diagnostic problem = Assert.Single(overrides.Problems);
        Assert.Equal(code, problem.Code);
        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Equal(5, problem.StartLine);
    }

    [Fact]
    public void ADuplicateIdIsReportedAndOnlyTheFirstRowIsKept()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse(Section("| SO-1 | One class | major | | |", "| so-1 | Two class | minor | | |"));

        Assert.Equal("One class", Assert.Single(overrides.Rows).FindingClass);
        Assert.Equal(SeverityOverrides.DuplicateIdCode, Assert.Single(overrides.Problems).Code);
    }

    [Fact]
    public void ATableWithoutTheRequiredColumnsIsReported()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse("## Severity overrides\n\n| Id | Why |\n|---|---|\n| SO-1 | x |\n");

        Assert.Empty(overrides.Rows);
        Assert.Equal(SeverityOverrides.EmptyClassCode, Assert.Single(overrides.Problems).Code);
    }

    [Fact]
    public void LoweringAClassTheShippedLensGradesCriticalIsAnAdvisoryAndTheRowStillApplies()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse(Section(
            "| SO-1 | A described capability with no implementation | minor | intent-alignment | |"));

        Assert.Single(overrides.Rows);
        Diagnostic advisory = Assert.Single(overrides.Problems);
        Assert.Equal(SeverityOverrides.LowersCriticalCode, advisory.Code);
        Assert.Equal(Severity.Warning, advisory.Severity);
    }

    [Fact]
    public void ThePlacementRowDrawsNoAdvisory()
    {
        Assert.Empty(SeverityOverrides.Parse(Table).Problems);
    }

    // ---------------------------------------------------------------- delivery to seats

    [Fact]
    public void AnUnscopedRowReachesEverySeatAndAScopedRowOnlyItsLensWhenItsTechnologyChanged()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse(Table);
        string[] changed = ["src/Foo.cs", "docs/a.md"];

        Assert.Equal(["SO-1", "SO-2"], overrides.RowsFor("model-placement", changed).Select(row => row.Id));
        Assert.Equal(["SO-2"], overrides.RowsFor("security", changed).Select(row => row.Id));
        Assert.Equal(["SO-2"], overrides.RowsFor("model-placement", ["docs/a.md"]).Select(row => row.Id));
        Assert.Equal(["SO-2", "SO-3"], overrides.RowsFor("security", [".github/workflows/ci.yml"]).Select(row => row.Id));
    }

    [Fact]
    public void DeliveryFailsOpenWhenTheChangedFilesOrTheTechnologyGlobsAreUnknown()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse(Table.Replace("| csharp | `*.cs`, `*.csproj` | 2 |", "| csharp | C# sources | 2 |", StringComparison.Ordinal));

        Assert.Equal(["SO-1", "SO-2"], overrides.RowsFor("model-placement", ["docs/a.md"]).Select(row => row.Id));
        Assert.Equal(["SO-2", "SO-3"], SeverityOverrides.Parse(Table).RowsFor("security", null).Select(row => row.Id));
    }

    [Fact]
    public void TheSeatPacketListsOnlyTheRowsThatSeatIsGiven()
    {
        CouncilSeatContext context = new(
            "/tree", "/evidence.md", true, "balanced", 7, "minor", "/tree/REVIEW.md", null,
            SeverityOverrides.Parse(Table), ["src/Foo.cs"]);

        string placement = CouncilSeating.Packet(context, "model-placement");
        string security = CouncilSeating.Packet(context, "security");

        Assert.Contains("- Severity overrides (", placement, StringComparison.Ordinal);
        Assert.Contains("  - SO-1: major — File or type placed against the documented project, layer or folder structure (why: Placement is maintainability)", placement, StringComparison.Ordinal);
        Assert.Contains("write `override: <id>`", placement, StringComparison.Ordinal);
        Assert.DoesNotContain("SO-1", security, StringComparison.Ordinal);
        Assert.Contains("  - SO-2: nitpick", security, StringComparison.Ordinal);
    }

    [Fact]
    public void APacketWithNoOverridesSaysNothingAboutThem()
    {
        CouncilSeatContext context = new("/tree", "/evidence.md", true, "balanced", 7, "minor", null, null);

        Assert.DoesNotContain("Severity overrides", CouncilSeating.Packet(context, "security"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- seat schema

    [Fact]
    public void ASeatFindingCarriesTheOverrideItCites()
    {
        SeatOutput seat = SeatOutputParser.Parse("model-placement", SeatFile("major", "  override: SO-1"));

        Assert.Equal("SO-1", Assert.Single(seat.Findings).Override);
    }

    [Fact]
    public void AnOverrideKeyUnderNoFindingsIsRefusedLikeAnyFindingKey()
    {
        Assert.Throws<SeatOutputException>(() => SeatOutputParser.Parse(
            "model-placement", "NO FINDINGS: clean.\noverride: SO-1\nREVIEW.md sections checked: [§1]\n"));
    }

    // ---------------------------------------------------------------- verdict application

    [Fact]
    public void ACitedRowSetsTheSeverityAndRecordsTheSeatsOwn()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse(Table);
        List<Diagnostic> notes = [];

        ReviewFinding applied = Assert.Single(overrides.ApplyCited([Finding("model-placement/wrong-folder", ReviewSeverity.Minor) with { Override = "SO-1" }], notes));

        Assert.Equal(ReviewSeverity.Major, applied.Severity);
        Assert.Equal("SO-1", applied.Override);
        Assert.Equal(SeverityOverrides.SeatSource, applied.OverrideSource);
        Assert.Equal(ReviewSeverity.Minor, applied.SeatSeverity);
        Diagnostic note = Assert.Single(notes);
        Assert.Equal(SeverityOverrides.AppliedCode, note.Code);
        Assert.Equal(
            "'model-placement/wrong-folder': severity set by host override SO-1 (minor → major; cited by the seat, which graded it differently).",
            note.Message);
    }

    [Fact]
    public void AnUnknownCitedIdIsAWarningAndTheSeatsSeverityStands()
    {
        List<Diagnostic> notes = [];

        ReviewFinding kept = Assert.Single(SeverityOverrides.Parse(Table).ApplyCited([Finding("a/b", ReviewSeverity.Minor) with { Override = "SO-9" }], notes));

        Assert.Equal(ReviewSeverity.Minor, kept.Severity);
        Assert.Null(kept.Override);
        Assert.Equal(Severity.Warning, Assert.Single(notes).Severity);
    }

    [Fact]
    public void AnOverrideAppliedInAnEarlierCycleIsNotAppliedAgain()
    {
        List<Diagnostic> notes = [];
        ReviewFinding carried = Finding("a/b", ReviewSeverity.Major) with { Override = "SO-1", OverrideSource = SeverityOverrides.ModelSource, SeatSeverity = ReviewSeverity.Minor };

        Assert.Same(carried, Assert.Single(SeverityOverrides.Parse(Table).ApplyCited([carried], notes)));
        Assert.Empty(notes);
    }

    // ---------------------------------------------------------------- decision model (optional)

    [Fact]
    public async Task AConfidentMatchGradesAFindingThatCitedNoRow()
    {
        FakeProvider provider = new(_ => ("belongs", 0.95));

        (List<ReviewFinding> findings, var matched) = await ReviewVerdictDecisions.MatchOverridesAsync(
            Model(provider), SeverityOverrides.Parse(Table), [Finding("model-placement/wrong-folder", ReviewSeverity.Minor)]);

        ReviewFinding graded = Assert.Single(findings);
        Assert.Equal(ReviewSeverity.Major, graded.Severity);
        Assert.Equal(SeverityOverrides.ModelSource, graded.OverrideSource);
        Assert.Equal("SO-1", Assert.Single(matched).Row.Id);
        Assert.Equal(2, provider.Calls); // SO-1 (model-placement, a .cs file) and SO-2 (every seat); SO-3 is github-actions only.
    }

    [Fact]
    public async Task AnUnsureAnswerChangesNothing()
    {
        (List<ReviewFinding> findings, var matched) = await ReviewVerdictDecisions.MatchOverridesAsync(
            Model(new FakeProvider(_ => ("belongs", 0.6))), SeverityOverrides.Parse(Table), [Finding("model-placement/x", ReviewSeverity.Minor)]);

        Assert.Equal(ReviewSeverity.Minor, Assert.Single(findings).Severity);
        Assert.Empty(matched);
    }

    [Fact]
    public async Task OverrideQuestionsAreCappedAndTheSkippedComparisonsAreReported()
    {
        FakeProvider provider = new(_ => ("no", 0.99));
        ReviewDecisionModel model = Model(provider);
        ReviewFinding[] many = [.. Enumerable.Range(1, 160).Select(index => Finding($"model-placement/x{index}", ReviewSeverity.Minor))];

        await ReviewVerdictDecisions.MatchOverridesAsync(model, SeverityOverrides.Parse(Table), many);

        Assert.Equal(ReviewVerdictDecisions.MaxQuestionsPerStep, provider.Calls);
        Assert.Contains("20 comparison(s) skipped", model.SummaryLine([]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheQuestionCapIsReachedTheMostSevereFindingsAreAskedFirst()
    {
        FakeProvider provider = new(_ => ("belongs", 0.95));
        // 150 minor findings, then 10 critical ones the seats listed last; two rows apply to each, so 300 questions
        // cover 150 findings and the ten critical ones must be among them.
        ReviewFinding[] findings =
        [
            .. Enumerable.Range(0, 150).Select(index => Finding($"model-placement/m{index}", ReviewSeverity.Minor)),
            .. Enumerable.Range(0, 10).Select(index => Finding($"model-placement/c{index}", ReviewSeverity.Critical)),
        ];

        (List<ReviewFinding> graded, _) = await ReviewVerdictDecisions.MatchOverridesAsync(Model(provider), SeverityOverrides.Parse(Table), findings);

        Assert.Equal(ReviewVerdictDecisions.MaxQuestionsPerStep, provider.Calls);
        Assert.All(graded.Skip(150), finding => Assert.Equal(SeverityOverrides.ModelSource, finding.OverrideSource));
        Assert.Equal(SeverityOverrides.ModelSource, graded[0].OverrideSource);
        Assert.Equal(SeverityOverrides.ModelSource, graded[139].OverrideSource);
        Assert.Null(graded[140].OverrideSource);
        Assert.Null(graded[149].OverrideSource);
    }

    [Fact]
    public async Task OnlyTheStepsFindingLimitIsLookedAtAndTheRestAreReportedAsLeftOut()
    {
        FakeProvider provider = new(_ => ("no", 0.99));
        ReviewDecisionModel model = Model(provider);
        ReviewFinding[] many = [.. Enumerable.Range(0, ReviewVerdictDecisions.MaxFindingsPerStep + 100).Select(index => Finding($"model-placement/x{index}", ReviewSeverity.Minor))];

        await ReviewVerdictDecisions.MatchOverridesAsync(model, SeverityOverrides.Parse(Table), many);

        Assert.Equal(ReviewVerdictDecisions.MaxQuestionsPerStep, provider.Calls);
        Assert.Contains("100 finding(s) left out of the comparison", model.SummaryLine([]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFaultChangesNothingAndStopsFurtherCalls()
    {
        FakeProvider provider = new(_ => ("belongs", 1.0)) { Fault = true };
        ReviewDecisionModel model = Model(provider);
        ReviewFinding[] many = [.. Enumerable.Range(1, 6).Select(index => Finding($"model-placement/x{index}", ReviewSeverity.Minor))];

        (List<ReviewFinding> findings, var matched) = await ReviewVerdictDecisions.MatchOverridesAsync(model, SeverityOverrides.Parse(Table), many);

        Assert.All(findings, finding => Assert.Equal(ReviewSeverity.Minor, finding.Severity));
        Assert.Empty(matched);
        Assert.True(model.Faulted);
        Assert.True(provider.Calls <= 4, "one wave of requests at most before the breaker stops the run");
    }

    [Fact]
    public async Task ProviderNoneAsksNothing()
    {
        ReviewDecisionModel model = new(new NoneProvider(), ArbiterConfig.ProductDefaults.Rules, LogDirectory, TimeProvider.System);

        (List<ReviewFinding> findings, var matched) = await ReviewVerdictDecisions.MatchOverridesAsync(
            model, SeverityOverrides.Parse(Table), [Finding("model-placement/x", ReviewSeverity.Minor)]);

        Assert.Equal(ReviewSeverity.Minor, Assert.Single(findings).Severity);
        Assert.Empty(matched);
    }

    [Fact]
    public async Task TheMostConfidentRowWinsAndATieGoesToTheRowFirstInTheTable()
    {
        SeverityOverrides overrides = SeverityOverrides.Parse(Table);
        ReviewFinding finding = Finding("model-placement/x", ReviewSeverity.Minor);

        (List<ReviewFinding> confident, _) = await ReviewVerdictDecisions.MatchOverridesAsync(
            Model(new FakeProvider(state => ("belongs", state.Contains("XML doc", StringComparison.Ordinal) ? 0.99 : 0.9))), overrides, [finding]);
        (List<ReviewFinding> tied, _) = await ReviewVerdictDecisions.MatchOverridesAsync(
            Model(new FakeProvider(_ => ("belongs", 0.9))), overrides, [finding]);

        Assert.Equal("SO-2", Assert.Single(confident).Override);
        Assert.Equal("SO-1", Assert.Single(tied).Override);
    }

    [Fact]
    public async Task AFindingThatCitesARowIsNotAskedAndAnOverriddenMinorIsNeverDroppedAsANitpick()
    {
        FakeProvider provider = new(_ => ("belongs", 0.99));
        ReviewFinding cited = Finding("model-placement/x", ReviewSeverity.Major) with { Override = "SO-1" };

        await ReviewVerdictDecisions.MatchOverridesAsync(Model(provider), SeverityOverrides.Parse(Table), [cited]);
        Assert.Equal(0, provider.Calls);

        ReviewFinding overridden = Finding("a/minor", ReviewSeverity.Minor) with { Override = "SO-9", OverrideSource = SeverityOverrides.SeatSource };
        FakeProvider nitpicker = new(_ => ("nitpick", 0.99));
        (List<ReviewFinding> kept, var nitpicks) = await ReviewVerdictDecisions.DropNitpicksAsync(Model(nitpicker), [overridden]);
        Assert.Single(kept);
        Assert.Empty(nitpicks);
        Assert.Equal(0, nitpicker.Calls);
    }

    // ---------------------------------------------------------------- helpers

    private string LogDirectory => Path.Combine(_temp.Path, "artifacts", "arbiter");

    private ReviewDecisionModel Model(IArbiterProvider provider) =>
        new(provider, ArbiterConfig.ProductDefaults.Rules, LogDirectory, TimeProvider.System);

    private static string Section(params string[] rows) =>
        "## Severity overrides\n\n| Id | Finding class | Severity | Applies to | Why |\n|---|---|---|---|---|\n" + string.Join('\n', rows) + "\n";

    private static ReviewFinding Finding(string id, ReviewSeverity severity) =>
        new(id, id[..id.IndexOf('/', StringComparison.Ordinal)], severity, 8, "src/Foo.cs", 10, "class Foo", "Foo sits in the wrong project.", "evidence", "A reader looks for Foo in its layer.");

    private static string SeatFile(string severity, string extra) => $"""
        ```yaml
        - id: model-placement/wrong-folder
          lens: model-placement
          severity: {severity}
          confidence: 9
          file: src/Foo.cs
          line: 1
          excerpt: "class Foo"
          claim: Foo sits in the wrong project.
          evidence: docs/catalog.md
          failure_scenario: A reader looks for Foo in its layer.
        {extra}
        ```
        Discovery: codegraph 1, docs 1, direct reads 0
        REVIEW.md sections checked: [§1]
        """;

    /// <summary>Answers each question from its state text, as the real client does: below the floor is undecidable.</summary>
    private sealed class FakeProvider(Func<string, (string Choice, double Confidence)> answer) : IArbiterProvider
    {
        private readonly Lock _gate = new();

        public bool Fault { get; init; }

        public int Calls { get; private set; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                Calls++;

            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                answers[rule.Id] = RuleEngine.Undecidable;
                if (Fault)
                    continue;

                string state = string.Join('\n', rule.Ask!.State.Values
                    .Select(name => facts.TryGet(name, out ArbiterFact? fact) ? fact.Value?.ToString() : null));
                (string choice, double confidence) = answer(state);
                scores[rule.Id] = new ArbiterStep1Score(null, confidence, rule.Ask.ConfidenceAtLeast);
                if (rule.Answers.Contains(choice) && confidence >= (rule.Ask.ConfidenceAtLeast ?? 0))
                    answers[rule.Id] = choice;
            }

            return Task.FromResult(Fault
                ? new ArbiterStep1BatchResult(answers, null, null, true)
                : new ArbiterStep1BatchResult(answers, "jev-1.14.0", null, true, scores));
        }
    }
}

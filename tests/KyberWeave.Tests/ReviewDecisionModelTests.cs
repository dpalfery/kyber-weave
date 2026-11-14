using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The review commands' decision-model steps, against a fake provider that answers as the real client does: a choice
/// below its rule's floor is undecidable, a <c>noul</c> carries the probability of the rule's first answer, and a fault
/// answers every question undecidable with no scores. No step may change a review when the model is absent or unsure.
/// </summary>
public sealed class ReviewDecisionModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---------------------------------------------------------------- lens applicability (review seats)

    [Fact]
    public async Task AConfidentNotApplicableLeavesTheLensUnseatedWithItsConfidence()
    {
        FakeProvider provider = new() { Noul = 0.02 };
        ReviewDecisionModel model = Model(provider);

        IReadOnlyDictionary<string, string> notSeated = await ReviewLensApplicability.NotApplicableAsync(
            model, ["security", "performance"], "src/\n  src/a.cs +1 -0");

        Assert.Equal("decision model: not applicable (confidence 0.98)", notSeated["security"]);
        Assert.Equal(2, notSeated.Count);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task AnUnsureApplicabilityAnswerSeatsTheLens()
    {
        ReviewDecisionModel model = Model(new FakeProvider { Noul = 0.5 });

        IReadOnlyDictionary<string, string> notSeated = await ReviewLensApplicability.NotApplicableAsync(model, ["security"], "x");

        Assert.Empty(notSeated);
    }

    [Fact]
    public async Task CorrectnessTestAdequacyAndTheGateReadingLensesAreNeverAsked()
    {
        FakeProvider provider = new() { Noul = 0.0 };

        IReadOnlyDictionary<string, string> notSeated = await ReviewLensApplicability.NotApplicableAsync(
            Model(provider), ["correctness", "test-adequacy", "duplicate-implementation", "static-analysis-triage"], "x");

        Assert.Empty(notSeated);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task AFaultSeatsEveryLensAndStopsFurtherCalls()
    {
        FakeProvider provider = new() { Fault = true };
        ReviewDecisionModel model = Model(provider);

        IReadOnlyDictionary<string, string> notSeated = await ReviewLensApplicability.NotApplicableAsync(
            model, ["security", "performance", "supportability", "prior-art", "authz-tenancy", "intent-alignment"], "x");

        Assert.Empty(notSeated);
        Assert.True(model.Faulted);
        Assert.True(provider.Calls <= 4, "one wave of requests at most before the breaker stops the run");
        Assert.Contains("stopped after a provider fault", model.SummaryLine([]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderNoneAsksNothingAndPrintsNothing()
    {
        ReviewDecisionModel model = new(new NoneProvider(), ArbiterConfig.ProductDefaults.Rules, LogDirectory, TimeProvider.System);

        IReadOnlyDictionary<string, string> notSeated = await ReviewLensApplicability.NotApplicableAsync(model, ["security"], "x");

        Assert.Empty(notSeated);
        Assert.False(model.Configured);
        Assert.Null(model.SummaryLine(["1 lens(es) not seated"]));
        Assert.False(Directory.Exists(LogDirectory));
    }

    [Fact]
    public void AConfiguredModelThatCannotBeUsedSaysSoOnce()
    {
        ReviewDecisionModel model = new(null, [], null, TimeProvider.System, "no key for https://example.test");

        Assert.Equal("decision model: not configured (no key for https://example.test)", model.SummaryLine([]));
    }

    // ---------------------------------------------------------------- nitpicks (review verdict)

    [Fact]
    public async Task AConfidentNitpickDropsAMinorFinding()
    {
        ReviewDecisionModel model = Model(new FakeProvider { Choice = "nitpick", Confidence = 0.95 });

        (List<ReviewFinding> kept, List<(ReviewFinding Finding, double? Confidence)> nitpicks) =
            await ReviewVerdictDecisions.DropNitpicksAsync(model, [Finding("a/minor", ReviewSeverity.Minor)]);

        Assert.Empty(kept);
        Assert.Equal("a/minor", Assert.Single(nitpicks).Finding.Id);
    }

    [Fact]
    public async Task MajorAndCriticalFindingsAreNeverAskedOrDroppedAsNitpicks()
    {
        FakeProvider provider = new() { Choice = "nitpick", Confidence = 1.0 };

        (List<ReviewFinding> kept, List<(ReviewFinding Finding, double? Confidence)> nitpicks) =
            await ReviewVerdictDecisions.DropNitpicksAsync(
                Model(provider), [Finding("a/major", ReviewSeverity.Major), Finding("a/critical", ReviewSeverity.Critical)]);

        Assert.Equal(2, kept.Count);
        Assert.Empty(nitpicks);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("nitpick", 0.6, false)]
    [InlineData("substantive", 0.99, false)]
    [InlineData("nitpick", 0.99, true)]
    public async Task OnlyAnAnswerAtTheFloorDropsANitpick(string choice, double confidence, bool dropped)
    {
        ReviewDecisionModel model = Model(new FakeProvider { Choice = choice, Confidence = confidence });

        (_, List<(ReviewFinding Finding, double? Confidence)> nitpicks) =
            await ReviewVerdictDecisions.DropNitpicksAsync(model, [Finding("a/minor", ReviewSeverity.Minor)]);

        Assert.Equal(dropped, nitpicks.Count == 1);
    }

    [Fact]
    public async Task ANitpickRuleInShadowLogsButDropsNothing()
    {
        IReadOnlyList<ArbiterRule> rules = [.. ArbiterConfig.ProductDefaults.Rules
            .Select(rule => rule.Id == ReviewVerdictDecisions.NitpickRule ? rule with { Shadow = true } : rule)];
        ReviewDecisionModel model = new(new FakeProvider { Choice = "nitpick", Confidence = 0.99 }, rules, LogDirectory, TimeProvider.System);

        (List<ReviewFinding> kept, _) = await ReviewVerdictDecisions.DropNitpicksAsync(model, [Finding("a/minor", ReviewSeverity.Minor)]);

        Assert.Single(kept);
        Assert.Equal("shadow: would drop", Assert.Single(model.Decisions).Effect);
        Assert.True(File.Exists(Path.Combine(LogDirectory, "decisions.jsonl")));
    }

    [Fact]
    public async Task AFaultKeepsEveryFinding()
    {
        ReviewDecisionModel model = Model(new FakeProvider { Fault = true });

        (List<ReviewFinding> kept, _) = await ReviewVerdictDecisions.DropNitpicksAsync(model, [Finding("a/minor", ReviewSeverity.Minor)]);

        Assert.Single(kept);
    }

    // ---------------------------------------------------------------- duplicates (review verdict)

    [Fact]
    public async Task AConfidentDuplicateIsMergedIntoTheHigherSeverityFinding()
    {
        ReviewDecisionModel model = Model(new FakeProvider { Choice = "same-defect", Confidence = 0.97 });

        (List<ReviewFinding> kept, List<MergedFindings> merged) = await ReviewVerdictDecisions.MergeDuplicatesAsync(
            model, [Finding("intent/b", ReviewSeverity.Minor, line: 16), Finding("docs/a", ReviewSeverity.Major, line: 16)]);

        Assert.Equal("docs/a", Assert.Single(kept).Id);
        MergedFindings group = Assert.Single(merged);
        Assert.Equal("intent/b", Assert.Single(group.Merged).Finding.Id);
    }

    [Fact]
    public async Task FindingsFarApartOrInOtherFilesAreNotAskedAboutAsDuplicates()
    {
        FakeProvider provider = new() { Choice = "same-defect", Confidence = 1.0 };

        (List<ReviewFinding> kept, _) = await ReviewVerdictDecisions.MergeDuplicatesAsync(
            Model(provider), [Finding("a", ReviewSeverity.Major, line: 1), Finding("b", ReviewSeverity.Major, line: 300), Finding("c", ReviewSeverity.Major, file: "other.cs")]);

        Assert.Equal(3, kept.Count);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("same-defect", 0.5)]
    [InlineData("different-defect", 0.99)]
    public async Task AnUnsureOrDifferentAnswerKeepsBothFindings(string choice, double confidence)
    {
        ReviewDecisionModel model = Model(new FakeProvider { Choice = choice, Confidence = confidence });

        (List<ReviewFinding> kept, List<MergedFindings> merged) = await ReviewVerdictDecisions.MergeDuplicatesAsync(
            model, [Finding("a", ReviewSeverity.Major), Finding("b", ReviewSeverity.Minor)]);

        Assert.Equal(2, kept.Count);
        Assert.Empty(merged);
    }

    // ---------------------------------------------------------------- claim check (review verdict --candidates)

    [Fact]
    public async Task AConfidentSupportedClaimNeedsNoRefuter()
    {
        string root = Repository();
        ReviewDecisionModel model = Model(new FakeProvider { Choice = "supports", Confidence = 0.95 });

        ClaimCheck check = Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(model, root, [Finding("a", ReviewSeverity.Major)]));

        Assert.False(check.NeedsRefuter);
    }

    [Fact]
    public async Task AConfidentContradictedClaimStillGoesToTheRefuter()
    {
        string root = Repository();
        ReviewDecisionModel model = Model(new FakeProvider { Choice = "contradicts", Confidence = 0.99 });

        ClaimCheck check = Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(model, root, [Finding("a", ReviewSeverity.Major)]));

        Assert.True(check.NeedsRefuter);
        Assert.Contains("the refuter decides", check.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsureClaimAnswerGoesToTheRefuter()
    {
        string root = Repository();
        ReviewDecisionModel model = Model(new FakeProvider { Choice = "supports", Confidence = 0.6 });

        Assert.True(Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(model, root, [Finding("a", ReviewSeverity.Major)])).NeedsRefuter);
    }

    [Fact]
    public async Task AQuoteThatIsNotInTheFileIsNotAskedAndGoesToTheRefuter()
    {
        string root = Repository();
        FakeProvider provider = new() { Choice = "supports", Confidence = 1.0 };

        ClaimCheck check = Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(
            Model(provider), root, [Finding("a", ReviewSeverity.Major) with { Excerpt = "code that is not there" }]));

        Assert.True(check.NeedsRefuter);
        Assert.Equal(0, provider.Calls);
        Assert.Contains("excerpt is not at the cited lines", check.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClaimQuestionReadsTheLinesTheEvidenceCites()
    {
        string root = Repository();
        await File.WriteAllTextAsync(Path.Combine(root, "other.md"), "one\nstatus: archived\nthree\n");
        FakeProvider provider = new() { Choice = "supports", Confidence = 0.95 };

        await ReviewVerdictDecisions.CheckClaimsAsync(
            Model(provider), root, [Finding("a", ReviewSeverity.Major) with { Evidence = "other.md line 2 says otherwise" }]);

        Assert.Contains("other.md:2: status: archived", provider.LastStateText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("see a.cs:9999999999")]
    [InlineData("see a.cs:1-99999999999")]
    [InlineData("a.cs lines 4 and 99999999999")]
    public async Task AnEvidenceCitationThatIsNotAnIntegerIsIgnoredNotFatal(string evidence)
    {
        string root = Repository();
        FakeProvider provider = new() { Choice = "supports", Confidence = 0.95 };

        ClaimCheck check = Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(
            Model(provider), root, [Finding("a", ReviewSeverity.Major) with { Evidence = evidence }]));

        Assert.False(check.NeedsRefuter);
    }

    // ---------------------------------------------------------------- severity note (review verdict)

    [Theory]
    [InlineData("too-high", 0.9, "high")]
    [InlineData("too-low", 0.9, "low")]
    [InlineData("about-right", 0.99, null)]
    [InlineData("too-high", 0.5, null)]
    public async Task OnlyAConfidentMismatchIsNoted(string choice, double confidence, string? direction)
    {
        ReviewDecisionModel model = Model(new FakeProvider { Choice = choice, Confidence = confidence });

        IReadOnlyList<(ReviewFinding Finding, string Direction, double? Confidence)> notes =
            await ReviewVerdictDecisions.CheckSeveritiesAsync(model, [Finding("a", ReviewSeverity.Major)]);

        Assert.Equal(direction, notes.Count == 0 ? null : notes[0].Direction);
    }

    [Fact]
    public async Task ACachedAnswerIsReusedWithoutAnotherCall()
    {
        string cache = Path.Combine(_temp.Path, "decision-model.json");
        string root = Repository();
        FakeProvider first = new() { Choice = "supports", Confidence = 0.95 };
        await ReviewVerdictDecisions.CheckClaimsAsync(
            new ReviewDecisionModel(first, ArbiterConfig.ProductDefaults.Rules, null, TimeProvider.System, cachePath: cache), root, [Finding("a", ReviewSeverity.Major)]);
        FakeProvider second = new() { Choice = "contradicts", Confidence = 0.99 };

        ClaimCheck check = Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(
            new ReviewDecisionModel(second, ArbiterConfig.ProductDefaults.Rules, null, TimeProvider.System, cachePath: cache), root, [Finding("a", ReviewSeverity.Major)]));

        Assert.False(check.NeedsRefuter);
        Assert.Equal(0, second.Calls);
    }

    // ---------------------------------------------------------------- helpers

    private string LogDirectory => Path.Combine(_temp.Path, "artifacts", "arbiter");

    private ReviewDecisionModel Model(IArbiterProvider provider) =>
        new(provider, ArbiterConfig.ProductDefaults.Rules, LogDirectory, TimeProvider.System);

    private string Repository()
    {
        string root = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.cs"), string.Join('\n', Enumerable.Range(1, 30).Select(line => line == 10 ? "    return total / count;" : $"// line {line}")));
        return root;
    }

    private static ReviewFinding Finding(string id, ReviewSeverity severity, int line = 10, string file = "a.cs") =>
        new(id, "correctness", severity, 8, file, line, "    return total / count;", "Divides by zero when count is 0.", "evidence", "An empty list crashes the report.");

    /// <summary>Answers like the real client: one answer per asked rule id, the floor applied, scores kept.</summary>
    private sealed class FakeProvider : IArbiterProvider
    {
        public string Choice { get; init; } = "supports";

        public double Confidence { get; init; } = 0.95;

        public double Noul { get; init; } = 0.5;

        public bool Fault { get; init; }

        private readonly Lock _gate = new();

        public int Calls { get; private set; }

        public string LastStateText { get; private set; } = string.Empty;

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Calls++;
                LastStateText = string.Join('\n', rules.SelectMany(rule => rule.Ask!.State.Values)
                    .Select(name => facts.TryGet(name, out ArbiterFact? fact) ? fact.Value?.ToString() : null));
            }

            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                answers[rule.Id] = RuleEngine.Undecidable;
                if (Fault)
                    continue;

                if (rule.Ask!.Type == "noul")
                {
                    scores[rule.Id] = new ArbiterStep1Score([Noul], null, rule.Ask.ProbabilityBelow);
                    answers[rule.Id] = Noul < (rule.Ask.ProbabilityBelow ?? 0.5) ? rule.Answers[1] : rule.Answers[0];
                    continue;
                }

                scores[rule.Id] = new ArbiterStep1Score(null, Confidence, rule.Ask.ConfidenceAtLeast);
                if (rule.Answers.Contains(Choice) && Confidence >= (rule.Ask.ConfidenceAtLeast ?? 0))
                    answers[rule.Id] = Choice;
            }

            return Task.FromResult(Fault
                ? new ArbiterStep1BatchResult(answers, null, null, true)
                : new ArbiterStep1BatchResult(answers, "jev-1.14.0", null, true, scores));
        }
    }
}

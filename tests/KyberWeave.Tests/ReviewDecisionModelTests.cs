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

    [Fact]
    public async Task DuplicateQuestionsAreCappedAndTheSkippedComparisonsAreReported()
    {
        FakeProvider provider = new() { Choice = "different-defect", Confidence = 0.99 };
        ReviewDecisionModel model = Model(provider);
        ReviewFinding[] many = [.. Enumerable.Range(1, 30).Select(index => Finding($"x/f{index}", ReviewSeverity.Major, line: index))];

        (List<ReviewFinding> kept, _) = await ReviewVerdictDecisions.MergeDuplicatesAsync(model, many);

        Assert.Equal(30, kept.Count);
        Assert.Equal(ReviewVerdictDecisions.MaxQuestionsPerStep, provider.Calls);
        Assert.Contains("90 comparison(s) skipped", model.SummaryLine([]), StringComparison.Ordinal);
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
    [InlineData("}", false)]
    [InlineData("});\n}", false)]
    [InlineData("x;", false)]
    [InlineData("    return total / count;", true)]
    [InlineData("if (count == 0)\n{\n    return total / count;", true)]
    [InlineData("return total / count;\nif (count == 0)", false)]
    public void AnExcerptNeedsEnoughSignalAndItsLinesInOrder(string excerpt, bool verified)
    {
        const string window = "if (count == 0)\n{\n    return total / count;\n}\n";

        Assert.Equal(verified, ReviewVerdictDecisions.ExcerptIsIn(excerpt, window));
    }

    [Fact]
    public async Task AnOverLongOrEscapingFileNameIsAQuoteProblemNotACrash()
    {
        string root = Repository();
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "outside.cs"), "    return total / count;");
        FakeProvider provider = new() { Choice = "supports", Confidence = 1.0 };

        foreach (string file in new[] { new string('d', 5000) + "/x.cs", "../outside.cs", "bad\0name.cs" })
        {
            ClaimCheck check = Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(
                Model(provider), root, [Finding("a", ReviewSeverity.Major, file: file)]));

            Assert.True(check.NeedsRefuter);
            Assert.Contains("the cited file could not be read", check.Note, StringComparison.Ordinal);
        }

        Assert.Equal(0, provider.Calls);
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

    // ---------------------------------------------------------------- bounds on seat-written numbers, text and paths

    [Fact]
    public async Task LinesAtTheTwoEndsOfTheIntRangeAreComparedWithoutOverflowing()
    {
        FakeProvider provider = new() { Choice = "same-defect", Confidence = 0.99 };
        ReviewFinding[] findings =
        [
            Finding("a", ReviewSeverity.Major, line: int.MinValue),
            Finding("b", ReviewSeverity.Major, line: 0),
            Finding("c", ReviewSeverity.Major, line: int.MaxValue),
        ];

        (List<ReviewFinding> kept, List<MergedFindings> merged) = await ReviewVerdictDecisions.MergeDuplicatesAsync(Model(provider), findings);

        Assert.Equal(3, kept.Count);
        Assert.Empty(merged);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void ThePairsAreTheNearestFirstThenTheMostSevereThenSeatOrder()
    {
        ReviewFinding[] findings =
        [
            Finding("a", ReviewSeverity.Minor, line: 10),
            Finding("b", ReviewSeverity.Minor, line: 12),
            Finding("c", ReviewSeverity.Critical, line: 12),
            Finding("far", ReviewSeverity.Minor, line: 500),
            Finding("other-file", ReviewSeverity.Minor, line: 10, file: "b.cs"),
        ];

        (List<(int A, int B)> pairs, int candidates, int leftOut) = ReviewVerdictDecisions.NearbyPairs(findings);

        Assert.Equal([(1, 2), (0, 2), (0, 1)], pairs);
        Assert.Equal(3, candidates);
        Assert.Equal(0, leftOut);
    }

    [Fact]
    public void TwentyThousandFindingsOnOneFileAreBoundedInWorkAndReported()
    {
        ReviewFinding[] findings = [.. Enumerable.Range(0, 20_000).Select(index => Finding($"f{index}", ReviewSeverity.Minor, line: index % 40))];

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        (List<(int A, int B)> pairs, _, int leftOut) = ReviewVerdictDecisions.NearbyPairs(findings);
        watch.Stop();

        Assert.Equal(ReviewVerdictDecisions.MaxQuestionsPerStep, pairs.Count);
        Assert.Equal(20_000 - ReviewVerdictDecisions.MaxFindingsPerFile, leftOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task AFindingsOverTheLimitAreReportedInTheSummaryAndNeverLost()
    {
        FakeProvider provider = new() { Choice = "different", Confidence = 0.99 };
        ReviewDecisionModel model = Model(provider);
        ReviewFinding[] findings = [.. Enumerable.Range(0, ReviewVerdictDecisions.MaxFindingsPerFile + 25).Select(index => Finding($"f{index}", ReviewSeverity.Minor, line: 1))];

        (List<ReviewFinding> kept, _) = await ReviewVerdictDecisions.MergeDuplicatesAsync(model, findings);

        Assert.Equal(findings.Length, kept.Count);
        Assert.Equal(ReviewVerdictDecisions.MaxQuestionsPerStep, provider.Calls);
        Assert.Contains("25 finding(s) left out of the comparison", model.SummaryLine([]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryStepAsksAtMostTheQuestionCapEvenWhenItDidNotCapItself()
    {
        FakeProvider provider = new() { Choice = "about-right", Confidence = 0.99 };
        ReviewDecisionModel model = Model(provider);
        ReviewFinding[] findings = [.. Enumerable.Range(0, 450).Select(index => Finding($"f{index}", ReviewSeverity.Major, line: index))];

        await ReviewVerdictDecisions.CheckSeveritiesAsync(model, findings);

        Assert.Equal(ReviewVerdictDecisions.MaxQuestionsPerStep, provider.Calls);
        Assert.Contains("150 comparison(s) skipped", model.SummaryLine([]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task AnImpossibleCitedLineIsAFileProblemNotACrash(int line)
    {
        string root = Repository();
        FakeProvider provider = new() { Choice = "supports", Confidence = 1.0 };

        ClaimCheck check = Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(
            Model(provider), root, [Finding("a", ReviewSeverity.Major, line: line)]));

        Assert.True(check.NeedsRefuter);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task AnExcerptOverTheLimitsIsNotVerifiedAndGoesToTheRefuter()
    {
        string root = Repository();
        FakeProvider provider = new() { Choice = "supports", Confidence = 1.0 };
        string[] excerpts =
        [
            string.Join('\n', Enumerable.Repeat("    return total / count;", 500_000)),
            new string('x', ReviewSeatInput.MaxExcerptCharacters + 1),
            string.Join('\n', Enumerable.Repeat("    return total / count;", ReviewSeatInput.MaxExcerptLines + 1)),
        ];

        foreach (string excerpt in excerpts)
        {
            ClaimCheck check = Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(
                Model(provider), root, [Finding("a", ReviewSeverity.Major) with { Excerpt = excerpt }]));

            Assert.True(check.NeedsRefuter);
            Assert.Contains("so the quote was not verified", check.Note, StringComparison.Ordinal);
        }

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task AnExcerptExactlyAtTheLimitsIsStillChecked()
    {
        string root = Repository();
        FakeProvider provider = new() { Choice = "supports", Confidence = 0.95 };
        string atLines = "    return total / count;" + string.Concat(Enumerable.Repeat("\n", ReviewSeatInput.MaxExcerptLines - 1));

        ClaimCheck check = Assert.Single(await ReviewVerdictDecisions.CheckClaimsAsync(
            Model(provider), root, [Finding("a", ReviewSeverity.Major) with { Excerpt = atLines }]));

        Assert.False(check.NeedsRefuter);
    }

    [Fact]
    public async Task OverLongClaimAndEvidenceAreCutBeforeTheyAreSentOrScanned()
    {
        string root = Repository();
        FakeProvider provider = new() { Choice = "supports", Confidence = 0.95 };
        string huge = new('a', 600_000);

        await ReviewVerdictDecisions.CheckClaimsAsync(
            Model(provider), root, [Finding("a", ReviewSeverity.Major) with { Claim = huge, Evidence = huge }]);

        Assert.True(provider.LastStateText.Length < 50_000, $"{provider.LastStateText.Length} characters reached the model");
    }

    [Fact]
    public void EveryWayAFileReadCanFailIsOneAnswer()
    {
        Exception[] failures =
        [
            new IOException(), new UnauthorizedAccessException(), new System.Security.SecurityException(),
            new ArgumentException(), new NotSupportedException(), new PathTooLongException(),
        ];

        Assert.All(failures, failure => Assert.True(ReviewSeatInput.IsFileFailure(failure), failure.GetType().Name));
        Assert.False(ReviewSeatInput.IsFileFailure(new InvalidOperationException()));
    }

    [Fact]
    public void ALineDistanceBetweenTheEndsOfTheIntRangeDoesNotOverflow()
    {
        Assert.Equal(4_294_967_295L, ReviewSeatInput.LineDistance(int.MinValue, int.MaxValue));
        Assert.Equal(2_147_483_648L, ReviewSeatInput.LineDistance(int.MinValue, 0));
        Assert.Equal(0L, ReviewSeatInput.LineDistance(7, 7));
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

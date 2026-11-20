using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The architect rates each task's difficulty, and the decision model checks that rating by the shipped rubric in the
/// same call that picks the agent. A confident <c>not-agreed</c> raises <c>KW-DOC-TASKS-020</c>, unless the plan's
/// Decisions table already answers the rating; the model never changes the difficulty or its source.
/// </summary>
public sealed class PlanTaskDifficultyAgreementTests : IDisposable
{
    private const string Plan = "docs/plans/2026-11-14-cards.md";

    private const string Rubric = """
        <!-- One copy of the rubric; the model reads everything after this comment. -->
        Standard (easy) is idiomatic code an ordinary test covers. Advanced (hard) fails in a way no test shows.
        """;

    private const string Tasks = """
        ## Tasks

        ### T1: Add the job status repository method

        - **Files:** `src/Data/JobRepository.cs`
        - **Depends on:** none
        - **Capabilities:** ADO.NET repository with a GROUP BY query and integration tests
        - **Difficulty:** easy

        Build it. Command: `dotnet test --filter JobRepositoryTests`
        Acceptance criteria: counts per status are returned.
        """;

    private const string DecisionsAnsweringTheRating = """

        ## Decisions

        | ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
        |---|---|---|---|---|
        | D1 | T1 difficulty | Kept as easy: the repository already has this query pattern. | | Approved |
        """;

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeEmbeddedResources _resources = new FakeEmbeddedResources()
        .Add(PlanTaskAgentPick.RubricResourceName, Rubric);
    private readonly List<PlanTaskAgentPick> _pickers = [];

    public PlanTaskDifficultyAgreementTests()
    {
        _files.CreateDirectory(Path.Combine(Repo, "docs", "plans"));
    }

    public void Dispose()
    {
        foreach (PlanTaskAgentPick picker in _pickers)
            picker.Dispose();
    }

    private string Repo => Path.Combine(_files.Root, "repo");

    private PlanTaskFileOutcome Write(AgreementProvider provider, string extra = "")
    {
        _files.WriteAllText(Path.Combine(Repo, Plan), $"# Cards\n\n{Tasks}{extra}");
        PlanTaskAgentPick picker = new(provider, PlanTaskAgentPick.ImplementationAgents(), _resources);
        _pickers.Add(picker);
        return new PlanTaskFileGenerator(
            Repo, KyberWeaveConfigDefaults.Product, new InMemoryRepositoryFileStore(_files), picker)
            .Run(Plan, PlanTaskFileMode.Write);
    }

    [Fact]
    public void Agreed_ChangesNothingAndRaisesNothing()
    {
        AgreementProvider provider = new("dev-medium", 0.95, DifficultyAgreement.Agreed, 0.9);

        PlanTaskFileOutcome outcome = Write(provider);
        PlanTaskFileEntry task = outcome.File!.Tasks![0];

        Assert.DoesNotContain(outcome.Report.Items, item => item.Code == PlanTaskFileRules.DifficultyDisagreed);
        Assert.Equal("easy", task.Difficulty);
        Assert.Equal("architect", task.DifficultySource);
        Assert.Equal("T1 difficulty easy: agreed (confidence 0.9)", Assert.Single(outcome.DifficultyChecks!));
    }

    [Fact]
    public void NotAgreedAtTheFloor_RaisesTheFindingAndKeepsTheArchitectsRating()
    {
        AgreementProvider provider = new("dev-medium", 0.95, DifficultyAgreement.NotAgreed, 0.9);

        PlanTaskFileOutcome outcome = Write(provider);
        PlanTaskFileEntry task = outcome.File!.Tasks![0];

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Code == PlanTaskFileRules.DifficultyDisagreed);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("T1", finding.Subject);
        Assert.Contains("T1", finding.Message, StringComparison.Ordinal);
        Assert.Contains("easy", finding.Message, StringComparison.Ordinal);
        Assert.Contains("standard", finding.Message, StringComparison.Ordinal);
        Assert.Contains("T1 difficulty", finding.Hint, StringComparison.Ordinal);
        Assert.Equal("easy", task.Difficulty);
        Assert.Equal("architect", task.DifficultySource);
        Assert.Equal("T1 difficulty easy: not-agreed (confidence 0.9)", Assert.Single(outcome.DifficultyChecks!));
    }

    [Fact]
    public void NotAgreedWithADecisionsRowAnsweringTheRating_RaisesNothing()
    {
        AgreementProvider provider = new("dev-medium", 0.95, DifficultyAgreement.NotAgreed, 0.9);

        PlanTaskFileOutcome outcome = Write(provider, DecisionsAnsweringTheRating);

        Assert.DoesNotContain(outcome.Report.Items, item => item.Code == PlanTaskFileRules.DifficultyDisagreed);
        Assert.Equal("easy", outcome.File!.Tasks![0].Difficulty);
    }

    [Fact]
    public void NotAgreedBelowTheFloor_RaisesNothing()
    {
        AgreementProvider provider = new("dev-medium", 0.95, DifficultyAgreement.NotAgreed, 0.5);

        PlanTaskFileOutcome outcome = Write(provider);

        Assert.DoesNotContain(outcome.Report.Items, item => item.Code == PlanTaskFileRules.DifficultyDisagreed);
        Assert.Equal("T1 difficulty easy: unchecked", Assert.Single(outcome.DifficultyChecks!));
    }

    [Fact]
    public void TheRequestCarriesTheRubricAndTheArchitectsRating()
    {
        AgreementProvider provider = new("dev-medium", 0.95, DifficultyAgreement.Agreed, 0.9);

        Write(provider);

        Assert.Contains("KW-DOC-TASKS-PICK-T1", provider.Asked);
        Assert.Contains("KW-DOC-TASKS-AGREE-T1", provider.Asked);
        Assert.Equal("easy (standard)", provider.Facts["pick.architect-rating"].Value);
        string task = Assert.IsType<string>(provider.Facts["pick.task"].Value);
        Assert.Contains("Build it.", task, StringComparison.Ordinal);
        string rubric = Assert.IsType<string>(provider.Facts["pick.rubric"].Value);
        Assert.Contains("Advanced (hard) fails in a way no test shows", rubric, StringComparison.Ordinal);
        Assert.DoesNotContain("One copy of the rubric", rubric, StringComparison.Ordinal);
    }

    private sealed class AgreementProvider(
        string pickAnswer,
        double pickConfidence,
        string agreementAnswer,
        double agreementConfidence,
        bool fail = false) : IArbiterProvider
    {
        public List<string> Asked { get; } = [];

        public IReadOnlyDictionary<string, ArbiterFact> Facts { get; private set; } =
            new Dictionary<string, ArbiterFact>(StringComparer.Ordinal);

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Asked.AddRange(rules.Select(rule => rule.Id));
            if (fail)
                throw new HttpRequestException("model unreachable");

            Facts = facts.Facts;
            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                bool agreement = rule.Id.StartsWith("KW-DOC-TASKS-AGREE-", StringComparison.Ordinal);
                answers[rule.Id] = agreement ? agreementAnswer : pickAnswer;
                scores[rule.Id] = new ArbiterStep1Score(
                    null, agreement ? agreementConfidence : pickConfidence, 0.8);
            }

            return Task.FromResult(new ArbiterStep1BatchResult(answers, "stub", null, true, scores));
        }
    }
}

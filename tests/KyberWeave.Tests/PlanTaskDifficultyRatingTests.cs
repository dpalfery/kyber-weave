using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The decision model rates each task's difficulty in the same call that picks its agent. A rating at
/// the confidence floor or above replaces the architect's <c>Difficulty:</c> line and records
/// <c>decision-model</c> as its source; a lower answer, a fault or no model keeps the architect's.
/// </summary>
public sealed class PlanTaskDifficultyRatingTests : IDisposable
{
    private const string Plan = "docs/plans/2026-11-14-cards.md";

    private const string Markdown = """
        # Cards

        ## Tasks

        ### T1: Add the job status repository method

        - **Files:** `src/Data/JobRepository.cs`
        - **Depends on:** none
        - **Capabilities:** ADO.NET repository with a GROUP BY query and integration tests
        - **Difficulty:** easy

        Build it. Command: `dotnet test --filter JobRepositoryTests`
        Acceptance criteria: counts per status are returned.
        """;

    private readonly TempDirectory _repo = new();
    private readonly List<PlanTaskAgentPick> _pickers = [];

    public PlanTaskDifficultyRatingTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, "docs", "plans"));
        File.WriteAllText(Path.Combine(_repo.Path, Plan), Markdown);
    }

    public void Dispose()
    {
        foreach (PlanTaskAgentPick picker in _pickers)
            picker.Dispose();
        _repo.Dispose();
    }

    private PlanTaskFile Write(IArbiterProvider provider)
    {
        PlanTaskAgentPick picker = new(provider, PlanTaskAgentPick.ImplementationAgents());
        _pickers.Add(picker);
        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(
            _repo.Path, KyberWeaveConfig.ProductDefaults, DocsPorts.Files, picker)
            .Run(Plan, PlanTaskFileMode.Write);
        Assert.False(outcome.Report.HasErrors, string.Join("; ", outcome.Report.Items.Select(item => item.Message)));
        return outcome.File!;
    }

    [Fact]
    public void AConfidentRatingReplacesTheArchitectsRating()
    {
        PlanTaskFileEntry task = Write(new RateProvider("dev-medium", 0.95, "hard", 0.9)).Tasks![0];

        Assert.Equal("hard", task.Difficulty);
        Assert.Equal("decision-model", task.DifficultySource);
    }

    [Fact]
    public void TheShippedStandardAnswerMapsToMedium()
    {
        PlanTaskFileEntry task = Write(new RateProvider("dev-medium", 0.95, "standard", 0.9)).Tasks![0];

        Assert.Equal("medium", task.Difficulty);
        Assert.Equal("decision-model", task.DifficultySource);
    }

    [Fact]
    public void ARatingBelowTheFloorKeepsTheArchitectsRating()
    {
        PlanTaskFileEntry task = Write(new RateProvider("dev-medium", 0.95, "hard", 0.6)).Tasks![0];

        Assert.Equal("easy", task.Difficulty);
        Assert.Equal("architect", task.DifficultySource);
    }

    [Fact]
    public void AModelFaultKeepsTheArchitectsRating()
    {
        PlanTaskFileEntry task = Write(new RateProvider("dev-medium", 0.95, "hard", 0.9, fail: true)).Tasks![0];

        Assert.Equal("easy", task.Difficulty);
        Assert.Equal("architect", task.DifficultySource);
    }

    [Fact]
    public void OneCallPerTaskCarriesBothQuestions()
    {
        RateProvider provider = new("dev-medium", 0.95, "hard", 0.9);

        Write(provider);

        Assert.Equal(1, provider.Calls);
        Assert.Contains("KW-DOC-TASKS-PICK-T1", provider.Asked);
        Assert.Contains("KW-DOC-TASKS-RATE-T1", provider.Asked);
    }

    private sealed class RateProvider(
        string pickAnswer,
        double pickConfidence,
        string? rateAnswer,
        double? rateConfidence,
        bool fail = false) : IArbiterProvider
    {
        public List<string> Asked { get; } = [];

        public int Calls { get; private set; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Asked.AddRange(rules.Select(rule => rule.Id));
            if (fail)
                throw new HttpRequestException("model unreachable");

            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                bool rating = rule.Id.StartsWith("KW-DOC-TASKS-RATE-", StringComparison.Ordinal);
                answers[rule.Id] = rating ? rateAnswer ?? "undecidable" : pickAnswer;
                scores[rule.Id] = new ArbiterStep1Score(
                    null, rating ? rateConfidence : pickConfidence, 0.8);
            }

            return Task.FromResult(new ArbiterStep1BatchResult(answers, "stub", null, true, scores));
        }
    }
}

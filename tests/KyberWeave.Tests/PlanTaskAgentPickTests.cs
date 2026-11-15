using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The decision model picks each unassigned task's agent once, as the task file is written (ADR 0082). The pick is
/// optional: no model, a fault or a pick below the floor leaves the task without one, and the file is still written.
/// </summary>
public sealed class PlanTaskAgentPickTests : IDisposable
{
    private const string Plan = "docs/plans/2026-11-14-cards.md";

    private const string Markdown = """
        # Cards

        ## Tasks

        ### T1: Add the job status repository method

        - **Files:** `src/Data/JobRepository.cs`
        - **Depends on:** none
        - **Capabilities:** ADO.NET repository with a GROUP BY query and integration tests

        Build it. Command: `dotnet test --filter JobRepositoryTests`
        Acceptance criteria: counts per status are returned.

        ### T2: Add the dashboard cards

        - **Files:** `web/src/JobStatusCards.tsx`
        - **Depends on:** none
        - **Capabilities:** React UI with Playwright tests
        - **Role:** `react-dev`

        Build it. Command: `npm test -- cards`
        Acceptance criteria: four cards show counts.
        """;

    private readonly TempDirectory _repo = new();
    private readonly List<PlanTaskAgentPick> _pickers = [];

    public PlanTaskAgentPickTests()
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

    private PlanTaskAgentPick Picker(IArbiterProvider provider)
    {
        PlanTaskAgentPick picker = new(provider, PlanTaskAgentPick.ImplementationAgents());
        _pickers.Add(picker);
        return picker;
    }

    [Fact]
    public void AConfidentPick_IsWrittenForAnUnassignedTaskOnly_AndTheReadyQueueNamesIt()
    {
        StubProvider provider = new("dal-dev", 0.97);

        PlanTaskFile file = Write(Picker(provider));

        PlanTaskFileEntry picked = file.Tasks![0];
        Assert.Equal("dal-dev", picked.Agent);
        Assert.Equal(0.97, picked.AgentConfidence);
        Assert.Null(file.Tasks[1].Agent);
        Assert.Equal(["KW-DOC-TASKS-PICK-T1"], provider.Asked);
        Assert.Contains("| agent=dal-dev |", PlanTaskPacket.ReadyQueue(file.Tasks), StringComparison.Ordinal);
        Assert.Contains("| role=react-dev |", PlanTaskPacket.ReadyQueue(file.Tasks), StringComparison.Ordinal);
    }

    [Fact]
    public void APickBelowTheFloor_LeavesTheConductorToChoose()
    {
        PlanTaskFile file = Write(Picker(new StubProvider("dal-dev", 0.6)));

        Assert.Null(file.Tasks![0].Agent);
        Assert.DoesNotContain("agent=", PlanTaskPacket.ReadyQueue(file.Tasks), StringComparison.Ordinal);
    }

    [Fact]
    public void AModelFault_WritesTheFileWithNoPick()
    {
        PlanTaskFile file = Write(Picker(new StubProvider(null, null, fail: true)));

        Assert.Null(file.Tasks![0].Agent);
    }

    [Fact]
    public void NoPicker_WritesTheFileAsBefore()
    {
        PlanTaskFile file = Write(null);

        Assert.All(file.Tasks!, task => Assert.Null(task.Agent));
    }

    [Fact]
    public void ACheckKeepsThePickOnDisk_WithoutAskingAgain_AndARewriteDoesNotMoveIt()
    {
        Write(Picker(new StubProvider("dal-dev", 0.97)));
        StubProvider second = new("csharp-dev", 0.99);

        PlanTaskFileOutcome check = new PlanTaskFileGenerator(_repo.Path, KyberWeaveConfig.ProductDefaults,
            Picker(second)).Run(Plan, PlanTaskFileMode.Check);
        PlanTaskFile rewritten = Write(Picker(second));

        Assert.False(check.Report.HasErrors, string.Join("; ", check.Report.Items.Select(item => item.Message)));
        Assert.Equal("dal-dev", rewritten.Tasks![0].Agent);
        Assert.Empty(second.Asked);
    }

    [Fact]
    public void ProviderNone_BuildsNoPicker_AndMakesNoCall()
    {
        using TempDirectory home = new();

        Assert.Null(ConfiguredProvider.TryCreate(KyberWeaveConfig.ProductDefaults, home.Path));
    }

    private PlanTaskFile Write(IPlanTaskAgentPicker? picker)
    {
        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(_repo.Path, KyberWeaveConfig.ProductDefaults, picker)
            .Run(Plan, PlanTaskFileMode.Write);
        Assert.False(outcome.Report.HasErrors, string.Join("; ", outcome.Report.Items.Select(item => item.Message)));
        return outcome.File!;
    }

    private sealed class StubProvider(string? answer, double? confidence, bool fail = false) : IArbiterProvider
    {
        public List<string> Asked { get; } = [];

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Asked.AddRange(rules.Select(rule => rule.Id));
            if (fail)
                throw new HttpRequestException("model unreachable");

            Dictionary<string, string> answers = rules.ToDictionary(rule => rule.Id, _ => answer ?? "undecidable");
            Dictionary<string, ArbiterStep1Score> scores = rules.ToDictionary(
                rule => rule.Id, _ => new ArbiterStep1Score(null, confidence, 0.8));
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "stub", null, true, scores));
        }
    }
}

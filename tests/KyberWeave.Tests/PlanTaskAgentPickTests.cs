using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.Core;

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
        - **Role:** `dev-medium`

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
        StubProvider provider = new("dev-medium", 0.97);

        PlanTaskFile file = Write(Picker(provider));

        PlanTaskFileEntry picked = file.Tasks![0];
        Assert.Equal("dev-medium", picked.Agent);
        Assert.Equal(0.97, picked.AgentConfidence);
        Assert.Null(file.Tasks[1].Agent);
        Assert.Contains("KW-DOC-TASKS-PICK-T1", provider.Asked);
        Assert.Contains("KW-DOC-TASKS-RATE-T1", provider.Asked);
        Assert.Equal(1, provider.Calls);
        Assert.Contains("| agent=dev-medium |", PlanTaskPacket.ReadyQueue(file.Tasks), StringComparison.Ordinal);
        Assert.Contains("| role=dev-medium |", PlanTaskPacket.ReadyQueue(file.Tasks), StringComparison.Ordinal);
    }

    [Fact]
    public void APickBelowTheFloor_LeavesTheConductorToChoose()
    {
        PlanTaskFile file = Write(Picker(new StubProvider("dev-medium", 0.6)));

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
        Write(Picker(new StubProvider("dev-medium", 0.97)));
        StubProvider second = new("dev-easy", 0.99);

        PlanTaskFileOutcome check = new PlanTaskFileGenerator(_repo.Path, KyberWeaveConfig.ProductDefaults, DocsPorts.Files,
            Picker(second)).Run(Plan, PlanTaskFileMode.Check);
        PlanTaskFile rewritten = Write(Picker(second));

        Assert.False(check.Report.HasErrors, string.Join("; ", check.Report.Items.Select(item => item.Message)));
        Assert.Equal("dev-medium", rewritten.Tasks![0].Agent);
        Assert.Empty(second.Asked);
    }

    [Fact]
    public void ProviderNone_BuildsNoPicker_AndMakesNoCall()
    {
        using TempDirectory home = new();
        FakeArbiterUserSettings settings = new();
        ConfiguredArbiterProviderFactory providers = new(settings);

        Assert.Null(providers.TryCreate(KyberWeaveConfig.ProductDefaults.Arbiter.Provider, home.Path));
        Assert.Equal([home.Path], settings.Asked);
    }

    [Fact]
    public void AMalformedUserOverride_FallsBackToTheRepositoryProvider()
    {
        // A broken override file must not break task-file generation: the repository's own provider stands, and
        // provider none then builds no picker rather than letting the parse failure escape.
        using TempDirectory home = new();
        ConfiguredArbiterProviderFactory providers = new(new FakeArbiterUserSettings
        {
            Override = (_, _) => throw new YamlException("The user override could not be read."),
        });

        Assert.Null(providers.TryCreate(KyberWeaveConfig.ProductDefaults.Arbiter.Provider, home.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyHomeDirectory_IsTreatedAsNoOverride(string home)
    {
        ConfiguredArbiterProviderFactory providers = new(new FakeArbiterUserSettings());

        Assert.Null(providers.TryCreate(KyberWeaveConfig.ProductDefaults.Arbiter.Provider, home));
    }

    private PlanTaskFile Write(IPlanTaskAgentPicker? picker)
    {
        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(_repo.Path, KyberWeaveConfig.ProductDefaults, DocsPorts.Files, picker)
            .Run(Plan, PlanTaskFileMode.Write);
        Assert.False(outcome.Report.HasErrors, string.Join("; ", outcome.Report.Items.Select(item => item.Message)));
        return outcome.File!;
    }

    private sealed class StubProvider(string? answer, double? confidence, bool fail = false) : IArbiterProvider
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

            Dictionary<string, string> answers = rules.ToDictionary(rule => rule.Id, _ => answer ?? "undecidable");
            Dictionary<string, ArbiterStep1Score> scores = rules.ToDictionary(
                rule => rule.Id, _ => new ArbiterStep1Score(null, confidence, 0.8));
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "stub", null, true, scores));
        }
    }
}

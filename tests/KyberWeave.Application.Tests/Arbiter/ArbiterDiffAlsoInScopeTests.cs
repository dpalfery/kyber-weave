using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-DIFF-001</c> judges a returning fix round against the task's own files plus the other tasks' files its
/// dispatch names as also in scope, so a coupled fix of two tasks does not escalate on the second task's file, while a
/// path outside both still reaches the model's outside-task question.
/// </summary>
public sealed class ArbiterDiffAlsoInScopeTests
{
    private const string Root = "/repo";
    private const string PlanPath = "docs/plans/cards.md";

    private const string Plan =
        """
        # Plan: order cards

        **Status: Ready, 2026-11-14.**

        ## Tasks

        ### T2 — Order DTO
        - **Files:** `src/Contract/OrderDto.cs`

        ### T3 — Orders controller
        - **Files:** `src/Http/OrdersController.cs`
        """;

    private const string Prompt =
        """
        KYBER-ARBITER: true
        PLAN_FILE: docs/plans/cards.md
        TASK: T2
        Also in scope (task T3): src/Http/OrdersController.cs
        Fix packet: /work/review/cycle-3/fix-packets/task-T2.md
        """;

    private readonly InMemoryFileSystem _files = new();

    public ArbiterDiffAlsoInScopeTests()
    {
        _files.CreateDirectory($"{Root}/docs/plans");
        _files.WriteAllText($"{Root}/{PlanPath}", Plan);
    }

    /// <summary>A path another task owns, named as also in scope, is part of the fix's scope.</summary>
    [Fact]
    public void AChangedPathNamedAsAlsoInScopeIsInsideTheTask()
    {
        ArbiterFactSet facts = Facts("src/Contract/OrderDto.cs", "src/Http/OrdersController.cs");

        Assert.Equal("inside-task", RuleEngine.Decide(Rule(), facts));
    }

    /// <summary>A path outside the task and outside every also-in-scope line still reaches the outside-task question.</summary>
    [Fact]
    public void AChangedPathOutsideBothTheTaskAndTheAlsoInScopeFilesIsNotInsideTheTask()
    {
        ArbiterFactSet facts = Facts(
            "src/Contract/OrderDto.cs",
            "src/Http/OrdersController.cs",
            "src/Other/Unrelated.cs");

        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Rule(), facts));
        Assert.Equal(RuleEffects.Escalate, Rule().Effects["outside-task"]);
    }

    /// <summary>A path an also-in-scope line names but no plan task lists in its files is not part of the fix's scope.</summary>
    [Fact]
    public void AnAlsoInScopeFileNoPlanTaskOwnsIsNotAddedToTheScope()
    {
        const string prompt =
            """
            KYBER-ARBITER: true
            PLAN_FILE: docs/plans/cards.md
            TASK: T2
            Also in scope (task T3): src/Other/Unowned.cs
            Fix packet: /work/review/cycle-3/fix-packets/task-T2.md
            """;

        ArbiterFactSet facts = FactsWithPrompt(prompt, "src/Contract/OrderDto.cs");

        Assert.Equal(
            ["src/Contract/OrderDto.cs"],
            Assert.IsAssignableFrom<IEnumerable<string>>(facts.Facts[PlanFacts.ScopeFilesFact].Value));
    }

    /// <summary>
    /// A path that no other task lists exactly, but that sits under a directory another task lists, is part of the fix's
    /// scope: the writer groups a coupled fix by the same rule, so the return must read the scope the same way.
    /// </summary>
    [Fact]
    public void AFileUnderADirectoryAnotherTaskListsIsAlsoInScope()
    {
        DefinePlan(
            """
            # Plan: order cards

            **Status: Ready, 2026-11-14.**

            ## Tasks

            ### T2 — Order DTO
            - **Files:** `src/Contract/OrderDto.cs`

            ### T3 — Orders controller
            - **Files:** `src/Http`
            """);

        ArbiterFactSet facts = Facts("src/Contract/OrderDto.cs", "src/Http/OrdersController.cs");

        Assert.Equal("inside-task", RuleEngine.Decide(Rule(), facts));
    }

    /// <summary>A <c>./</c>-prefixed entry names the same file git reports without the prefix.</summary>
    [Fact]
    public void ADotSlashPrefixedEntryNamesTheFileItPrefixes()
    {
        const string prompt =
            """
            KYBER-ARBITER: true
            PLAN_FILE: docs/plans/cards.md
            TASK: T2
            Also in scope (task T3): ./src/Http/OrdersController.cs
            Fix packet: /work/review/cycle-3/fix-packets/task-T2.md
            """;

        ArbiterFactSet facts = FactsWithPrompt(prompt, "src/Contract/OrderDto.cs", "src/Http/OrdersController.cs");

        Assert.Equal("inside-task", RuleEngine.Decide(Rule(), facts));
    }

    private static ArbiterRule Rule() =>
        Assert.Single(
            ArbiterConfigDefaults.Product.Rules,
            rule => string.Equals(rule.Id, "KW-ARB-DIFF-001", StringComparison.Ordinal));

    /// <summary>A file is accepted only under the task id of the plan task that lists it, so a line cannot name another id.</summary>
    [Fact]
    public void AnAlsoInScopeFileNamedUnderTheWrongTaskIdIsNotAddedToTheScope()
    {
        const string prompt =
            """
            KYBER-ARBITER: true
            PLAN_FILE: docs/plans/cards.md
            TASK: T2
            Also in scope (task T2): src/Http/OrdersController.cs
            Fix packet: /work/review/cycle-3/fix-packets/task-T2.md
            """;

        ArbiterFactSet facts = FactsWithPrompt(prompt, "src/Contract/OrderDto.cs");

        Assert.Equal(
            ["src/Contract/OrderDto.cs"],
            Assert.IsAssignableFrom<IEnumerable<string>>(facts.Facts[PlanFacts.ScopeFilesFact].Value));
    }

    private void DefinePlan(string plan) => _files.WriteAllText($"{Root}/{PlanPath}", plan);

    private ArbiterFactSet Facts(params string[] changed) => FactsWithPrompt(Prompt, changed);

    private ArbiterFactSet FactsWithPrompt(string prompt, params string[] changed)
    {
        TriggerClassification classification = new(
            ArbiterTriggers.DelegateReturned,
            "conductor",
            ArbiterCallerSources.Harness,
            "dev-medium",
            "implementation",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PLAN_FILE"] = PlanPath,
                ["TASK"] = "T2",
            },
            null,
            true);

        KyberWeaveConfig config = new()
        {
            Arbiter = ArbiterConfigDefaults.Product,
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, prompt, config);
        facts = new PlanFacts(new ContainedPlanFileResolver(), _files).Enrich(facts, classification, Root);
        return facts.With("git.changed-paths.attributed", changed.ToList(), ArbiterFactLabel.Derived);
    }
}

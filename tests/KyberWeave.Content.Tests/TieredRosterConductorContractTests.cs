using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The conductor, the architect and the bug flow under the tiered roster: a dispatch carries the task id and
/// never the prompt, the docs tool names the developer, a failed attempt moves one tier up inside the existing
/// three-dispatch limit, the architect plans the schema and the data model, and the bug flow hands its fix to a
/// developer agent.
/// </summary>
/// <remarks>
/// "The conductor's text" is its body, the path contracts it loads and the one execution contract it loads for
/// dispatch; that skill is the single copy of the dispatch rules, so a rule repeated in the body as well would be
/// the second copy this contract exists to prevent.
/// </remarks>
public sealed class TieredRosterConductorContractTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string ConductorPath = "products/kyber-squad/agents/conductor.md";

    private const string BugCrusherPath = "products/kyber-squad/agents/bug-crusher.md";

    private const string BugCrusherSkillPath = "products/kyber-squad/skills/bug-crusher/SKILL.md";

    private const string ArchitectPath = "products/kyber-squad/agents/architect.md";

    private const string DeliveryExecutionPath = "products/kyber-squad/skills/delivery-execution/SKILL.md";

    private const string PlanPath = "products/kyber-squad/agents/conductor/references/plan-path.md";

    private const string SpecPath = "products/kyber-squad/agents/conductor/references/spec-path.md";

    private const string IntakePath = "products/kyber-squad/agents/conductor/references/intake-path.md";

    private const string AutonomyPath = "products/kyber-squad/agents/conductor/references/autonomy.md";

    /// <summary>Every file the conductor reads as its dispatch contract.</summary>
    private static readonly string[] ConductorSurface =
    [
        ConductorPath,
        PlanPath,
        SpecPath,
        IntakePath,
        AutonomyPath,
        DeliveryExecutionPath,
    ];

    /// <summary>The three developer tiers, in step-up order.</summary>
    private static readonly string[] DeveloperTiers = ["dev-easy", "dev-medium", "dev-hard"];

    [Fact]
    public void ADispatchCarriesTheTaskIdAndNeverThePrompt()
    {
        string skill = Read(DeliveryExecutionPath);

        Assert.Contains("the task id, and nothing else", skill, StringComparison.Ordinal);
        Assert.Contains("never the prompt", skill, StringComparison.Ordinal);
        Assert.Equal(1, CountInSurface("never the prompt"));
    }

    [Fact]
    public void TheDocsToolNamesTheDeveloper()
    {
        string conductor = Read(ConductorPath);

        Assert.Contains("The worker roster", conductor, StringComparison.Ordinal);
        Assert.Contains("docs_build_task_prompt_for_agent_dispatch", conductor, StringComparison.Ordinal);
        Assert.Contains("`docs_build_task_prompt_for_agent_dispatch` answers with it", conductor, StringComparison.Ordinal);
        Assert.Contains("task file records", conductor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheStepUpMovesExactlyOneTierAndStopsAtTheThreeDispatchLimit()
    {
        string conductor = Read(ConductorPath);

        Assert.Contains("tier = min(hard, rated difficulty + failed attempts)", conductor, StringComparison.Ordinal);
        foreach (string tier in DeveloperTiers)
        {
            Assert.Contains(tier, conductor, StringComparison.Ordinal);
        }

        Assert.Contains("exactly one tier up", conductor, StringComparison.Ordinal);
        Assert.Contains("never down", conductor, StringComparison.Ordinal);
        Assert.Contains("three rework dispatches", conductor, StringComparison.Ordinal);
        Assert.Contains("architect", conductor, StringComparison.Ordinal);
    }

    /// <summary>
    /// The step-up and the tier formula have one home. A second copy is how the ladder and the limit drift apart.
    /// </summary>
    [Fact]
    public void EachTieredRuleIsStatedOnce()
    {
        string[] once =
        [
            "tier = min(hard, rated difficulty + failed attempts)",
            "exactly one tier up",
            "never the prompt",
        ];

        string[] repeated =
        [
            .. once.Where(rule => CountInSurface(rule) != 1).Select(rule => $"'{rule}' appears {CountInSurface(rule)} times")
        ];

        Assert.True(
            repeated.Length == 0,
            $"Each tiered rule is stated once across the conductor's text; {string.Join("; ", repeated)}.");
    }

    [Fact]
    public void TheConductorNamesTheDeliveryAndPlatformAgents()
    {
        string conductor = Read(ConductorPath);

        Assert.Contains(
            "The delivery agent is `devops` and the live-platform agent is `platform-researcher`.",
            conductor,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The roster rule allows a caller only the agents its <c>delegates-to</c> lists, and run start and delivery
    /// already tell the conductor to dispatch <c>devops</c>.
    /// </summary>
    [Fact]
    public void TheConductorAndTheBugFlowListTheTieredWorkersTheyDispatch()
    {
        string[] callers = [ConductorPath, BugCrusherPath];
        string[] expected = [.. DeveloperTiers, "devops"];

        List<string> missing = [];
        foreach (string caller in callers)
        {
            HashSet<string> delegatesTo = DelegatesTo(Read(caller));
            missing.AddRange(
                expected
                    .Where(agent => !delegatesTo.Contains(agent))
                    .Select(agent => $"{caller} does not list '{agent}'"));
        }

        Assert.True(
            missing.Count == 0,
            $"The tiered workers must be dispatchable (KW-ARB-ROSTER-001): {string.Join("; ", missing)}.");
    }

    /// <summary>
    /// There is one roster, so the conductor names it and the ladder it dispatches on, and offers no other: a
    /// technology developer is not a choice it can make anywhere in its body.
    /// </summary>
    [Fact]
    public void TheConductorOffersTheOneRosterAndNoTechnologyDeveloper()
    {
        string conductor = Read(ConductorPath);

        Assert.Contains("There is one roster: the three developer tiers.", conductor, StringComparison.Ordinal);
        Assert.Contains("`dev-easy`, `dev-medium`, `dev-hard`", conductor, StringComparison.Ordinal);
        Assert.DoesNotContain("`csharp-dev`", conductor, StringComparison.Ordinal);
        Assert.DoesNotContain("`test-dev`", conductor, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConductorDispatchesTheTieredDeliveryAgentForRunStart()
    {
        string line = LineContaining(Read(ConductorPath), "RUN-START: true");

        Assert.Contains("`devops`", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second end-to-end tiered run stopped before its first dispatch because the generated Config Reg block in
    /// <c>AGENTS.md</c> still said <c>worker-roster: technology</c> while the configuration said <c>tiered</c>. The
    /// block is generated from the configuration, so the configuration is the authority; a stale block is reported
    /// and fixed with <c>docs init</c>, and it never stops a run. Every path the input router can take carries the
    /// rule: the conductor body is at its Copilot size cap, so it lives in each path contract a run reads whole.
    /// </summary>
    [Theory]
    [InlineData(PlanPath)]
    [InlineData(SpecPath)]
    [InlineData(IntakePath)]
    public void TheConductorTreatsTheConfigurationAsTheRegistryAuthorityAndNeverHaltsOnAStaleBlock(string path)
    {
        string contract = Read(path);

        Assert.Contains(
            "The roster and every Config Reg property come from `.kyber-weave/kyber-weave.yml`",
            contract,
            StringComparison.Ordinal);
        Assert.Contains("the block is stale", contract, StringComparison.Ordinal);
        Assert.Contains("docs init", contract, StringComparison.Ordinal);
        Assert.Contains("never stops a run", contract, StringComparison.Ordinal);
    }

    /// <summary>
    /// The planner's gate is the CLI's: it fails on an error, not on a warning. A host that has not
    /// run the standards migration reports warnings only, and a planner that read those as a failed
    /// check stopped a run that the configuration itself made valid.
    /// </summary>
    [Fact]
    public void TheArchitectTreatsAWarningOnlyPlanningCheckAsAPass()
    {
        string architect = Read(ArchitectPath);
        string planAuthoring = Read("products/kyber-squad/agents/architect/references/plan-authoring.md");

        Assert.Contains(
            "A check passes when it reports no error, so warnings never block the plan",
            architect,
            StringComparison.Ordinal);
        Assert.Contains("warnings do not fail it", planAuthoring, StringComparison.Ordinal);
        Assert.Contains(
            "`kyber-weave docs standards migrate`",
            architect,
            StringComparison.Ordinal);
    }

    /// <summary>The callers that send work to the live-platform agent list its tiered replacement too.</summary>
    [Fact]
    public void TheCallersThatReplacedAzureReaderListTheTieredResearcher()
    {
        string[] callers = [ConductorPath, ArchitectPath];

        string[] missing =
        [
            .. callers.Where(caller => !DelegatesTo(Read(caller)).Contains("platform-researcher"))
        ];

        Assert.True(
            missing.Length == 0,
            $"These callers must list 'platform-researcher' so a tiered run can dispatch it (KW-ARB-ROSTER-001): " +
            string.Join(", ", missing));
    }

    /// <summary>
    /// The unqualified technology rules must not come back. Each former sentence now opens with its roster, and
    /// the tiered branch states the override, so a future edit that drops the scope fails here rather than
    /// silently letting a tiered run match a technology agent to a file type.
    /// </summary>
    [Fact]
    public void TheTieredRoutingOverridesTheTechnologySelectionRules()
    {
        string conductor = Read(ConductorPath);
        string skill = Read(DeliveryExecutionPath);
        string bugCrusher = Read(BugCrusherSkillPath);

        Assert.DoesNotContain(
            "When none is named, assign it to the delegate whose description trigger matches its files",
            conductor,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Choose each task's agent by matching the abilities it needs",
            skill,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "If the fix is more than a few lines or sits in a specialist's domain, hand it to that specialist",
            bugCrusher,
            StringComparison.Ordinal);
        Assert.Contains(
            "That name is the developer tier the task's rating gives — `dev-easy`, `dev-medium` or `dev-hard` — or `docs-dev`/`devops` for other work",
            conductor,
            StringComparison.Ordinal);
        Assert.Contains("The dispatch tool names the agent", skill, StringComparison.Ordinal);
        Assert.Contains(
            "If the fix is more than a few lines or sits in a specialist's domain, hand it to a developer tier instead",
            bugCrusher,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// No dispatch site names the retired delivery agent. The rule is per site, because a global "read this
    /// name as that name" does not travel with a skill another entry point loads.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConductorSurfaceFiles))]
    public void NoDeliveryDispatchSiteNamesTheRetiredAgent(string relativePath)
    {
        string[] stale =
        [
            .. Read(relativePath).Split('\n')
                .Where(line => line.Contains("`github-devops`", StringComparison.Ordinal))
        ];

        Assert.True(
            stale.Length == 0,
            $"{relativePath} still dispatches the removal's deleted delivery agent. Stale: " + string.Join(" | ", stale));
    }

    public static IEnumerable<object[]> ConductorSurfaceFiles()
    {
        foreach (string path in ConductorSurface)
        {
            yield return [path];
        }
    }

    [Fact]
    public void TheSelectionRulesNameTheOneRosterAndNoTechnologyMatching()
    {
        string conductor = Read(ConductorPath);
        string skill = Read(DeliveryExecutionPath);

        Assert.DoesNotContain("On the `technology` roster", conductor, StringComparison.Ordinal);
        Assert.DoesNotContain("On the `technology` roster", skill, StringComparison.Ordinal);
        Assert.DoesNotContain("Under `tiered`", skill, StringComparison.Ordinal);
        Assert.Contains("The dispatch tool names the agent", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBugFlowHandsTheFixToADeveloperTierWithNoRosterBranch()
    {
        string bugCrusher = Read(BugCrusherSkillPath);

        Assert.Contains(
            "If the fix is more than a few lines or sits in a specialist's domain, hand it to a developer tier instead",
            bugCrusher,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Under `worker-roster: technology`", bugCrusher, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tiered fix dispatch passes the failed-attempt count, so the count must be the one the attempt budget
    /// already keeps, and the dispatch must carry it for the hook that builds the prompt.
    /// </summary>
    [Fact]
    public void TheBugFlowDefinesTheFailedAttemptCountItPasses()
    {
        string bugCrusher = Read(BugCrusherSkillPath);
        string line = LineContaining(bugCrusher, "docs_build_task_prompt_for_agent_dispatch");

        Assert.Contains("attempts already counted for this defect under", line, StringComparison.Ordinal);
        Assert.Contains("0 on the first fix attempt and 1 on the second", line, StringComparison.Ordinal);
        Assert.Contains("`FAILED_ATTEMPTS:`", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArchitectSendsLivePlatformStateToTheTieredResearcher()
    {
        string[] unscoped =
        [
            .. Read(ArchitectPath).Split('\n')
                .Where(line => line.Contains("`azure-reader`", StringComparison.Ordinal)
                               && !line.Contains("`platform-researcher`", StringComparison.Ordinal))
        ];

        Assert.True(
            unscoped.Length == 0,
            "The architect must name `platform-researcher` wherever it names `azure-reader`, or a tiered run keeps " +
            "dispatching the technology agent. Unscoped: " + string.Join(" | ", unscoped));
    }

    [Fact]
    public void TheArchitectPlansTheSchemaAndTheDataModel()
    {
        string architect = Read(ArchitectPath);

        Assert.Contains("Schema and data-model planning", architect, StringComparison.Ordinal);
        Assert.Contains("migration", architect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQL", architect, StringComparison.Ordinal);
        Assert.Contains("developer agent", architect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("index", architect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("constraint", architect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBugFlowHandsTheFixToADeveloperTier()
    {
        string bugCrusher = Read(BugCrusherSkillPath);

        Assert.Contains("docs_build_task_prompt_for_agent_dispatch", bugCrusher, StringComparison.Ordinal);
        Assert.Contains("dev-medium", bugCrusher, StringComparison.Ordinal);
        Assert.Contains("dev-hard", bugCrusher, StringComparison.Ordinal);
        Assert.Contains("TECHNOLOGY:", bugCrusher, StringComparison.Ordinal);
        Assert.Contains("There is one roster", bugCrusher, StringComparison.Ordinal);
        Assert.DoesNotContain("test-dev", bugCrusher, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) => Corpus.ReadText(relativePath);

    private static int CountInSurface(string phrase) =>
        ConductorSurface.Sum(path => Read(path).Split(phrase, StringSplitOptions.None).Length - 1);

    /// <summary>The frontmatter line that starts with <paramref name="name"/>, or an empty string.</summary>
    private static string FrontmatterLine(string text, string name) =>
        text.Split('\n').FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal)) ?? string.Empty;

    /// <summary>
    /// The agents a caller's <c>delegates-to</c> frontmatter lists, as a set. Parsed rather than substring-matched:
    /// a substring test would accept <c>dev-easy-extra</c> for <c>dev-easy</c> and would survive a missing comma.
    /// </summary>
    private static HashSet<string> DelegatesTo(string text)
    {
        string line = FrontmatterLine(text, "delegates-to:");
        int open = line.IndexOf('[', StringComparison.Ordinal);
        int close = line.LastIndexOf(']');
        if (open < 0 || close <= open)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return line[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The first line holding <paramref name="phrase"/>, or an empty string.</summary>
    private static string LineContaining(string text, string phrase) =>
        text.Split('\n').FirstOrDefault(line => line.Contains(phrase, StringComparison.Ordinal)) ?? string.Empty;
}

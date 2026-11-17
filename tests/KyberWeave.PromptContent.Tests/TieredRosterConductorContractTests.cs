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
    private const string ConductorPath = "products/kyber-squad/agents/conductor.md";

    private const string BugCrusherPath = "products/kyber-squad/agents/bug-crusher.md";

    private const string BugCrusherSkillPath = "products/kyber-squad/skills/bug-crusher/SKILL.md";

    private const string ArchitectPath = "products/kyber-squad/agents/architect.md";

    private const string DeliveryExecutionPath = "products/kyber-squad/skills/delivery-execution/SKILL.md";

    /// <summary>Every file the conductor reads as its dispatch contract.</summary>
    private static readonly string[] ConductorSurface =
    [
        ConductorPath,
        "products/kyber-squad/agents/conductor/references/plan-path.md",
        "products/kyber-squad/agents/conductor/references/spec-path.md",
        "products/kyber-squad/agents/conductor/references/intake-path.md",
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
    public void UnderTheTieredRosterTheDocsToolNamesTheDeveloper()
    {
        string conductor = Read(ConductorPath);

        Assert.Contains("worker-roster", conductor, StringComparison.Ordinal);
        Assert.Contains("tiered", conductor, StringComparison.Ordinal);
        Assert.Contains("docs_build_task_prompt_for_agent_dispatch", conductor, StringComparison.Ordinal);
        Assert.Contains("dispatch the agent it names", conductor, StringComparison.Ordinal);
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
    public void TheReplacedAgentsAreReadAsTheirTieredReplacements()
    {
        string conductor = Read(ConductorPath);

        Assert.Contains("`github-devops`", conductor, StringComparison.Ordinal);
        Assert.Contains("read `devops`", conductor, StringComparison.Ordinal);
        Assert.Contains("`azure-reader`", conductor, StringComparison.Ordinal);
        Assert.Contains("read `platform-researcher`", conductor, StringComparison.Ordinal);
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
            string delegatesTo = FrontmatterLine(Read(caller), "delegates-to:");
            missing.AddRange(
                expected
                    .Where(agent => !delegatesTo.Contains(agent, StringComparison.Ordinal))
                    .Select(agent => $"{caller} does not list '{agent}'"));
        }

        Assert.True(
            missing.Count == 0,
            $"The tiered workers must be dispatchable (KW-ARB-ROSTER-001): {string.Join("; ", missing)}.");
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
    public void TheBugFlowHandsTheFixToADeveloperAgentUnderTheTieredRoster()
    {
        string bugCrusher = Read(BugCrusherSkillPath);

        Assert.Contains("worker-roster", bugCrusher, StringComparison.Ordinal);
        Assert.Contains("docs_build_task_prompt_for_agent_dispatch", bugCrusher, StringComparison.Ordinal);
        Assert.Contains("dev-medium", bugCrusher, StringComparison.Ordinal);
        Assert.Contains("dev-hard", bugCrusher, StringComparison.Ordinal);
        Assert.Contains("TECHNOLOGY:", bugCrusher, StringComparison.Ordinal);
        Assert.Contains("no `test-dev`", bugCrusher, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, relativePath));

    private static int CountInSurface(string phrase) =>
        ConductorSurface.Sum(path => Read(path).Split(phrase, StringSplitOptions.None).Length - 1);

    /// <summary>The frontmatter line that starts with <paramref name="name"/>, or an empty string.</summary>
    private static string FrontmatterLine(string text, string name) =>
        text.Split('\n').FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal)) ?? string.Empty;
}

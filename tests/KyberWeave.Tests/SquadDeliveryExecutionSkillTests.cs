using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     The worker execution contract is one canonical skill that the conductor and the bug-crusher
///     workflow both load, and bug-crusher's roster covers every specialist that contract tells it
///     to start.
/// </summary>
/// <remarks>
///     Bug-crusher kept a second, partial copy of the conductor's execution contract inside its own
///     workflow, so the two copies drifted the first time one of them moved. The one copy is a
///     canonical skill rather than a conductor reference because a canonical skill is emitted for
///     every target where a conductor-only reference is not, and because a portable file may not
///     link out of its own directory. The roster is pinned at the same time: a delegate list
///     narrower than the escalation the skill performs leaves the escalated path unable to start the
///     specialists it names.
/// </remarks>
public sealed class SquadDeliveryExecutionSkillTests
{
    private const string ProductRootRelativePath = "products/kyber-squad";

    private const string DeliveryExecutionSkillName = "delivery-execution";

    private const string DeliveryExecutionSkillPath = "skills/delivery-execution/SKILL.md";

    private const string RetiredExecutionReferencePath =
        "agents/conductor/references/execution-and-review.md";

    /// <summary>
    ///     The removed reference's name, without its directory: a prose pointer to it strands the
    ///     reader just as a broken link does, so the scan looks for the name rather than the path.
    /// </summary>
    private const string RetiredExecutionReferenceName = "execution-and-review";

    private const string ConductorAgentPath = "agents/conductor.md";

    private const string BugCrusherAgentPath = "agents/bug-crusher.md";

    private const string BugCrusherSkillPath = "skills/bug-crusher/SKILL.md";

    /// <summary>
    ///     The specialists bug-crusher's escalated path starts: the architect it hands planning to,
    ///     the reviewer that judges the finished fix, the test author that establishes RED, and every
    ///     developer agent a task can route to.
    /// </summary>
    private static readonly string[] SpecialistDelegates =
    [
        "architect",
        "code-reviewer",
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "go-dev",
        "maui-dev",
        "pulumi-dev",
        "python-dev",
        "react-dev",
        "rust-dev",
        "sql-database-architect",
        "tauri-dev",
        "test-dev",
        "typescript-dev"
    ];

    /// <summary>The four delegates the roster already carried, which the widening keeps.</summary>
    private static readonly string[] RetainedDelegates =
        ["bug-crusher-investigator", "docs-dev", "review-lens", "review-triage"];

    private static readonly string[] ExpectedDelegates =
        [.. RetainedDelegates.Concat(SpecialistDelegates).Order(StringComparer.Ordinal)];

    /// <summary>
    ///     Discovery is the architect's own delegation and the review seats its own lenses, so these
    ///     three stay off the roster. Naming them would widen what an unreviewed escalation can start,
    ///     which is the risk the narrow list was defending against in the first place.
    /// </summary>
    private static readonly string[] ExcludedDelegates = ["azure-reader", "product-owner", "research-agent"];

    /// <summary>
    ///     The sections the conductor's removed reference carried. The contract moves rather than being
    ///     rewritten, so the skill has to hold the same ones: a skill that names itself the execution
    ///     contract while carrying none of it leaves both entry points with nothing to load.
    /// </summary>
    private static readonly string[] MovedContractSections =
    [
        "## Ready queue",
        "## Developer exit gate",
        "## Iteration circuit-breaker and loop detection",
        "## Findings and final council"
    ];

    /// <summary>A verb acting on a plan's status inside one clause, as the second copy once read.</summary>
    private static readonly Regex PlanStatusMutation = new(
        @"(?i)\b(?:set|sets|setting|change|changes|changing|update|updates|updating|move|moves|moving|flip|flips|flipping|edit|edits|editing|mark|marks|marking)\b[^\r\n.;]{0,40}\bplan\b[^\r\n.;]{0,20}\bstatus\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A search for a plan's status, which is the other half of owning the lifecycle.</summary>
    private static readonly Regex PlanStatusSearch = new(
        @"(?i)\b(?:grep|greps|grepping|ripgrep|rg)\b[^\r\n.;]{0,40}\bplan\b[^\r\n.;]{0,20}\bstatus\b|\bsearch(?:es|ing)?\b[^\r\n.;]{0,30}\bplan'?s?\s+status\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, ProductRootRelativePath);

    [Fact]
    public void TheCanonicalRosterLoadsTheSharedExecutionSkill()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadSkill[] matches = [.. source.Skills.Where(skill => skill.Name == DeliveryExecutionSkillName)];

        Assert.True(
            matches.Length == 1,
            $"The canonical skill roster must hold '{DeliveryExecutionSkillName}' exactly once so every target " +
            $"receives it; it holds it {matches.Length} times. Roster: {Joined([.. source.Skills.Select(skill => skill.Name)])}.");
        Assert.Equal(DeliveryExecutionSkillPath, matches[0].SourcePath);
        Assert.True(
            source.Bundle.SkillNames.Contains(DeliveryExecutionSkillName, StringComparer.Ordinal),
            $"The full bundle must select '{DeliveryExecutionSkillName}'; a skill the bundle omits is never deployed, " +
            $"so no entry point could load it. Bundle: {Joined(source.Bundle.SkillNames)}.");
    }

    [Fact]
    public void TheSharedExecutionSkillHoldsTheContractTheConductorUsedToKeep()
    {
        string skill = ReadProductFile(DeliveryExecutionSkillPath);

        string[] missing =
        [
            .. MovedContractSections.Where(section => !skill.Contains(section, StringComparison.Ordinal))
        ];

        Assert.True(
            missing.Length == 0,
            $"'{DeliveryExecutionSkillPath}' must carry the moved execution contract; it is missing " +
            $"{Joined(missing)}. The contract moves out of '{RetiredExecutionReferencePath}' rather than being " +
            "rewritten, so both entry points load the same rules.");
    }

    [Fact]
    public void TheRetiredExecutionReferenceIsRemoved()
    {
        string retired = Path.Combine(ProductRoot, ToRelativePath(RetiredExecutionReferencePath));

        Assert.False(
            File.Exists(retired),
            $"'{RetiredExecutionReferencePath}' must be removed: the contract lives at " +
            $"'{DeliveryExecutionSkillPath}' now, and a second copy is what drifted.");
    }

    [Fact]
    public void NoProductFileStillPointsAtTheRetiredExecutionReference()
    {
        string[] extensions = [".md", ".json", ".yaml", ".yml"];
        string[] offenders =
        [
            .. Directory
                .EnumerateFiles(ProductRoot, "*", SearchOption.AllDirectories)
                .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path => File.ReadAllText(path).Contains(RetiredExecutionReferenceName, StringComparison.Ordinal))
                .Select(RelativeProductPath)
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            offenders.Length == 0,
            $"Nothing under '{ProductRootRelativePath}' may still name '{RetiredExecutionReferenceName}', which no " +
            $"longer exists; the contract lives at '{DeliveryExecutionSkillPath}'. A reader who follows one of these " +
            $"pointers finds no file. Still naming it: {Joined(offenders)}.");
    }

    [Fact]
    public void TheConductorBodyNamesTheSharedExecutionSkill()
    {
        string conductor = ReadProductFile(ConductorAgentPath);

        Assert.True(
            conductor.Contains(DeliveryExecutionSkillName, StringComparison.Ordinal),
            $"'{ConductorAgentPath}' must name the '{DeliveryExecutionSkillName}' skill it loads for task sequencing, " +
            "evidence rules, findings, final review and closeout. A name, not a link: a portable file may not link " +
            "out of its own directory.");
    }

    [Fact]
    public void TheBugCrusherWorkflowNamesTheSharedExecutionSkill()
    {
        string skill = ReadProductFile(BugCrusherSkillPath);

        Assert.True(
            skill.Contains(DeliveryExecutionSkillName, StringComparison.Ordinal),
            $"'{BugCrusherSkillPath}' must load the '{DeliveryExecutionSkillName}' skill instead of restating the " +
            "conductor's worker rules in its own words.");
    }

    [Fact]
    public void BugCrusherDelegatesToEverySpecialistItsEscalationStarts()
    {
        SquadAgent bugCrusher = LoadAgent("bug-crusher");
        string[] actual = [.. bugCrusher.DelegatesTo.Order(StringComparer.Ordinal)];
        string[] missing = [.. ExpectedDelegates.Except(actual, StringComparer.Ordinal)];
        string[] unexpected = [.. actual.Except(ExpectedDelegates, StringComparer.Ordinal)];

        Assert.True(
            missing.Length == 0 && unexpected.Length == 0,
            $"'{BugCrusherAgentPath}' must delegate to {Joined(ExpectedDelegates)}. " +
            $"Missing: {Joined(missing)}. Unexpected: {Joined(unexpected)}.");
    }

    [Fact]
    public void BugCrusherLeavesDiscoveryAndTheOwnerOffItsRoster()
    {
        SquadAgent bugCrusher = LoadAgent("bug-crusher");
        string[] present =
        [
            .. ExcludedDelegates.Where(name => bugCrusher.DelegatesTo.Contains(name, StringComparer.Ordinal))
        ];

        Assert.True(
            present.Length == 0,
            $"'{BugCrusherAgentPath}' must not delegate to {Joined(present)}: discovery is the architect's own " +
            "delegation and the reviewer seats its own lenses.");
    }

    [Fact]
    public void TheBugCrusherMainSessionMovesNoPlanStatusLine()
    {
        string skill = ReadProductFile(BugCrusherSkillPath);

        Assert.False(
            PlanStatusMutation.IsMatch(skill),
            $"'{BugCrusherSkillPath}' must not tell the main session to move a plan's status itself, because the plan " +
            $"lifecycle belongs to the shared contract. Offending text: {FirstMatch(PlanStatusMutation, skill)}.");
        Assert.False(
            PlanStatusSearch.IsMatch(skill),
            $"'{BugCrusherSkillPath}' must not send the main session looking for a plan's status either. " +
            $"Offending text: {FirstMatch(PlanStatusSearch, skill)}.");
    }

    [Fact]
    public void BugCrusherSurfacesNoLongerClaimTheNarrowRoster()
    {
        string agent = ReadProductFile(BugCrusherAgentPath);
        string skill = ReadProductFile(BugCrusherSkillPath);

        Assert.False(
            agent.Contains("those four agents", StringComparison.OrdinalIgnoreCase),
            $"'{BugCrusherAgentPath}' still says the delegate grant is scoped to four agents, which the widened " +
            "roster contradicts.");
        Assert.False(
            skill.Contains("escalated rather than run on its own", StringComparison.OrdinalIgnoreCase),
            $"'{BugCrusherSkillPath}' still says the run-start dispatch is escalated rather than run; the widened " +
            "roster carries github-devops, so that dispatch runs.");
    }

    private static SquadAgent LoadAgent(string name)
    {
        SquadAgent? agent =
            TestSquadCorpus.CanonicalSource.Agents.FirstOrDefault(candidate => candidate.Name == name);
        Assert.NotNull(agent);
        return agent;
    }

    private static string ReadProductFile(string relativePath)
    {
        string path = Path.Combine(ProductRoot, ToRelativePath(relativePath));
        Assert.True(File.Exists(path), $"Expected the canonical product file at '{path}'.");
        return File.ReadAllText(path);
    }

    private static string ToRelativePath(string relativePath)
    {
        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string RelativeProductPath(string path)
    {
        return Path.GetRelativePath(ProductRoot, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string Joined(IEnumerable<string> names)
    {
        return names.Any() ? string.Join(", ", names) : "none";
    }

    private static string FirstMatch(Regex pattern, string content)
    {
        Match match = pattern.Match(content);
        return match.Success ? match.Value.Trim() : "none";
    }
}

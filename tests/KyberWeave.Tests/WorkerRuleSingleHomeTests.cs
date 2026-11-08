using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the rule map's single-home promise: a rule that moved to its one home must not also remain in
/// a second file, because regrowth is what the text trim has to survive (task T50, decision D62).
/// </summary>
/// <remarks>
/// <para>
/// The rule map itself (<c>docs/kyber-squad/worker-rule-map.md</c>) is task T49's deliverable and
/// carries one row per sentence removed. Until it exists, the rows this class can state without it
/// are the two the plan's Design table fixes by name: the <c>cleanupcode</c> pass is off by default
/// (D43) and stays in the skill that runs it, and the failure-loop rules live in the reference a
/// worker loads after a failed fix, not in each developer agent's body. Every other row waits for
/// the map, which is why <see cref="TheRuleMapRecordsAHomeForEveryRuleThatMoved"/> fails until T49
/// lands as well.
/// </para>
/// <para>
/// These cases are red by design until T52 applies the removals (D62). A red here is the ordering
/// working, not a defect: T50 moves the pins before the text moves, and no pinned string is weakened
/// to close the gap.
/// </para>
/// </remarks>
public sealed class WorkerRuleSingleHomeTests
{
    /// <summary>The developer agents whose failure-loop text the rule map moves to the rework reference.</summary>
    private static readonly string[] FailureLoopAgents = ["csharp-dev", "test-dev", "github-devops"];

    private static string ToolRoot => KyberWeaveTestPaths.ToolRoot;

    private static string ProductRoot => Path.Combine(ToolRoot, "products", "kyber-squad");

    private static string AgentsRoot => Path.Combine(ProductRoot, "agents");

    private static string RuleMapPath => Path.Combine(ToolRoot, "docs", "kyber-squad", "worker-rule-map.md");

    /// <summary>
    ///     The task-end pass decision D43 turns off. It rewrote existing code in the first RED task of
    ///     this programme and the worker reverted it, so no agent body may instruct it any more; the
    ///     skill that runs it on request keeps the command.
    /// </summary>
    [Fact]
    public void TheCleanupCodePassIsNotRequiredInAnyAgentBody()
    {
        string[] repeating = AgentsContaining("dotnet jb cleanupcode");

        Assert.True(
            repeating.Length == 0,
            "The cleanupcode pass is off by default (D43) and lives in the skill that runs it, not in an " +
            "agent body. Still instructing it: " + string.Join(", ", repeating));
    }

    /// <summary>The skill that owns the pass on request still spells it, so the rule kept a home.</summary>
    [Fact]
    public void TheCleanupCodePassKeepsItsHomeInTheSkillThatRunsIt()
    {
        string skill = ReadProductFile("skills/resharper-clt/SKILL.md");

        Assert.Contains("cleanupcode", skill, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The failure-loop rules moved to the one reference the task gate loads after a failed fix.
    ///     The JEV checkpoints, the retry limit, oscillation detection and the closed
    ///     <c>CIRCUIT_BREAKER_TRIGGER</c> vocabulary are asserted there by
    ///     <c>SquadCanonicalContentTests</c>; this case is the other half — no developer agent body
    ///     carries them as well.
    /// </summary>
    [Fact]
    public void TheFailureLoopRulesAreInNoDeveloperAgentBody()
    {
        string rework = ReadProductFile("skills/task-gate/references/rework.md");
        Assert.Contains("CIRCUIT_BREAKER_TRIGGER", rework, StringComparison.Ordinal);

        string[] repeating = FailureLoopAgents
            .Select(agent => (Agent: agent, Body: ReadProductFile($"agents/{agent}.md")))
            .Where(entry => entry.Body.Contains("CIRCUIT_BREAKER_TRIGGER", StringComparison.Ordinal))
            .Select(entry => entry.Agent)
            .ToArray();

        Assert.True(
            repeating.Length == 0,
            "The failure-loop rules live in 'skills/task-gate/references/rework.md'. Still carrying them: " +
            string.Join(", ", repeating));
    }

    /// <summary>
    ///     T49's rule map is what makes the rest of the single-home rows assertable: it names each
    ///     removed sentence and the home it moved to. It records the two homes above, so the map and
    ///     these cases cannot disagree about where the text went.
    /// </summary>
    [Fact]
    public void TheRuleMapRecordsAHomeForEveryRuleThatMoved()
    {
        Assert.True(
            File.Exists(RuleMapPath),
            $"The rule map must live at '{RuleMapPath}'. Task T49 writes it before T50, one row per " +
            "removed sentence, and it is what stops the text growing back.");

        string map = File.ReadAllText(RuleMapPath);
        Assert.Contains("task-gate/references/rework.md", map, StringComparison.Ordinal);
        Assert.Contains("cleanupcode", map, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every agent body holding <paramref name="text"/>, by file name.</summary>
    private static string[] AgentsContaining(string text)
    {
        return
        [
            .. Directory.GetFiles(AgentsRoot, "*.md", SearchOption.AllDirectories)
                .Where(path => File.ReadAllText(path).Contains(text, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
        ];
    }

    private static string ReadProductFile(string relativePath)
    {
        string path = Path.Combine(ProductRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Expected the canonical product file at '{path}'.");
        return File.ReadAllText(path);
    }
}

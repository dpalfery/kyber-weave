using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// This repository's own gate choices: onboarding recorded as done, and a task that edits only a workflow, a shell
/// script or the review configuration selecting at least one gate.
/// </summary>
/// <remarks>
/// <para>
/// A gate runs when one of its <c>include</c> globs matches a changed file, so the globs — not a list of gate ids —
/// are what decide whether a change was checked. Three file kinds had no glob covering them anywhere in the
/// configuration, so a task editing only one of them ran nothing and reported <c>NONE_APPLICABLE</c>, which a
/// reader cannot tell apart from a change that was checked and passed.
/// </para>
/// <para>
/// The cases are expressed over file kinds rather than over the agents that are known to be missing a gate: an
/// agent-scoped check would have to name today's gaps, and the next file kind to appear would then pass. The two
/// agents whose file kinds differ sharply from the .NET gate set they alias are named because the plan settles
/// their gate sets explicitly.
/// </para>
/// </remarks>
public sealed class TaskGatesRepositoryConfigTests
{
    /// <summary>The configuration file's own top-level <c>onboarding</c> key, as the loader does not model it yet.</summary>
    private const string OnboardingKey = "onboarding";

    private const string AgentGithubDevops = "github-devops";
    private const string AgentSqlDatabaseArchitect = "sql-database-architect";

    /// <summary>A change of only that file kind selects at least one task gate.</summary>
    /// <remarks>
    /// The sample path is one concrete file of the kind; a glob that matches it is what a task gate would be
    /// selected by. Without one, the task's digest reports that no gate applied, which is a valid result but no
    /// evidence that anything was. The message names the kind so the fix — a gate whose <c>include</c> lists it —
    /// is obvious from the failure alone.
    /// </remarks>
    [Theory]
    [InlineData(".github/workflows/ci.yml", "workflow YAML")]
    [InlineData("scripts/proc-guard.sh", "a shell script")]
    [InlineData(".kyber-weave/kyber-weave.yml", "the review configuration")]
    public void ATaskThatEditsOnlyThatFileKindSelectsAtLeastOneGate(string changed, string kind)
    {
        KyberWeaveConfig config = Load();

        string[] selected = config.Review.TaskGates
            .SelectMany(pair => pair.Value
                .Where(gate => gate.Include.Any(pattern => PathGlob.IsMatch(pattern, changed)))
                .Select(gate => $"{pair.Key}/{gate.Id}"))
            .ToArray();

        Assert.True(
            selected.Length > 0,
            $"No review.task-gates entry selects '{changed}', so a task that edits only {kind} runs no gate and " +
            "reports NONE_APPLICABLE. Add a gate whose include covers that file kind.");
    }

    /// <summary>
    /// This repository records that it has answered the onboarding step.
    /// </summary>
    /// <remarks>
    /// An unset value stops <c>run start</c> to offer the onboarding skill, and this repository's own runs would
    /// see that stop on every start until someone answered. The value is read from the file rather than from a
    /// parsed property because the setting's only surface is the file an operator writes.
    /// </remarks>
    [Fact]
    public void OnboardingIsRecordedAsDone()
    {
        YamlMappingNode root = LoadRoot();

        Assert.Equal(
            "done",
            Scalar(root, OnboardingKey));
    }

    /// <summary>
    /// <c>github-devops</c> selects a gate for the workflows it edits, rather than only the .NET gates it aliases.
    /// </summary>
    /// <remarks>
    /// The role mostly edits <c>.github/workflows/</c> and Dockerfiles; the .NET format and build globs it aliases
    /// never match those, so every one of its gates is skipped. The open question in
    /// <c>docs/todo/task-gate-config-review.md</c> is whether that is intended, and the answer recorded here is
    /// that it is not.
    /// </remarks>
    [Fact]
    public void GithubDevopsSelectsAGateForTheWorkflowsItEdits()
    {
        AssertAgentSelects(AgentGithubDevops, ".github/workflows/ci.yml");
    }

    /// <summary>
    /// <c>sql-database-architect</c> selects a gate for the SQL it edits, rather than only the .NET gates it aliases.
    /// </summary>
    /// <remarks>
    /// The same open question as the workflow case: an agent that mostly edits <c>.sql</c> and migration files runs
    /// a gate set whose globs never match them.
    /// </remarks>
    [Fact]
    public void SqlDatabaseArchitectSelectsAGateForTheSqlItEdits()
    {
        AssertAgentSelects(AgentSqlDatabaseArchitect, "db/migrations/0001-init.sql");
    }

    private static void AssertAgentSelects(string agent, string changed)
    {
        KyberWeaveConfig config = Load();

        Assert.True(
            config.Review.TaskGates.TryGetValue(agent, out IReadOnlyList<ReviewTaskGate>? gates),
            $"review.task-gates declares no entry for '{agent}', so every one of its changes runs no gate.");
        string[] selected = gates
            .Where(gate => gate.Include.Any(pattern => PathGlob.IsMatch(pattern, changed)))
            .Select(gate => gate.Id)
            .ToArray();

        Assert.True(
            selected.Length > 0,
            $"None of '{agent}'s gates selects '{changed}'. The agent that edits these files must have a gate for " +
            "them, or its task digest reports executed evidence it does not have.");
    }

    private static KyberWeaveConfig Load()
    {
        return KyberWeaveConfigLoader.Load(KyberWeaveTestPaths.ToolRoot);
    }

    private static YamlMappingNode LoadRoot()
    {
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, ".kyber-weave", "kyber-weave.yml");
        YamlStream stream = new();
        using (StreamReader reader = new(path))
        {
            stream.Load(reader);
        }

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string? Scalar(YamlMappingNode map, string key)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> pair in map.Children)
        {
            if (pair.Key is YamlScalarNode name && string.Equals(name.Value, key, StringComparison.Ordinal))
                return (pair.Value as YamlScalarNode)?.Value;
        }

        return null;
    }
}

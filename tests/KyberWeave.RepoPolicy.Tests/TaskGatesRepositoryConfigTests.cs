using KyberWeave.Tests.Configuration;

using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Corpus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// This repository's own gate choices: onboarding recorded as done, a task that edits only a workflow, a shell
/// script or the review configuration selecting at least one gate, and each developer difficulty tier running the
/// technology gates whether or not its task names a technology.
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
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>The configuration file's own top-level <c>onboarding</c> key, as the loader does not model it yet.</summary>
    private const string OnboardingKey = "onboarding";

    private const string TechnologyCsharp = "csharp";
    private const string TechnologyGithubActions = "github-actions";
    private const string AgentSqlDatabaseArchitect = "sql-database-architect";

    /// <summary>The three developer agents the roster dispatches a task to by its difficulty.</summary>
    private static readonly string[] DeveloperTiers = ["dev-easy", "dev-medium", "dev-hard"];

    /// <summary>The KyberDash gates a C# change under <c>dash/</c> must select.</summary>
    private static readonly string[] KyberDashGateIds = ["dash-build", "dash-unit-application", "dash-unit-domain"];

    /// <summary>A change of only that file kind selects at least one task gate.</summary>
    /// <remarks>
    /// The sample path is one concrete file of the kind; a glob that matches it is what a task gate would be
    /// selected by. Without one, the task's digest reports that no gate applied, which is a valid result but no
    /// evidence that anything was. The message names the kind so the fix — a gate whose <c>include</c> lists it —
    /// is obvious from the failure alone.
    /// </remarks>
    [Theory]
    [InlineData(".github/workflows/ci.yml", "workflow YAML")]
    [InlineData("scripts/fast-loop.sh", "a shell script")]
    [InlineData(".kyber-weave/kyber-weave.yml", "the review configuration")]
    public void ATaskThatEditsOnlyThatFileKindSelectsAtLeastOneGate(string changed, string kind)
    {
        KyberWeaveConfig config = Load();

        string[] selected = config.Review.TaskGates
            .Concat(config.Review.TechnologyGates)
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
    /// The <c>github-actions</c> technology selects a gate for the workflows it edits, rather than only the .NET
    /// gates it aliases.
    /// </summary>
    /// <remarks>
    /// The role mostly edits <c>.github/workflows/</c> and Dockerfiles; the .NET format and build globs it aliases
    /// never match those, so every one of its gates is skipped. The open question in
    /// <c>docs/todo/task-gate-config-review.md</c> is whether that is intended, and the answer recorded here is
    /// that it is not. The entry is keyed by technology since the agent-keyed ones moved.
    /// </remarks>
    [Fact]
    public void GithubActionsSelectsAGateForTheWorkflowsItEdits()
    {
        AssertTechnologySelects(TechnologyGithubActions, ".github/workflows/ci.yml");
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

    /// <summary>
    /// Every developer difficulty tier resolves gates even when the task names no technology.
    /// </summary>
    /// <remarks>
    /// The resolver reads <c>review.task-gates</c> before the task's technology, and a task may name no technology
    /// at all. Without an entry the resolver falls through to the technology-keyed gates, and a task that names no
    /// technology has none — so the worker reports that no gate applied to a change nothing checked. Each tier
    /// aliases every technology gate once. This case keeps that union from going stale: a gate added to a technology
    /// and forgotten in the tiers is reported here, not in the next run that fails to check it.
    /// </remarks>
    [Fact]
    public void EveryDeveloperTierResolvesEveryTechnologyGate()
    {
        KyberWeaveConfig config = Load();

        string[] technologyGateIds = config.Review.TechnologyGates.Values
            .SelectMany(gates => gates)
            .Select(gate => gate.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(technologyGateIds);

        foreach (string tier in DeveloperTiers)
        {
            IReadOnlyList<ReviewTaskGate> gates = TaskGatesFor(config, tier);
            string[] tierIds = gates.Select(gate => gate.Id).ToArray();
            string[] missing = technologyGateIds.Except(tierIds, StringComparer.Ordinal).ToArray();

            Assert.True(
                missing.Length == 0,
                $"'{tier}' is missing gates the technology entries declare: {string.Join(", ", missing)}. " +
                "A task that names no technology would then skip them without reporting anything.");

            Assert.Equal(tierIds.Length, tierIds.Distinct(StringComparer.Ordinal).Count());

            foreach (string changed in new[] { "src/KyberWeave.Core/X.cs", ".kyber-weave/kyber-weave.yml" })
                Assert.True(
                    SelectedIds(gates, changed).Length > 0,
                    $"None of '{tier}'s gates selects '{changed}', so a task that names no technology runs nothing.");
        }
    }

    /// <summary>
    /// A C# change under KyberDash builds and tests KyberDash.
    /// </summary>
    /// <remarks>
    /// KyberDash's server is its own solution under <c>dash/server</c>. The KyberWeave gates cannot check it: the
    /// solution they build does not contain it, and narrowing their globs off <c>dash/</c> is exactly what stops
    /// a dash change from running ten minutes of KyberWeave tests. Without these gates a dash C# change is built
    /// and tested by nothing before the council runs.
    /// </remarks>
    [Fact]
    public void CsharpRunsTheKyberDashGatesForAKyberDashChange()
    {
        IReadOnlyList<ReviewTaskGate> gates = TechnologyGatesFor(TechnologyCsharp);

        string[] changes =
        [
            "dash/server/KyberDash.Core/X.cs",
            "dash/server.tests/KyberDash.Domain.Tests/XTests.cs"
        ];

        foreach (string changed in changes)
        {
            ReviewTaskGate[] selected = gates
                .Where(gate => gate.Include.Any(pattern => PathGlob.IsMatch(pattern, changed)))
                .ToArray();
            string[] selectedIds = selected.Select(gate => gate.Id).ToArray();

            foreach (string id in KyberDashGateIds)
                Assert.True(
                    selectedIds.Contains(id, StringComparer.Ordinal),
                    $"'{changed}' does not select the KyberDash gate '{id}', so a dash C# change is not built and " +
                    "tested before review.");

            string[] solutionGates = selected
                .Where(gate => gate.Run.Any(argument => string.Equals(argument, "KyberWeave.sln", StringComparison.Ordinal)))
                .Select(gate => gate.Id)
                .ToArray();

            Assert.True(
                solutionGates.Length == 0,
                $"'{changed}' selects the KyberWeave solution gates {string.Join(", ", solutionGates)}. A dash change " +
                "must not run the KyberWeave solution's build, format or test gates.");
        }
    }

    /// <summary>A KyberWeave source change selects no KyberDash gate.</summary>
    /// <remarks>
    /// The two sets are disjoint on purpose: a source change under <c>src/</c> is built by the KyberWeave gates and
    /// has no bearing on KyberDash's server, whose own build and tests cost minutes.
    /// </remarks>
    [Fact]
    public void AKyberWeaveSourceChangeSelectsNoKyberDashGate()
    {
        IReadOnlyList<ReviewTaskGate> gates = TechnologyGatesFor(TechnologyCsharp);
        string[] selected = SelectedIds(gates, "src/KyberWeave.Core/X.cs");

        string[] dashGates = selected
            .Where(id => id.StartsWith("dash-", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            dashGates.Length == 0,
            $"A change to 'src/KyberWeave.Core/X.cs' selects the KyberDash gates {string.Join(", ", dashGates)}, " +
            "which have nothing to check for it.");

        Assert.Contains("dotnet-build", selected, StringComparer.Ordinal);
        Assert.Contains("format-whitespace", selected, StringComparer.Ordinal);
    }

    /// <summary>The <c>csharp</c> technology runs the repository-policy tests for a policy-test change.</summary>
    /// <remarks>
    /// The review configuration and its tests are repository policy. Without this gate a C# task that edits them
    /// resolves no test that reads them, so a broken expectation reaches review with no RED or GREEN evidence.
    /// </remarks>
    [Fact]
    public void CsharpRunsThePolicyTestsForAPolicyTestChange()
    {
        string[] selected = SelectedIds(TechnologyGatesFor(TechnologyCsharp), "tests/KyberWeave.RepoPolicy.Tests/X.cs");

        Assert.Contains("test-policy", selected, StringComparer.Ordinal);
    }

    private static IReadOnlyList<ReviewTaskGate> TechnologyGatesFor(string technology)
    {
        KyberWeaveConfig config = Load();

        Assert.True(
            config.Review.TechnologyGates.TryGetValue(technology, out IReadOnlyList<ReviewTaskGate>? gates),
            $"review.technology-gates declares no entry for '{technology}'.");

        return gates;
    }

    private static IReadOnlyList<ReviewTaskGate> TaskGatesFor(KyberWeaveConfig config, string agent)
    {
        Assert.True(
            config.Review.TaskGates.TryGetValue(agent, out IReadOnlyList<ReviewTaskGate>? gates),
            $"review.task-gates declares no entry for '{agent}', so every one of its changes runs no gate.");

        return gates;
    }

    private static string[] SelectedIds(IReadOnlyList<ReviewTaskGate> gates, string changed) =>
        gates
            .Where(gate => gate.Include.Any(pattern => PathGlob.IsMatch(pattern, changed)))
            .Select(gate => gate.Id)
            .ToArray();

    private static void AssertTechnologySelects(string technology, string changed)
    {
        KyberWeaveConfig config = Load();

        Assert.True(
            config.Review.TechnologyGates.TryGetValue(technology, out IReadOnlyList<ReviewTaskGate>? gates),
            $"review.technology-gates declares no entry for '{technology}', so every one of its changes runs no gate.");
        AssertSelects(technology, gates, changed);
    }

    private static void AssertAgentSelects(string agent, string changed)
    {
        KyberWeaveConfig config = Load();

        Assert.True(
            config.Review.TaskGates.TryGetValue(agent, out IReadOnlyList<ReviewTaskGate>? gates),
            $"review.task-gates declares no entry for '{agent}', so every one of its changes runs no gate.");
        AssertSelects(agent, gates, changed);
    }

    private static void AssertSelects(string key, IReadOnlyList<ReviewTaskGate> gates, string changed)
    {
        string[] selected = gates
            .Where(gate => gate.Include.Any(pattern => PathGlob.IsMatch(pattern, changed)))
            .Select(gate => gate.Id)
            .ToArray();

        Assert.True(
            selected.Length > 0,
            $"None of '{key}'s gates selects '{changed}'. The worker that edits these files must have a gate for " +
            "them, or its task digest reports executed evidence it does not have.");
    }

    private static KyberWeaveConfig Load()
    {
        return ConfigFileReader.Load(Corpus.Root);
    }

    private static YamlMappingNode LoadRoot()
    {
        YamlStream stream = new();
        using (StringReader reader = new(Corpus.ReadText(".kyber-weave/kyber-weave.yml")))
        {
            stream.Load(reader);
        }

        Assert.NotEmpty(stream.Documents);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
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

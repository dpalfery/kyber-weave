using KyberWeave.Tests.Corpus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
///     Static reads of <c>ci.yml</c>'s <c>dash-e2e</c> job: the path filter that arms it, the read-only
///     permission it runs under, the isolated Playwright specs it executes, and the summary that must depend on
///     it.
/// </summary>
/// <remarks>
///     <para>
///         The job is the only place the isolated KyberDash Playwright specs run unattended. Two things go wrong
///         quietly if an edit drops them. First, the job is skipped on any pull request whose diff touches outside
///         <c>dash/</c>, so the parity suite would look green while never having run. Second, <c>ci-summary</c> is
///         the required check, and a skipped job counts as passed; without the dependency a failing e2e job never
///         reaches the summary.
///     </para>
///     <para>
///         The parity spec asserts what each harness collects and renders, so a regression hides if the step is
///         removed while the collection spec stays. Both specs are pinned here, each through the isolated config
///         rather than the default one whose web server targets the live dashboard.
///     </para>
///     <para>
///         This suite reads files only. It starts no process.
///     </para>
/// </remarks>
public sealed class DashE2eWorkflowTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string CiWorkflowPath = ".github/workflows/ci.yml";

    private const string DashE2eJob = "dash-e2e";

    private const string SummaryJob = "ci-summary";

    private const string ChangesJob = "changes";

    private const string IsolatedConfigPath = "dash/playwright.isolated.config.ts";

    private const string CollectionSpecPath = "dash/e2e/harness-collection.spec.ts";

    private const string ParitySpecPath = "dash/e2e/harness-content-parity.spec.ts";

    /// <summary>
    ///     The job runs only when the diff touches <c>dash/</c>, and under read-only permissions.
    /// </summary>
    /// <remarks>
    ///     The filter is what keeps an unrelated pull request from paying for the isolated boot, and the permission
    ///     is what lets the workflow run on a fork without a write scope it never uses. Dropping either turns a
    ///     guard into an assumption.
    /// </remarks>
    [Fact]
    public void DashE2eKeepsItsTriggerAndReadOnlyPermissions()
    {
        YamlMappingNode job = Job(CiWorkflowPath, DashE2eJob);

        Assert.Contains(ChangesJob, NeedsOf(job));

        Assert.Equal(
            "needs.changes.outputs.dash == 'true'",
            (Child(job, "if") as YamlScalarNode)?.Value);

        YamlMappingNode permissions = Assert.IsType<YamlMappingNode>(
            Child(job, "permissions"),
            exactMatch: false);
        string[] granted = permissions.Children
            .Select(entry =>
                $"{Assert.IsType<YamlScalarNode>(entry.Key).Value}: {Assert.IsType<YamlScalarNode>(entry.Value).Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            granted.SequenceEqual(["contents: read"], StringComparer.Ordinal),
            $"dash-e2e must grant exactly 'contents: read'; found: {string.Join(", ", granted)}.");
    }

    /// <summary>
    ///     The job runs the harness-collection conformance spec through the isolated config.
    /// </summary>
    /// <remarks>
    ///     The default config's web server targets the live dashboard on port 4747, so the spec must name the
    ///     isolated config explicitly; the spec itself boots the only server it talks to.
    /// </remarks>
    [Fact]
    public void DashE2eRunsTheHarnessCollectionSpecThroughTheIsolatedConfig()
    {
        string command = Assert.Single(RunsNaming(CiWorkflowPath, DashE2eJob, CollectionSpecPath));

        Assert.Contains(IsolatedConfigPath, command, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The job runs the cross-harness content parity spec through the isolated config, after the collection
    ///     spec.
    /// </summary>
    /// <remarks>
    ///     The parity suite is the only unattended check that a harness's content reaches the dashboard, so its
    ///     step disappearing would leave the collection spec green over a display regression. The order matters
    ///     for diagnosis: the conformance spec fails fast on a broken dashboard before the longer parity run.
    /// </remarks>
    [Fact]
    public void DashE2eRunsTheHarnessContentParitySpecAfterTheCollectionSpec()
    {
        string command = Assert.Single(RunsNaming(CiWorkflowPath, DashE2eJob, ParitySpecPath));

        Assert.Contains(IsolatedConfigPath, command, StringComparison.Ordinal);

        List<Step> steps = Steps(Job(CiWorkflowPath, DashE2eJob));
        int collectionIndex = steps.FindIndex(step => Names(step, CollectionSpecPath));
        int parityIndex = steps.FindIndex(step => Names(step, ParitySpecPath));

        Assert.True(
            collectionIndex >= 0,
            $"ci.yml job '{DashE2eJob}' runs no step naming '{CollectionSpecPath}'. Steps: {Describe(steps)}");
        Assert.True(
            parityIndex > collectionIndex,
            $"ci.yml job '{DashE2eJob}' runs '{ParitySpecPath}' before '{CollectionSpecPath}', or in the same step. " +
            $"Steps: {Describe(steps)}");
    }

    /// <summary>
    ///     The required summary check depends on the e2e job, so a skipped or failing parity run cannot pass.
    /// </summary>
    /// <remarks>
    ///     A skipped job reports success, and <c>ci-summary</c> is the only required check; without the dependency a
    ///     path-filtered job that never ran would look green.
    /// </remarks>
    [Fact]
    public void CiSummaryDependsOnDashE2e()
    {
        IReadOnlyList<string> needs = NeedsOf(Job(CiWorkflowPath, SummaryJob));

        Assert.Contains(DashE2eJob, needs);
    }

    private static bool Names(Step step, string path) =>
        step.Run is not null && step.Run.Contains(path, StringComparison.Ordinal);

    private static string Describe(IEnumerable<Step> steps) =>
        string.Join(" | ", steps.Select(step => step.Name ?? step.Run ?? "(unnamed)"));

    /// <summary>The run commands of the job's steps whose script names <paramref name="path" />.</summary>
    private static List<string> RunsNaming(string relativePath, string jobName, string path)
    {
        return Steps(Job(relativePath, jobName))
            .Where(step => Names(step, path))
            .Select(step => step.Run!)
            .ToList();
    }

    /// <summary>The <c>needs</c> of a job, in either the scalar or sequence form.</summary>
    private static IReadOnlyList<string> NeedsOf(YamlMappingNode job)
    {
        return Child(job, "needs") switch
        {
            YamlSequenceNode sequence => sequence.Children
                .OfType<YamlScalarNode>()
                .Select(node => node.Value ?? string.Empty)
                .ToList(),
            YamlScalarNode scalar when scalar.Value is not null => [scalar.Value],
            _ => []
        };
    }

    private static YamlMappingNode Job(string relativePath, string jobName)
    {
        return Child(Root(relativePath), "jobs") is YamlMappingNode jobs && Child(jobs, jobName) is YamlMappingNode job
            ? job
            : throw new InvalidOperationException($"{relativePath} has no job named '{jobName}'.");
    }

    private static List<Step> Steps(YamlMappingNode job)
    {
        if (Child(job, "steps") is not YamlSequenceNode steps) return [];

        return steps.Children
            .OfType<YamlMappingNode>()
            .Select(step => new Step(
                (Child(step, "name") as YamlScalarNode)?.Value,
                (Child(step, "run") as YamlScalarNode)?.Value))
            .ToList();
    }

    private static YamlMappingNode Root(string relativePath)
    {
        Assert.True(Corpus.Exists(relativePath), $"The workflow is missing at '{relativePath}'.");

        using StringReader reader = new(Corpus.ReadText(relativePath));
        YamlStream stream = new();
        stream.Load(reader);

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlNode? Child(YamlMappingNode map, string key)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> pair in map.Children)
            if (pair.Key is YamlScalarNode scalar && scalar.Value == key)
                return pair.Value;

        return null;
    }

    private sealed record Step(string? Name, string? Run);
}

using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
///     Static reads of the workflows that police pull request naming: <c>ci.yml</c> holds the check and
///     the required summary that must depend on it, and <c>pr-naming-recheck.yml</c> handles the one event
///     CI deliberately does not listen to.
/// </summary>
/// <remarks>
///     <para>
///         The check sits in its own job, so a bad title fails a fast job instead of the whole build. That
///         arrangement has two failure modes this suite pins. First, a job that is skipped counts as passed,
///         so <c>ci-summary</c> — the only required check — must depend on the naming job, or a branch that
///         never ran it looks green. Second, adding <c>edited</c> to the CI trigger would cancel and restart
///         every CI run on a description edit, so the recheck lives in its own workflow instead.
///     </para>
///     <para>
///         This suite reads files only. It starts no process.
///     </para>
/// </remarks>
public sealed class PrNamingWorkflowTests
{
    private const string CiWorkflowPath = ".github/workflows/ci.yml";

    private const string RecheckWorkflowPath = ".github/workflows/pr-naming-recheck.yml";

    private const string CheckScriptPath = "scripts/check-pr-name.sh";

    private const string PrNamingJob = "pr-naming";

    private const string SummaryJob = "ci-summary";

    /// <summary>
    ///     CI declares the naming job, and that job runs the check script, so there is one naming rule in
    ///     the repository and CI is where it runs unattended.
    /// </summary>
    [Fact]
    public void CiDeclaresThePrNamingJobThatRunsTheCheck()
    {
        YamlMappingNode job = Job(CiWorkflowPath, PrNamingJob);

        List<string> runs = Steps(job)
            .Select(step => step.Run)
            .Where(run => run is not null && run.Contains(CheckScriptPath, StringComparison.Ordinal))
            .Select(run => run!)
            .ToList();

        Assert.True(
            runs.Count > 0,
            $"ci.yml job '{PrNamingJob}' runs no step naming '{CheckScriptPath}'. Steps: " +
            string.Join(" | ", Steps(job).Select(step => step.Name ?? step.Run ?? "(unnamed)")));
    }

    /// <summary>
    ///     The only required check depends on the naming job. Without the dependency a skipped naming job
    ///     counts as passed, which is exactly how a non-conforming title reaches the default branch.
    /// </summary>
    [Fact]
    public void CiSummaryDependsOnThePrNamingJob()
    {
        IReadOnlyList<string> needs = NeedsOf(CiWorkflowPath, SummaryJob);

        Assert.Contains(PrNamingJob, needs);
    }

    /// <summary>
    ///     An edited description is handled by the recheck workflow, and CI's pull request trigger is left
    ///     alone: adding <c>edited</c> there would cancel and restart every run on a description edit.
    /// </summary>
    [Fact]
    public void AnEditedDescriptionIsHandledOutsideCi()
    {
        Assert.DoesNotContain("edited", PullRequestTypes(CiWorkflowPath));

        string recheckPath = Path.Combine(KyberWeaveTestPaths.ToolRoot, RecheckWorkflowPath);
        Assert.True(File.Exists(recheckPath), $"The recheck workflow is missing at '{recheckPath}'.");

        string recheck = File.ReadAllText(recheckPath);
        Assert.Contains("edited", recheck, StringComparison.Ordinal);
        Assert.Contains("pull_request", recheck, StringComparison.Ordinal);
        Assert.Contains(CheckScriptPath, recheck, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The pull request trigger types <c>ci.yml</c> declares. A trigger with no <c>types</c> list
    ///     declares none of its own, and a scalar form such as <c>pull_request:</c> declares none either.
    /// </summary>
    private static IReadOnlyList<string> PullRequestTypes(string relativePath)
    {
        YamlMappingNode root = Root(relativePath);
        YamlNode? trigger = Child(root, "on") is YamlMappingNode triggers ? Child(triggers, "pull_request") : null;

        if (trigger is not YamlMappingNode pullRequest) return [];

        return Child(pullRequest, "types") is YamlSequenceNode types
            ? types.Children.OfType<YamlScalarNode>().Select(node => node.Value ?? string.Empty).ToList()
            : [];
    }

    /// <summary>The <c>needs</c> of <paramref name="jobName" />, in either the scalar or sequence form.</summary>
    private static IReadOnlyList<string> NeedsOf(string relativePath, string jobName)
    {
        YamlMappingNode job = Job(relativePath, jobName);
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
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"The workflow is missing at '{path}'.");

        using StreamReader reader = new(path);
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

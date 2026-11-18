using KyberWeave.Tests.Corpus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
///     Pins the invariant the comment above <c>ci.yml</c>'s <c>changes</c> job states: a job with neither
///     <c>needs:</c> nor <c>if:</c> runs on every pull request, whatever the path filter says.
/// </summary>
/// <remarks>
///     The comment is where a reader, or the next edit to the filters, learns which jobs no path can
///     skip. A job made unconditional without the comment naming it leaves the workflow describing a
///     filter that no longer exists. This suite reads files only; it starts no process.
/// </remarks>
public sealed class CiAlwaysRunJobsTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string CiWorkflowPath = ".github/workflows/ci.yml";

    private const string ChangesJob = "changes";

    /// <summary>
    ///     Every job the path filter cannot skip is named in the comment above <c>changes</c>, in the job
    ///     ids the rest of the workflow uses.
    /// </summary>
    [Fact]
    public void TheChangesJobCommentNamesEveryJobWithNoPathFilter()
    {
        string workflow = Corpus.ReadText(CiWorkflowPath);

        List<string> unnamed = [.. JobsWithNoPathFilter(workflow)
            .Where(job => !CommentAbove(workflow, ChangesJob).Contains(job, StringComparison.Ordinal))];

        Assert.True(
            unnamed.Count == 0,
            $"ci.yml jobs {string.Join(", ", unnamed)} carry no 'needs:' and no 'if:', so no path filter can " +
            $"skip them, but the comment above '{ChangesJob}:' does not name them.");
    }

    /// <summary>The job ids that run regardless of the pull request's diff, in the order they are declared.</summary>
    private static IReadOnlyList<string> JobsWithNoPathFilter(string workflow)
    {
        YamlStream stream = new();
        using StringReader reader = new(workflow);
        stream.Load(reader);

        YamlMappingNode root = (YamlMappingNode)stream.Documents[0].RootNode;
        YamlMappingNode jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];

        return
        [
            .. jobs.Children
                .Where(entry => entry.Value is YamlMappingNode job &&
                    !job.Children.ContainsKey(new YamlScalarNode("needs")) &&
                    !job.Children.ContainsKey(new YamlScalarNode("if")))
                .Select(entry => ((YamlScalarNode)entry.Key).Value!)
                .Where(name => !string.Equals(name, ChangesJob, StringComparison.Ordinal))
        ];
    }

    /// <summary>The comment lines directly above a job, joined.</summary>
    private static string CommentAbove(string workflow, string jobName)
    {
        string[] lines = workflow.Split('\n');
        int jobLine = Array.FindIndex(lines, line => line.TrimEnd('\r') == $"  {jobName}:");
        Assert.True(jobLine > 0, $"ci.yml declares no '{jobName}:' job.");

        List<string> comment = [];
        for (int index = jobLine - 1; index >= 0; index--)
        {
            string line = lines[index].TrimEnd('\r');
            if (!line.TrimStart().StartsWith('#'))
                break;

            comment.Insert(0, line);
        }

        return string.Join('\n', comment);
    }
}

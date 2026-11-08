using System.Text;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>kyber-weave run start --resume &lt;branch&gt;</c> reads the <c>Task:</c> lines a run
/// leaves on its branch and prints the completed tasks with the reports they committed.
/// </summary>
/// <remarks>
/// <para>
/// A second machine, or a session that lost its context, opens with no idea how far the run
/// got. Without this the only way to find out is an architect pass over the plan, which the
/// measured run paid for with 225 seconds and 64 tool calls. The commits are already there:
/// every finished task commits with a <c>Task: &lt;id&gt;</c> line and its report.
/// </para>
/// <para>
/// The reader takes the line from the commit's trailer area — the last paragraph — because a
/// commit body is prose and prose quotes other tasks. Both halves of that are pinned below:
/// an inline mention is not a record, and neither is a standalone line buried in a quoted
/// report. A reader that scanned every line would resurrect finished or unrelated ids.
/// </para>
/// <para>
/// The surface is a branch, not a pull-request number: narrowing it that way is a separate
/// decision the todo records, so the refusal is pinned here rather than left to be widened
/// silently.
/// </para>
/// </remarks>
public sealed class RunStartResumeTests : IDisposable
{
    private const string Branch = "claude/feat/resume-reader";

    /// <summary>The two tasks the branch records as finished.</summary>
    private const string FirstTask = "T41";
    private const string SecondTask = "T42";

    /// <summary>A task id that appears only in an ordinary sentence, and one that appears only in a quoted report.</summary>
    private const string MentionedInASentence = "T43";
    private const string QuotedFromAnotherReport = "T44";

    /// <summary>The reports the two finished tasks committed, which resume has to print back.</summary>
    private const string FirstResult = "alpha-result: the reader lists completed tasks.";
    private const string SecondResult = "beta-result: the reader prints each prior report.";

    private readonly RunStartProbe _probe = BranchWithFinishedTasks();

    public void Dispose()
    {
        _probe.Dispose();
    }

    /// <summary>
    /// A branch carrying two finished tasks, one commit that only mentions a task in a
    /// sentence, and one that quotes a task inside a report. Nothing is uncommitted.
    /// </summary>
    private static RunStartProbe BranchWithFinishedTasks()
    {
        RunStartProbe probe = RunStartProbe.Create();
        probe.Git("switch", "-c", Branch, "origin/main");

        Commit(probe, "first.txt", TaskCommit(FirstResult, FirstTask));
        Commit(probe, "second.txt", TaskCommit(SecondResult, SecondTask));

        Commit(
            probe,
            "third.txt",
            $"""
             chore(squad): a commit that names a task in a sentence

             This superseded the earlier Task: {MentionedInASentence} note about the reader.
             """);

        Commit(
            probe,
            "fourth.txt",
            $"""
             docs(squad): a commit that quotes another report

             Quoted from an earlier report:

             Task: {QuotedFromAnotherReport}

             No task of its own is recorded here.
             """);

        Assert.Equal(string.Empty, probe.GitOutput("status", "--porcelain").Trim());
        return probe;
    }

    /// <summary>Writes a file and commits it with <paramref name="message"/>.</summary>
    private static void Commit(RunStartProbe probe, string fileName, string message)
    {
        File.WriteAllText(Path.Combine(probe.Root, fileName), message + "\n", Encoding.UTF8);
        probe.Git("add", "-A");
        probe.Git("commit", "-m", message);
    }

    /// <summary>A commit message in the shape the execution contract prescribes.</summary>
    private static string TaskCommit(string result, string taskId) =>
        $"""
         test(squad): {taskId}'s work

         {result}

         Task: {taskId}
         """;

    /// <summary>
    /// Resuming a branch reports every finished task and the report it committed, and nothing
    /// that only looks like a task record.
    /// </summary>
    [Fact]
    public void ResumeListsCompletedTasksAndTheirReportsFromTaskCommitLines()
    {
        ProbeResult result = _probe.RunStart(
            "--harness", "claude", "--type", "feat", "--slug", "resume-reader", "--resume", Branch);

        Assert.True(result.ExitCode == 0, $"resuming an existing branch must exit 0, not {result.ExitCode}. {result.Output}");

        Assert.True(
            result.Output.Contains(FirstTask, StringComparison.Ordinal),
            $"resume must list the completed task {FirstTask}. It printed: {result.Output}");
        Assert.True(
            result.Output.Contains(FirstResult, StringComparison.Ordinal),
            $"resume must print {FirstTask}'s prior report. It printed: {result.Output}");
        Assert.True(
            result.Output.Contains(SecondTask, StringComparison.Ordinal),
            $"resume must list the completed task {SecondTask}. It printed: {result.Output}");
        Assert.True(
            result.Output.Contains(SecondResult, StringComparison.Ordinal),
            $"resume must print {SecondTask}'s prior report. It printed: {result.Output}");

        Assert.False(
            result.Output.Contains(MentionedInASentence, StringComparison.Ordinal),
            $"a Task: mention inside a sentence is not a task record, so {MentionedInASentence} must not " +
            $"be reported as completed. It printed: {result.Output}");
        Assert.False(
            result.Output.Contains(QuotedFromAnotherReport, StringComparison.Ordinal),
            $"a Task: line in a quoted report is outside the trailer area, so {QuotedFromAnotherReport} " +
            $"must not be reported as completed. It printed: {result.Output}");
    }

    /// <summary>
    /// A pull-request number is not a resume target. The form is a branch, and widening it is a
    /// separate decision recorded in the open todo, so a refusal here is the correct surface.
    /// </summary>
    [Fact]
    public void APullRequestNumberIsNotAcceptedAsAResumeTarget()
    {
        ProbeResult result = _probe.RunStart(
            "--harness", "claude", "--type", "feat", "--slug", "resume-reader", "--resume", "412");

        Assert.True(
            result.ExitCode == 2,
            $"a pull-request number is refused input and must exit 2, not {result.ExitCode}. {result.Output}");
        Assert.Contains("412", result.Output, StringComparison.Ordinal);
        Assert.False(
            _probe.BranchExists("412"),
            "a refused resume target must create no branch.");
    }
}

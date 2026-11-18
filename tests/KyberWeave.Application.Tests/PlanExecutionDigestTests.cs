using KyberWeave.Core.Arbiter.Plans;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What the task file's execution digest covers. The digest exists so the conductor can tell
/// a task file written from this plan from one written from an older draft; the whole point is
/// that it hashes the parsed plan, not the file.
/// </summary>
/// <remarks>
/// <para>
/// The plan's status moves from Draft to Ready and its decision rows are answered during the
/// same finalization that writes the task file, so a digest over the raw text would mark every
/// finished file stale and force an architect dispatch on every amendment of prose the Arbiter
/// never reads. The opposite mistake is as costly: a digest that ignores a task edit lets the
/// conductor paste a prompt the plan has moved past. Both directions are pinned here, because
/// a test that only proves "a task edit changes it" is satisfied by hashing the whole file.
/// </para>
/// <para>
/// The digest is reached through <see cref="PlanTaskFileProbe" />, which resolves
/// <c>PlanDocumentParser.ExecutionDigest(PlanDocument)</c> by reflection: this batch is
/// authored before that method exists. Once it lands, the assertions are the property.
/// </para>
/// </remarks>
public sealed class PlanExecutionDigestTests
{
    /// <summary>
    /// A status flip is the finalization itself: the plan the architect wrote the task file
    /// from is the same plan after its status moves, so the digest must not change.
    /// </summary>
    [Fact]
    public void AStatusFlipLeavesTheExecutionDigestUnchanged()
    {
        string current = PlanTaskFileProbe.PlanMarkdown();
        string draft = current.Replace("status: current", "status: draft", StringComparison.Ordinal);

        Assert.Equal("current", PlanDocumentParser.Parse(current).Status);
        Assert.Equal("draft", PlanDocumentParser.Parse(draft).Status);

        Assert.Equal(
            PlanTaskFileProbe.ExecutionDigest(current),
            PlanTaskFileProbe.ExecutionDigest(draft));
    }

    /// <summary>
    /// Answering a decision row is an edit to prose the Arbiter never reads: the tasks, their
    /// files, their dependencies and the contract are untouched, so the file is not stale.
    /// </summary>
    [Fact]
    public void ADecisionsRowEditLeavesTheExecutionDigestUnchanged()
    {
        string baseline = PlanTaskFileProbe.PlanMarkdown();
        string answered = baseline.Replace(
            "Tracked in git, so a push carries it.",
            "Tracked in git, so a push carries it to another machine.",
            StringComparison.Ordinal);

        Assert.NotEqual(baseline, answered);

        Assert.Equal(
            PlanTaskFileProbe.ExecutionDigest(baseline),
            PlanTaskFileProbe.ExecutionDigest(answered));
    }

    /// <summary>
    /// The task text is what the Arbiter compares a dispatch against, so a rewritten task must
    /// mark the file stale: the prompt beside it was written from the old words.
    /// </summary>
    [Fact]
    public void ATaskEditChangesTheExecutionDigest()
    {
        string baseline = PlanTaskFileProbe.PlanMarkdown();
        string rewritten = baseline.Replace(
            "### T1: The task file validator",
            "### T1: The task file validator and its digest",
            StringComparison.Ordinal);

        Assert.NotEqual(
            PlanDocumentParser.Parse(baseline).Tasks[0].Title,
            PlanDocumentParser.Parse(rewritten).Tasks[0].Title);

        Assert.NotEqual(
            PlanTaskFileProbe.ExecutionDigest(baseline),
            PlanTaskFileProbe.ExecutionDigest(rewritten));
    }

    /// <summary>
    /// The test contract is a parsed field the readiness rules read, so a changed row is a
    /// changed plan even when no task moved.
    /// </summary>
    [Fact]
    public void ATestContractRowEditChangesTheExecutionDigest()
    {
        string baseline = PlanTaskFileProbe.PlanMarkdown();
        string rewritten = baseline.Replace(
            "The task file is validated.",
            "The task file is validated, then checked against its plan.",
            StringComparison.Ordinal);

        Assert.NotEqual(
            PlanDocumentParser.Parse(baseline).ContractRows[0].ObservableBehaviour,
            PlanDocumentParser.Parse(rewritten).ContractRows[0].ObservableBehaviour);

        Assert.NotEqual(
            PlanTaskFileProbe.ExecutionDigest(baseline),
            PlanTaskFileProbe.ExecutionDigest(rewritten));
    }

    /// <summary>
    /// The out-of-scope paths are what a scope-drift dispatch is judged against, so moving one
    /// changes the plan the task file was written from.
    /// </summary>
    [Fact]
    public void AnOutOfScopeEditChangesTheExecutionDigest()
    {
        string baseline = PlanTaskFileProbe.PlanMarkdown();
        string rewritten = baseline.Replace(
            "- `docs/archive/**`",
            "- `docs/generated/**`",
            StringComparison.Ordinal);

        Assert.NotEqual(
            PlanDocumentParser.Parse(baseline).OutOfScope[0],
            PlanDocumentParser.Parse(rewritten).OutOfScope[0]);

        Assert.NotEqual(
            PlanTaskFileProbe.ExecutionDigest(baseline),
            PlanTaskFileProbe.ExecutionDigest(rewritten));
    }

    /// <summary>
    /// The development mode decides which contract the dispatch must satisfy, so a plan that
    /// changes mode is a different plan even with identical tasks.
    /// </summary>
    [Fact]
    public void AModeFlipChangesTheExecutionDigest()
    {
        string baseline = PlanTaskFileProbe.PlanMarkdown();
        string standard = baseline.Replace(
            "development-mode: test-first",
            "development-mode: standard",
            StringComparison.Ordinal);

        Assert.NotEqual(
            PlanDocumentParser.Parse(baseline).DevelopmentMode,
            PlanDocumentParser.Parse(standard).DevelopmentMode);

        Assert.NotEqual(
            PlanTaskFileProbe.ExecutionDigest(baseline),
            PlanTaskFileProbe.ExecutionDigest(standard));
    }

    /// <summary>
    /// A task's file scope, its dependencies and its skills are each read by a different rule
    /// — scope drift, readiness, ownership — so each must move the digest on its own.
    /// </summary>
    /// <param name="edit">The task field to edit: <c>files</c>, <c>depends-on</c> or <c>skills</c>.</param>
    [Theory]
    [InlineData("files")]
    [InlineData("depends-on")]
    [InlineData("skills")]
    public void AFileScopeDependencyOrSkillsEditChangesTheExecutionDigest(string edit)
    {
        string baseline = PlanTaskFileProbe.PlanMarkdown();
        (string Rewritten, Func<PlanDocument, string> Read) change = edit switch
        {
            "files" => (
                baseline.Replace(
                    "`src/KyberWeave.Core.Docs/Validation/PlanTaskFile.cs`",
                    "`src/KyberWeave.Core.Docs/Validation/PlanTaskFileRow.cs`",
                    StringComparison.Ordinal),
                plan => string.Join(",", plan.Tasks[0].Files)),
            "depends-on" => (
                baseline.Replace(
                    "- **Depends on:** T1",
                    "- **Depends on:** none",
                    StringComparison.Ordinal),
                plan => string.Join(",", plan.Tasks[1].DependsOn)),
            _ => (
                baseline.Replace(
                    "- **Required skills:** `test-dev`",
                    "- **Required skills:** `docs-dev`",
                    StringComparison.Ordinal),
                plan => string.Join(",", plan.Tasks[1].Skills))
        };

        Assert.NotEqual(
            change.Read(PlanDocumentParser.Parse(baseline)),
            change.Read(PlanDocumentParser.Parse(change.Rewritten)));

        Assert.NotEqual(
            PlanTaskFileProbe.ExecutionDigest(baseline),
            PlanTaskFileProbe.ExecutionDigest(change.Rewritten));
    }
}

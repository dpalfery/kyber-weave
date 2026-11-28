using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The task-gate target reads the plans still in review. A closed-out plan that happens to hold the same task id
/// is not a source for the task or its technology, so a review rework of a plan in review takes its own task.
/// </summary>
public sealed class PlanTaskTargetStatusTests : IDisposable
{
    private const string Config = """
        ontology:
          docs-root: 6-Docs
          technologies: [csharp]
        """;

    private const string CompletedPlan = "6-Docs/plans/2026-05-01-completed.md";
    private const string ReviewPlan = "6-Docs/plans/2026-11-07-in-review.md";

    private readonly InMemoryReviewServices _services = new();
    private readonly string _repo;

    public PlanTaskTargetStatusTests()
    {
        _repo = Path.Combine(_services.Root, "repo");
        _services.Files.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        _services.Files.CreateDirectory(Path.Combine(_repo, "6-Docs", "plans"));
        _services.Files.WriteAllText(Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"), Config);
    }

    /// <summary>Releases the services.</summary>
    public void Dispose()
    {
        _services.Dispose();
    }

    [Fact]
    public async Task ThePlansTaskInReviewIsUsedRatherThanTheClosedOutPlansTaskWithTheSameId()
    {
        WritePlan(CompletedPlan, "**Status: Completed.**", "tests/Completed/T2.cs");
        WritePlan(ReviewPlan, "**Status: Review required.**", "tests/Review/T2.cs");

        PlanTaskTargetLookup lookup =
            await _services.Get<IPlanTaskTargetService>().FindAsync(_repo, EffectiveConfig(), "T2", CancellationToken.None);
        PlanTaskTargetMatch? match = lookup.Match;

        Assert.Null(lookup.Reason);
        Assert.NotNull(match);
        Assert.Equal(ReviewPlan, match.Plan);
        Assert.Contains("tests/Review/T2.cs", match.Entry.Files ?? []);
    }

    private KyberWeaveConfig EffectiveConfig() =>
        _services.Get<IKyberWeaveConfigRepository>().Load(_repo);

    /// <summary>Writes the fixture plan at <paramref name="relative"/> with a status and the second task's own files, and its task file.</summary>
    private void WritePlan(string relative, string status, string secondTaskFiles)
    {
        string plan = PlanTaskFileProbe.PlanMarkdown()
            .Replace("**Status: Ready.**", status, StringComparison.Ordinal)
            .Replace(
                $"- **Files:** `{PlanTaskFileProbe.SecondTaskFiles[0]}`",
                $"- **Files:** `{secondTaskFiles}`",
                StringComparison.Ordinal);

        _services.Files.WriteAllText(Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar)), plan);
        _services.Get<IPlanTaskService>().Run(_repo, EffectiveConfig(), relative, PlanTaskFileMode.Write);
    }
}

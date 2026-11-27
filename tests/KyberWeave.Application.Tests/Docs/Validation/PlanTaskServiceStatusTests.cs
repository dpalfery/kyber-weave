using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>
/// A plan is active only when its own status line says so: a status word quoted elsewhere in the body is
/// prose, not the plan's state.
/// </summary>
public sealed class PlanTaskServiceStatusTests
{
    private const string Root = "/kw/work/repo";

    private readonly InMemoryFileSystem _disk = new();
    private readonly InMemoryRepositoryFileStore _store;
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/kw/work");

    public PlanTaskServiceStatusTests()
    {
        _store = new InMemoryRepositoryFileStore(_disk);
        _paths = new FakePathResolver(_disk);
    }

    private static KyberWeaveConfig Config => KyberWeaveConfigDefaults.Product
        .WithOntology(new OntologyConfig { DocsRoots = ["docs"] });

    [Fact]
    public void ADraftPlanQuotingAReadyStatusIsNotActive()
    {
        _disk.CreateDirectory($"{Root}/docs/plans");
        _disk.WriteAllText(
            $"{Root}/docs/plans/2026-01-01-widget.md",
            "# Widget\n\n**Status: Draft.**\n\n## Notes\n\n"
            + "The finalize step writes `**Status: Ready, 2026-11-26.**` into the plan.\n");

        IReadOnlyList<string> plans = new PlanTaskService(_store, _paths, _environment).ActivePlans("repo", Config);

        Assert.Empty(plans);
    }

    [Fact]
    public void ADraftPlanQuotingABlockedStatusIsNotInReview()
    {
        _disk.CreateDirectory($"{Root}/docs/plans");
        _disk.WriteAllText(
            $"{Root}/docs/plans/2026-01-01-widget.md",
            "# Widget\n\n**Status: Draft.**\n\n## Notes\n\n"
            + "A plan becomes **Status: Blocked** when an external decision is missing.\n");

        IReadOnlyList<string> plans = new PlanTaskService(_store, _paths, _environment).ReviewPlans("repo", Config);

        Assert.Empty(plans);
    }
}

using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>The task prompt's repository root: a relative one is resolved against the injected current directory.</summary>
public sealed class TaskPromptRootOverSeamsTests
{
    private const string Current = "/kw/work";

    [Fact]
    public void ARelativeRepoRoot_AsksForThePlansOfTheRootUnderTheInjectedCurrentDirectory()
    {
        // Two plans are active for the resolved root, so the refusal names both: only that root was asked.
        TaskPromptResult inside = Build(
            "repo",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["/kw/work/repo"] = ["docs/plans/a.md", "docs/plans/b.md"],
            });
        TaskPromptResult outside = Build(
            "repo",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["/kw/elsewhere/repo"] = ["docs/plans/a.md", "docs/plans/b.md"],
            });

        Assert.Contains("2 plans are Ready or In progress", inside.Refusal, StringComparison.Ordinal);
        Assert.Contains("no plan is Ready or In progress", outside.Refusal, StringComparison.Ordinal);
    }

    private static TaskPromptResult Build(string root, Dictionary<string, IReadOnlyList<string>> plans)
    {
        TaskPromptService service = new(
            new FakePlanTaskService { PlansByRoot = plans },
            new NoHostFacts(),
            new InMemoryFileSystem(),
            _ => null,
            new FakeProcessEnvironment(currentDirectory: Current),
            FixedKwSkillCatalog.Empty);
        return service.Build(new TaskPromptRequest(root, TaskId: "T1"), new KyberWeaveConfig { Arbiter = ArbiterConfigDefaults.Product });
    }

    private sealed class NoHostFacts : IPacketHostFacts
    {
        public string Render(
            string repoRoot,
            KyberWeaveConfig config,
            string? role,
            string? taskId,
            IList<string>? diagnostics = null,
            string? technology = null) => throw new NotSupportedException("The prompt is refused before host facts are read.");
    }
}

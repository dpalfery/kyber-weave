using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// A developer tier is a difficulty, not a technology: <c>dev-easy</c>, <c>dev-medium</c> and <c>dev-hard</c>
/// all resolve a react task's gates through <c>react-dev</c>, through the task's technology rather than their
/// own name. A review rework re-runs the task gate while the plan sits in review — not Ready or In progress —
/// so the task's technology and files must resolve then too.
/// </summary>
public sealed class TaskGateTierResolutionTests : IDisposable
{
    private const string Config = """
        ontology:
          docs-root: 6-Docs
          technologies: [react]

        review:
          task-gates:
            react-dev:
              - id: dashboard-lint
                run: [echo, lint]
              - id: dashboard-typecheck
                run: [echo, typecheck]
              - id: dashboard-build
                run: [echo, build]
              - id: dashboard-e2e
                run: [echo, e2e]
        """;

    private readonly InMemoryReviewServices _services = new();
    private readonly string _repo;

    public TaskGateTierResolutionTests()
    {
        _repo = Path.Combine(_services.Root, "repo");
        _services.Files.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        _services.Files.CreateDirectory(Path.Combine(_repo, "6-Docs", "plans"));
        _services.Files.CreateDirectory(Path.Combine(_repo, "src"));
        _services.Files.WriteAllText(Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"), Config);
        WritePlan(_services, "**Status: Ready.**");

        // The task file as the worker's checkout holds it, written the way `docs tasks --write` writes it.
        _services.Get<IPlanTaskService>().Run(
            _repo,
            _services.Get<IKyberWeaveConfigRepository>().Load(_repo),
            PlanTaskFileProbe.PlanPath,
            PlanTaskFileMode.Write);

        // The plan has moved into review: the state a review rework re-runs its task gate in.
        WritePlan(_services, "**Status: Review required.**");
        _services.Processes.Script(
            "git", new ProcessRunResult(0, string.Empty, TimedOut: false), "worktree", "list", "--porcelain");
        _services.ScriptProgram("echo", TimeSpan.Zero);
    }

    /// <summary>Releases the services.</summary>
    public void Dispose()
    {
        _services.Dispose();
    }

    [Theory]
    [InlineData("dev-easy")]
    [InlineData("dev-medium")]
    [InlineData("dev-hard")]
    public void EveryDeveloperTierRunsTheTasksTechnologyGatesWhileThePlanIsInReview(string agent)
    {
        ReviewCommandResult result = Gates(agent);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Epilogue);
        Assert.Contains(result.Epilogue, line => line.Contains($"task: T2 from {PlanTaskFileProbe.PlanPath}", StringComparison.Ordinal));
        Assert.Contains(result.Epilogue, line => line.Contains($"TASK_GATE: PASS — agent={agent} gates=4", StringComparison.Ordinal));
    }

    /// <summary>Runs the agent-gate path the CLI's <c>review gates --agent ... --task ...</c> hands its arguments to.</summary>
    private ReviewCommandResult Gates(string agent) =>
        _services.ReviewGates.Run(
            new ReviewGatesRequest(
                _repo, null, null, agent, null, [], [], All: false, Deadline: false, Council: false,
                StopOnFailure: false, Full: false, TaskSlug: "T2", Fix: false, Red: false,
                HumanOutput: true, Interactive: false),
            new FakeProcessExecutor(),
            TextWriter.Null);

    /// <summary>Writes the fixture plan as a react plan carrying <paramref name="status"/> as its body status.</summary>
    private static void WritePlan(InMemoryReviewServices services, string status)
    {
        string plan = PlanTaskFileProbe.PlanMarkdown()
            .Replace("**Technology:** csharp", "**Technology:** react", StringComparison.Ordinal)
            .Replace("`csharp-dev`", "`dev-medium`", StringComparison.Ordinal)
            .Replace("`test-dev`", "`dev-medium`", StringComparison.Ordinal)
            .Replace("**Status: Ready.**", status, StringComparison.Ordinal);

        string path = Path.Combine(
            services.Root, "repo", PlanTaskFileProbe.PlanPath.Replace('/', Path.DirectorySeparatorChar));
        services.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        services.Files.WriteAllText(path, plan);
    }
}

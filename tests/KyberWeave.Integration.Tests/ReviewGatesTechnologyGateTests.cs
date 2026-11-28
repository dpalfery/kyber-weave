using System.Diagnostics;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// <c>review gates . --agent &lt;tier&gt; --task &lt;id&gt;</c> resolves a task's gates by the task's technology, the way
/// <c>docs packet</c> does, so a host that declares only <c>review.technology-gates</c> still runs the task's change.
/// </summary>
public sealed class ReviewGatesTechnologyGateTests : IAsyncLifetime
{
    private const string NoGatesForAgent = "KW-REVIEW-027";

    private DocFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await ReadyAsync();

    public ValueTask DisposeAsync()
    {
        _fixture.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static async Task<DocFixture> ReadyAsync(string? planMarkdown = null)
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus(planMarkdown);
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        _ = await DocsTestCommands.Tasks().ExecuteAsync(
            null!,
            new DocsTasksSettings { Path = Path.Combine(fixture.Root, PlanTaskFileProbe.PlanPath), Format = "json", Write = true },
            CancellationToken.None);
        return fixture;
    }

    /// <summary>Writes the host's configuration, whose <c>review:</c> body is <paramref name="reviewBody"/>.</summary>
    private void Configure(string reviewBody) =>
        _fixture.Write(".kyber-weave/kyber-weave.yml", "review:\n" + reviewBody);

    /// <summary>One entry under a gate map, naming a probe that leaves <paramref name="marker"/> behind when it runs.</summary>
    private static string Entry(string key, string id, string marker) =>
        $"    {key}:\n" +
        $"      - id: {id}\n" +
        "        include: [\"**/*.cs\"]\n" +
        $"        run: [sh, -c, 'printf ran > {marker}']\n";

    private (int Exit, string Output) Run(DocFixture fixture, string agent, params string[] options)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(
                ["review", "gates", fixture.Root, "--agent", agent, "--task", "T1", .. options]));
        return (execution.Result, execution.Output);
    }

    private static bool Ran(DocFixture fixture, string marker) => File.Exists(Path.Combine(fixture.Root, marker));

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("The probe gate is a shell script.");
    }

    /// <summary>The fixture plan with its first task naming no technology, as a documentation task does.</summary>
    private static Task<DocFixture> WithoutTechnologyAsync()
    {
        const string Technology = "- **Technology:** csharp";
        string plan = PlanTaskFileProbe.PlanMarkdown();
        int at = plan.IndexOf(Technology, StringComparison.Ordinal);
        Assert.True(at >= 0, "the fixture plan should name the first task's technology");
        return ReadyAsync(plan[..at] + "- **Capabilities:** documentation and prose" + plan[(at + Technology.Length)..]);
    }

    [Fact]
    public void ATechnologyKeyedGateRunsForATaskWhoseTechnologyThePlanNames()
    {
        SkipOnWindows();
        Configure("  technology-gates:\n" + Entry("csharp", "tech-probe", "tech-ran.txt"));

        (int exit, string output) = Run(_fixture, "dev-easy");

        Assert.True(exit == 0, output);
        Assert.DoesNotContain(NoGatesForAgent, output, StringComparison.Ordinal);
        Assert.True(Ran(_fixture, "tech-ran.txt"), output);
    }

    [Fact]
    public void AnAgentKeyedGateStillRunsForAnOldHost()
    {
        SkipOnWindows();
        Configure("  task-gates:\n" + Entry("dev-medium", "agent-probe", "agent-ran.txt"));

        (int exit, string output) = Run(_fixture, "dev-medium");

        Assert.True(exit == 0, output);
        Assert.DoesNotContain(NoGatesForAgent, output, StringComparison.Ordinal);
        Assert.True(Ran(_fixture, "agent-ran.txt"), output);
    }

    [Fact]
    public void TheAgentsOwnEntryWinsWhenBothAreDeclared()
    {
        SkipOnWindows();
        Configure(
            "  task-gates:\n" + Entry("dev-medium", "agent-probe", "agent-ran.txt")
            + "  technology-gates:\n" + Entry("csharp", "tech-probe", "tech-ran.txt"));

        (int exit, string output) = Run(_fixture, "dev-medium");

        Assert.True(exit == 0, output);
        Assert.True(Ran(_fixture, "agent-ran.txt"), output);
        Assert.False(Ran(_fixture, "tech-ran.txt"), output);
    }

    [Fact]
    public async Task ATechnologyWithNoEntryAnywhereReportsTheWarningAndNamesTechnologyGates()
    {
        SkipOnWindows();
        using DocFixture fixture = await WithoutTechnologyAsync();
        ConfigureWith(fixture, "  technology-gates:\n" + Entry("csharp", "tech-probe", "tech-ran.txt"));

        (int exit, string output) = Run(fixture, "dev-easy");

        Assert.True(exit == 0, output);
        Assert.Contains(NoGatesForAgent, output, StringComparison.Ordinal);
        Assert.Contains("review.technology-gates", output, StringComparison.Ordinal);
        Assert.False(Ran(fixture, "tech-ran.txt"), output);
    }

    [Fact]
    public async Task ATaskWithNoTechnologyLineResolvesItsAgentsOwnEntry()
    {
        SkipOnWindows();
        using DocFixture fixture = await WithoutTechnologyAsync();
        ConfigureWith(fixture, "  task-gates:\n" + Entry("dev-medium", "agent-probe", "agent-ran.txt"));

        (int exit, string output) = Run(fixture, "dev-medium");

        Assert.True(exit == 0, output);
        Assert.DoesNotContain(NoGatesForAgent, output, StringComparison.Ordinal);
        Assert.True(Ran(fixture, "agent-ran.txt"), output);
    }

    [Fact]
    public void ATaskWhoseTechnologyHasNoEntryAnywhereIsRefusedWithTheTechnologyNamed()
    {
        SkipOnWindows();
        Configure("  technology-gates:\n" + Entry("react", "tech-probe", "tech-ran.txt"));

        (int exit, string output) = Run(_fixture, "dev-easy");

        Assert.Equal(1, exit);
        Assert.Contains(NoGatesForAgent, output, StringComparison.Ordinal);
        Assert.Contains("review.technology-gates.csharp", output, StringComparison.Ordinal);
        Assert.False(Ran(_fixture, "tech-ran.txt"), output);
    }

    private static void ConfigureWith(DocFixture fixture, string reviewBody) =>
        fixture.Write(".kyber-weave/kyber-weave.yml", "review:\n" + reviewBody);

    /// <summary>
    /// The run's plan and task file live in the conductor's worktree, while a worker runs its gate in a worktree of
    /// its own; <c>docs packet</c> finds the task there, so the gate resolves the task's technology the same way.
    /// </summary>
    [Fact]
    public async Task APlanInAnotherWorktreeStillResolvesTheTasksTechnologyGates()
    {
        SkipOnWindows();
        using TempDirectory other = new();
        DocFixture host = _fixture;
        ConfigureWith(host, "  technology-gates:\n" + Entry("csharp", "tech-probe", "tech-ran.txt"));

        await Git(host.Root, "init", "-q");
        await Git(host.Root, "add", "-A");
        await Git(host.Root, "-c", "user.email=probe@example.com", "-c", "user.name=Probe", "commit", "-qm", "fixture");
        await Git(host.Root, "worktree", "add", "-q", Path.Combine(other.Path, "run"), "-b", "probe/run");

        // The run's worktree keeps the plan and its task file; the worker's checkout does not have them, which is
        // the case `docs packet` already handles by reading the plan from the other worktree.
        File.Delete(Path.Combine(host.Root, PlanTaskFileProbe.PlanPath));
        File.Delete(Path.Combine(host.Root, PlanTaskFileProbe.TaskFilePath));

        (int exit, string output) = Run(host, "dev-easy");

        Assert.True(exit == 0, output);
        Assert.DoesNotContain(NoGatesForAgent, output, StringComparison.Ordinal);
        Assert.True(Ran(host, "tech-ran.txt"), output);
    }

    private static async Task Git(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        ProcessResult result = await ProcessRunner.RunAsync(start, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}{result.StandardOutput}");
    }
}

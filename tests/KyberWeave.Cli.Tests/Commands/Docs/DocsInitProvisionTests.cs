using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Cli.Tests.Commands.Docs;

/// <summary>
/// Pins D12: <c>docs init</c> without <c>--provision</c> is pure scaffolding and starts no index, so CI's scaffold
/// step is unchanged. The provisioning path runs the injected doctor over in-memory seams; the real doctor against a
/// real disk is <c>DocsInitProvisionEndToEndTests</c> in the integration project.
/// </summary>
public sealed class DocsInitProvisionTests
{
    private readonly MemoryDocsHost _host = new(new Dictionary<string, string?> { ["PATH"] = "/stub/bin" });

    private IServiceProvider Services => _host.Services();

    /// <summary>
    /// <c>docs init</c> without <c>--provision</c> scaffolds and creates no CodeGraph index. CI runs this as pure
    /// scaffolding today, so a default that started indexing would slow every CI run and could spawn a server.
    /// </summary>
    /// <remarks>
    /// The no-spawn claim is observed through the filesystem: a provisioning run would create <c>.codegraph/</c>.
    /// Calling a fake executor here would need a seam <c>DocsInitCommand</c> does not expose.
    /// </remarks>
    [Fact]
    public void DocsInit_WithoutProvision_SpawnsNothing()
    {
        int exit = CommandAppFixture
            .DocsInit(_host.Register, _host.Console)
            .Run(["docs", "init", _host.Repo, "--no-skill", "--no-review-gates"]);

        Assert.True(exit == 0, $"docs init exited {exit}. Output:{Environment.NewLine}{_host.Output}");
        Assert.False(
            _host.Files.DirectoryExists(Path.Combine(_host.Repo, ".codegraph")),
            "docs init without --provision must not provision a CodeGraph index.");
        Assert.DoesNotContain(_host.Processes.Calls, call => call.Program == "codegraph");
    }

    /// <summary>
    /// <c>docs init --provision</c> runs the injected doctor in provision mode against the scaffolded root, so the
    /// command can be proven without a child process or a real index.
    /// </summary>
    [Fact]
    public async Task DocsInit_WithProvision_UsesTheInjectedDoctor()
    {
        FakeRepositoryDoctor doctor = new();
        DocsInitCommand command = DocsTestCommands.Init(doctor, _host.Console, Services);
        DocsInitSettings settings = new()
        {
            Path = _host.Repo,
            NoSkill = true,
            NoReviewGates = true,
            Provision = true,
        };

        int exit = await command.ExecuteAsync(null!, settings, CancellationToken.None);

        Assert.True(exit == 0, $"docs init --provision exited {exit}. Output:{Environment.NewLine}{_host.Output}");
        DoctorRun run = Assert.Single(doctor.Runs);
        Assert.Equal(_host.Repo, run.Root);
        Assert.Equal(DoctorMode.Provision, run.Mode);
    }

    /// <summary>
    /// The command passes the provision run's options, executor and environment to the doctor, so a change to one of
    /// them is visible here rather than only in a real run.
    /// </summary>
    [Fact]
    public async Task DocsInit_WithProvision_PassesTheProvisionArgumentsToTheDoctor()
    {
        ScriptedProcessExecutor executor = new(_ => DoctorHarness.Ok());
        FakeRepositoryDoctor doctor = new();
        DocsInitCommand command = DocsTestCommands.Init(doctor, _host.Console, Services, executor: executor, mcpExecutable: "kyber-weave-mcp-stub");
        DocsInitSettings settings = new()
        {
            Path = _host.Repo,
            NoSkill = true,
            NoReviewGates = true,
            Provision = true,
        };

        _ = await command.ExecuteAsync(null!, settings, CancellationToken.None);

        DoctorRun run = Assert.Single(doctor.Runs);
        Assert.Equal(TimeSpan.FromMinutes(10), run.Options.IndexTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), run.Options.McpTimeout);
        Assert.Equal("codegraph", run.Options.CodeGraphExecutable);
        Assert.Equal("kyber-weave-mcp-stub", run.Options.McpExecutable);
        Assert.Same(executor, run.Executor);
        Assert.Equal("/stub/bin", run.Environment("PATH"));
        Assert.Null(run.LockDirectory);
    }

    /// <summary>The command returns the doctor's exit code, so a failed provision is not reported as success.</summary>
    [Fact]
    public async Task DocsInit_WithProvision_ReturnsTheDoctorsExitCode()
    {
        FakeRepositoryDoctor doctor = new()
        {
            Report = new DoctorReport(
                ".",
                DoctorMode.Provision,
                3,
                [new DoctorCheck("codegraph.binary", false, "codegraph is missing", "install codegraph")]),
        };
        Spectre.Console.Testing.TestConsole console = new();
        DocsInitCommand command = DocsTestCommands.Init(doctor, console, Services);
        DocsInitSettings settings = new()
        {
            Path = _host.Repo,
            NoSkill = true,
            NoReviewGates = true,
            Provision = true,
        };

        int exit = await command.ExecuteAsync(null!, settings, CancellationToken.None);

        Assert.Equal(3, exit);
        Assert.Contains("codegraph.binary", console.Output, StringComparison.Ordinal);
    }
}

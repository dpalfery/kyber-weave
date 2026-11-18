using KyberWeave.Application;
using KyberWeave.Cli.Composition;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Mcp;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Xunit;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Cli;

namespace KyberWeave.Tests;

/// <summary>
/// Pins D12: <c>docs init</c> without <c>--provision</c> is pure scaffolding and starts no index, so CI's scaffold
/// step is unchanged. The provisioning path, which delegates to <c>IRepositoryDoctor</c> for the target's git root, is
/// skipped here because <c>DocsInitCommand</c> has no executor seam in the contract.
/// </summary>
public sealed class DocsInitProvisionTests : IDisposable
{
    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

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
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            DocsApp().Run(["docs", "init", _repo.Path, "--no-skill", "--no-review-gates"]));

        Assert.True(
            execution.Result == 0,
            $"docs init exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        Assert.False(
            Directory.Exists(Path.Combine(_repo.Path, ".codegraph")),
            "docs init without --provision must not provision a CodeGraph index.");
    }

    /// <summary>
    /// <c>docs init --provision</c> runs the injected doctor in provision mode against the scaffolded root, so the
    /// command can be proven without a child process or a real index.
    /// </summary>
    [Fact]
    public void DocsInit_WithProvision_UsesTheInjectedDoctor()
    {
        FakeRepositoryDoctor doctor = new();
        DocsInitCommand command = new(McpWiringTestServices.Wiring, DocsTestCommands.Scaffolding, doctor);
        DocsInitSettings settings = new()
        {
            Path = _repo.Path,
            NoSkill = true,
            NoReviewGates = true,
            Provision = true,
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            command.Execute(null!, settings, CancellationToken.None));

        Assert.True(
            execution.Result == 0,
            $"docs init --provision exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        (string root, DoctorMode mode) = Assert.Single(doctor.Runs);
        Assert.Equal(Path.GetFullPath(_repo.Path), root);
        Assert.Equal(DoctorMode.Provision, mode);
    }

    /// <summary>
    /// <c>docs init --provision</c> runs <c>IRepositoryDoctor</c> in provision mode against the scaffolded root, or the
    /// target's git root with <c>--target</c>.
    /// </summary>
    /// <remarks>
    /// Skipped, not weakened. The provisioning path would start the real <c>codegraph</c> binary from a unit test,
    /// and the contract gives <c>DocsInitCommand</c> no executor seam to fake it. It needs that seam (D12) before it
    /// can run hermetically.
    /// </remarks>
    [Fact]
    public void DocsInit_WithProvision_UsesRepositoryDoctor_TargetRoot()
    {
        using TempDirectory stubs = new();
        string repo = _repo.Path;
        ScriptedProcessExecutor executor = DoctorHarness.Executor(repo);
        DocsInitCommand command = new(McpWiringTestServices.Wiring, DocsTestCommands.Scaffolding, DoctorTestServices.Doctor)
        {
            Executor = executor,
            McpExecutable = DoctorHarness.HealthyStub(stubs.Path, repo),
        };
        DocsInitSettings settings = new()
        {
            Path = repo,
            NoSkill = true,
            NoReviewGates = true,
            Provision = true,
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            command.Execute(null!, settings, CancellationToken.None));

        Assert.True(
            execution.Result == 0,
            $"docs init --provision exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        Assert.True(
            File.Exists(Path.Combine(repo, ".kyber-weave", "kyber-weave.yml")),
            "The scaffold must run before the provisioning doctor.");
        ProcessStartInfo init = Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
        Assert.Equal(repo, init.WorkingDirectory);
        Assert.Contains(
            executor.CallsTo("git"),
            call => call.WorkingDirectory == repo);
    }

    private static CommandApp DocsApp()
    {
        ServiceCollection services = [];
        services.AddSingleton<IMcpDeclarationReader, McpDeclarationReader>();
        _ = services.AddWeaveApplication();
        CommandApp app = new(new ServiceCollectionTypeRegistrar(services));
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.AddBranch("docs", docs => { docs.AddCommand<DocsInitCommand>("init"); });
        });
        return app;
    }
}

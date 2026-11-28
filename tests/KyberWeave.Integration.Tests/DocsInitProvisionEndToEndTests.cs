using System.Diagnostics;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// <c>docs init --provision</c> end to end: the real doctor over the real disk, with the MCP probe started as a shell
/// stub. The unit coverage of the same command is <c>DocsInitProvisionTests</c> in the Cli unit project.
/// </summary>
public sealed class DocsInitProvisionEndToEndTests : IDisposable
{
    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

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
    public async Task DocsInit_WithProvision_UsesRepositoryDoctor_TargetRoot()
    {
        using TempDirectory stubs = new();
        string repo = _repo.Path;
        ScriptedProcessExecutor executor = DoctorHarness.Executor(repo);
        DocsInitCommand command = DocsTestCommands.Init(DoctorTestServices.Doctor, executor: executor, mcpExecutable: DoctorHarness.HealthyStub(stubs.Path, repo));
        DocsInitSettings settings = new()
        {
            Path = repo,
            NoSkill = true,
            NoReviewGates = true,
            Provision = true,
        };

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(() =>
            command.ExecuteAsync(null!, settings, CancellationToken.None));

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
}

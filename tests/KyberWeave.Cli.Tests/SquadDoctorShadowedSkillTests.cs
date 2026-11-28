using KyberWeave.Testing.Squad;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Application.Arbiter;

namespace KyberWeave.Tests;

/// <summary>
/// <c>squad doctor</c> warns when a skill folder the repository keeps for itself repeats the name of
/// an installed squad skill with different content, so a worker is not left reading a stale copy.
/// </summary>
public sealed class SquadDoctorShadowedSkillTests
{
    private const string Installed = "---\nname: task-gate\n---\nRun the current command.\n";

    private readonly InMemorySquadState _state = new();

    [Fact]
    public async Task AProjectCopyWithDifferentContentIsAWarningNamingBothPathsAndTheFix()
    {
        string workingDir = Deploy(".agents/skills/task-gate/SKILL.md", "---\nname: task-gate\n---\nRun the removed command.\n");

        (int exitCode, string output) = await RunDoctor(workingDir);

        Assert.Equal(0, exitCode);
        Assert.Contains("KW-SQUAD-SHADOW-001", output, StringComparison.Ordinal);
        Assert.Contains(Normalize(Path.Combine(workingDir, ".agents", "skills", "task-gate", "SKILL.md")), output, StringComparison.Ordinal);
        Assert.Contains(Normalize(Path.Combine(workingDir, ".claude", "skills", "task-gate", "SKILL.md")), output, StringComparison.Ordinal);
        Assert.Contains("Delete the project copy", output, StringComparison.Ordinal);
        Assert.Contains("squad install", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectCopyWithTheSameBodyUnderAnotherHarnessFrontmatterIsNotReported()
    {
        string workingDir = Deploy(".github/skills/task-gate/SKILL.md", "---\nname: task-gate\nextra: other-harness\n---\r\nRun the current command.\r\n");

        (int exitCode, string output) = await RunDoctor(workingDir);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("KW-SQUAD-SHADOW-001", output, StringComparison.Ordinal);
        Assert.Contains("shadowing the squad: none", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASkillTheSquadNeverInstalledIsLeftAlone()
    {
        string workingDir = Deploy(".agents/skills/our-own-skill/SKILL.md", "---\nname: our-own-skill\n---\nOurs.\n");

        (_, string output) = await RunDoctor(workingDir);

        Assert.DoesNotContain("KW-SQUAD-SHADOW-001", output, StringComparison.Ordinal);
    }

    /// <summary>Installs the squad skill under <c>.claude</c> through a receipt, then writes one unmanaged project file.</summary>
    private string Deploy(string projectRelativePath, string projectContent)
    {
        string workingDir = _state.CreateRoot("project");
        string installedPath = Path.Combine(workingDir, ".claude", "skills", "task-gate", "SKILL.md");
        _state.Files.CreateDirectory(Path.GetDirectoryName(installedPath)!);
        byte[] installedBytes = Encoding.UTF8.GetBytes(Installed);
        _state.Files.WriteAllBytes(installedPath, installedBytes);
        SeedReceipt(workingDir, ".claude/skills/task-gate/SKILL.md", installedBytes);

        string projectPath = Path.Combine(workingDir, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        _state.Files.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        _state.Files.WriteAllText(projectPath, projectContent);
        return workingDir;
    }

    private async Task<(int ExitCode, string Output)> RunDoctor(string workingDir)
    {
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(
            _state,
            executor,
            workingDirectory: workingDir);

        CapturedConsoleExecution<int> run = await ProcessConsoleCapture.RunAsync(() => command.ExecuteAsync(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false },
            CancellationToken.None));
        return (run.Result, Normalize(run.Output));
    }

    private void SeedReceipt(string targetRoot, string relativePath, byte[] content)
    {
        SquadStateStore stateStore = _state.Store;
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project);
        _state.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        SquadReceipt receipt = new(
            Schema: "kyber-squad.receipt/v1",
            Scope: SquadDeploymentScope.Project,
            TargetRoot: ".",
            InstalledAtUtc: DateTimeOffset.UtcNow,
            Degradations: [],
            Files: [new SquadOwnedFile(relativePath, Convert.ToHexStringLower(SHA256.HashData(content)), "claude", Adopted: false)]);
        _state.Files.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt));
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

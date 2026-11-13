using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>squad doctor</c> warns when a skill folder the repository keeps for itself repeats the name of
/// an installed squad skill with different content, so a worker is not left reading a stale copy.
/// </summary>
public sealed class SquadDoctorShadowedSkillTests : IDisposable
{
    private const string Installed = "---\nname: resharper-clt\n---\nRun the current command.\n";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AProjectCopyWithDifferentContentIsAWarningNamingBothPathsAndTheFix()
    {
        string workingDir = Deploy(".agents/skills/resharper-clt/SKILL.md", "---\nname: resharper-clt\n---\nRun the removed command.\n");

        (int exitCode, string output) = RunDoctor(workingDir);

        Assert.Equal(0, exitCode);
        Assert.Contains("KW-SQUAD-SHADOW-001", output, StringComparison.Ordinal);
        Assert.Contains(Normalize(Path.Combine(workingDir, ".agents", "skills", "resharper-clt", "SKILL.md")), output, StringComparison.Ordinal);
        Assert.Contains(Normalize(Path.Combine(workingDir, ".claude", "skills", "resharper-clt", "SKILL.md")), output, StringComparison.Ordinal);
        Assert.Contains("Delete the project copy", output, StringComparison.Ordinal);
        Assert.Contains("squad install", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectCopyWithTheSameBodyUnderAnotherHarnessFrontmatterIsNotReported()
    {
        string workingDir = Deploy(".github/skills/resharper-clt/SKILL.md", "---\nname: resharper-clt\nextra: other-harness\n---\r\nRun the current command.\r\n");

        (int exitCode, string output) = RunDoctor(workingDir);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("KW-SQUAD-SHADOW-001", output, StringComparison.Ordinal);
        Assert.Contains("shadowing the squad: none", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASkillTheSquadNeverInstalledIsLeftAlone()
    {
        string workingDir = Deploy(".agents/skills/our-own-skill/SKILL.md", "---\nname: our-own-skill\n---\nOurs.\n");

        (_, string output) = RunDoctor(workingDir);

        Assert.DoesNotContain("KW-SQUAD-SHADOW-001", output, StringComparison.Ordinal);
    }

    /// <summary>Installs the squad skill under <c>.claude</c> through a receipt, then writes one unmanaged project file.</summary>
    private string Deploy(string projectRelativePath, string projectContent)
    {
        string workingDir = Path.Combine(_temp.Path, "project");
        string installedPath = Path.Combine(workingDir, ".claude", "skills", "resharper-clt", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
        byte[] installedBytes = Encoding.UTF8.GetBytes(Installed);
        File.WriteAllBytes(installedPath, installedBytes);
        SeedReceipt(workingDir, ".claude/skills/resharper-clt/SKILL.md", installedBytes);

        string projectPath = Path.Combine(workingDir, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, projectContent);
        return workingDir;
    }

    private (int ExitCode, string Output) RunDoctor(string workingDir)
    {
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = new(
            executor,
            new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home")),
            workingDirectory: workingDir);

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));
        return (run.Result, Normalize(run.Output));
    }

    private void SeedReceipt(string targetRoot, string relativePath, byte[] content)
    {
        SquadStateStore stateStore = new(new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home")));
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        SquadReceipt receipt = new(
            Schema: "kyber-squad.receipt/v1",
            Scope: SquadDeploymentScope.Project,
            TargetRoot: ".",
            InstalledAtUtc: DateTimeOffset.UtcNow,
            Degradations: [],
            Files: [new SquadOwnedFile(relativePath, Convert.ToHexStringLower(SHA256.HashData(content)), "claude", Adopted: false)]);
        File.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt), Encoding.UTF8);
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T14 RED: <c>squad doctor</c> warns about the retired <c>second-brain</c> skill, whether
/// it survives as an unmanaged directory in a deployed target tree or as a retained entry
/// in a deployment receipt. The warning prints the stale paths, the delete-then-
/// <c>squad update</c> cleanup steps, and the new Kyber-Docs home via <c>docs init</c>.
/// Both facts fail on the phase-1 tree until T15-T17 lands.
/// </summary>
public sealed class SquadDoctorRetiredSkillTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A retired <c>second-brain</c> directory sitting in a deployed target tree without
    /// receipt cover is unmanaged: doctor warns, prints its path, the delete-then-update
    /// steps, and the new Kyber-Docs home. A warning, not a failure: the stale copy works.
    /// </summary>
    [Fact]
    public void Doctor_ProjectWorkspaceWithUnmanagedRetiredSkillDir_WarnsWithPathsAndCleanupSteps()
    {
        string workingDir = Path.Combine(_temp.Path, "doctor-retired-unmanaged");
        string staleDir = Path.Combine(workingDir, ".claude", "skills", "second-brain");
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(
            Path.Combine(staleDir, "SKILL.md"), "---\nname: second-brain\n---\nStale.\n");

        CommandExecution execution = RunProjectDoctor(workingDir);
        string normalizedOutput = Normalize(execution.Output);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("warn", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("second-brain", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains(Normalize(staleDir), normalizedOutput, StringComparison.Ordinal);
        Assert.Contains("delete", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("squad update", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs init", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kyber-docs-", normalizedOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deployment receipt that still claims the retired <c>second-brain</c> files is
    /// retained state: doctor warns, prints the stale paths, the delete-then-update steps,
    /// and the new Kyber-Docs home.
    /// </summary>
    [Fact]
    public void Doctor_ProjectWorkspaceWithRetainedRetiredReceiptEntry_WarnsAndNamesNewHome()
    {
        string workingDir = Path.Combine(_temp.Path, "doctor-retired-retained");
        string staleDir = Path.Combine(workingDir, ".claude", "skills", "second-brain");
        Directory.CreateDirectory(staleDir);
        byte[] staleBytes = Encoding.UTF8.GetBytes("---\nname: second-brain\n---\nStale.\n");
        File.WriteAllBytes(Path.Combine(staleDir, "SKILL.md"), staleBytes);
        SeedProjectReceipt(workingDir, ".claude/skills/second-brain/SKILL.md", staleBytes);

        CommandExecution execution = RunProjectDoctor(workingDir);
        string normalizedOutput = Normalize(execution.Output);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("warn", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("second-brain", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains(Normalize(staleDir), normalizedOutput, StringComparison.Ordinal);
        Assert.Contains("delete", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("squad update", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs init", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kyber-docs-", normalizedOutput, StringComparison.Ordinal);
    }

    private CommandExecution RunProjectDoctor(string workingDir)
    {
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new SquadDoctorCommand(
            executor,
            userPaths,
            workingDirectory: workingDir);

        return Capture(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = workingDir,
                Global = false
            }));
    }

    private void SeedProjectReceipt(string targetRoot, string relativePath, byte[] content)
    {
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new SquadStateStore(userPaths);
        string receiptPath = stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project);
        string lockPath = stateStore.ResolveLockPath(targetRoot, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        string sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        SquadReceipt receipt = new SquadReceipt(
            Schema: "kyber-squad.receipt/v1",
            Scope: SquadDeploymentScope.Project,
            TargetRoot: ".",
            InstalledAtUtc: DateTimeOffset.UtcNow,
            Degradations: [],
            Files: [new SquadOwnedFile(relativePath, sha256, "claude", Adopted: false)]);
        SquadLock squadLock = new SquadLock(
            Schema: "kyber-squad.lock/v1",
            SquadVersion: "1.2.3",
            CliVersion: "1.2.3",
            McpVersion: "1.2.3",
            Bundle: "full",
            Targets: ["claude"],
            Exclusions: [],
            Translation: "best-effort",
            BundleDigest: "a".PadRight(64, '0'),
            AssetDigest: "b".PadRight(64, '0'),
            Apm: SquadApmIdentity.None);

        File.WriteAllText(receiptPath, stateStore.SerializeReceipt(receipt), Encoding.UTF8);
        File.WriteAllText(lockPath, stateStore.SerializeLock(squadLock), Encoding.UTF8);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record CommandExecution(int ExitCode, string Output);
}

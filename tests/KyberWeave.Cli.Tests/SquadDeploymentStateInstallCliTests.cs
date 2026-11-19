using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The uninstall command's dry run over an in-memory deployment: its report is the command's own output, so the case
/// lives with the CLI tests even though its subject is the deployment receipt.
/// </summary>
public sealed class SquadDeploymentStateInstallCliTests
{
    /// <summary>
    ///     An uninstall plan's receipt carries only retained files; the dry-run report must list
    ///     the planned removals with their target, and remove nothing.
    /// </summary>
    [Fact]
    public void UninstallDryRunListsPlannedRemovalsWithTargetsAndRemovesNothing()
    {
        InMemorySquadState state = new();
        string root = Path.Combine(state.Files.Root, "dry-run-project");
        state.Files.CreateDirectory(root);
        const string relativePath = ".codex/agents/conductor.toml";
        string agentPath = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        state.Files.CreateDirectory(Path.GetDirectoryName(agentPath)!);
        state.Files.WriteAllText(agentPath, "installed body");
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            new DateTimeOffset(2026, 8, 14, 12, 34, 56, TimeSpan.Zero),
            [],
            [SquadStateTestLayout.OwnedFile(relativePath, "installed body")]);
        string lockPath = state.Store.ResolveLockPath(root, SquadDeploymentScope.Project);
        string receiptPath = state.Store.ResolveReceiptPath(root, SquadDeploymentScope.Project);
        state.Files.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        state.Files.WriteAllText(lockPath, state.Store.SerializeLock(SquadStateTestLayout.Lock()));
        state.Files.WriteAllText(receiptPath, state.Store.SerializeReceipt(receipt));
        using FakeSquadReleaseSource releaseSource = FakeSquadReleaseSource.Failing(new NotImplementedException());
        SquadUninstallCommand command = SquadTestCommands.InMemoryUninstall(
            state,
            releaseSource,
            FakeSquadRenderer.Empty());

        CapturedConsoleExecution<int> dryRun = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                PathOption = root,
                DryRun = true
            }));

        Assert.Equal(0, dryRun.Result);
        Assert.Contains("would uninstall 1 files", dryRun.Output, StringComparison.Ordinal);
        Assert.Contains(relativePath, dryRun.Output, StringComparison.Ordinal);
        Assert.Contains("(codex)", dryRun.Output, StringComparison.Ordinal);
        Assert.True(state.Files.FileExists(agentPath));
    }
}

using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// When a command recovers an interrupted transaction and a later step then fails, the operator must still learn that
/// the recovery happened: the notice reaches the result on success and the failure message otherwise.
/// </summary>
public sealed class SquadLifecycleRecoveryNoticeTests
{
    private const string Notice = "Recovered an unfinished Squad transaction";

    private readonly InMemorySquadState _state = new();

    [Fact]
    public async Task AnInstallWhoseRenderFailsAfterRecoveringStillSaysAJournalledTransactionWasFinished()
    {
        (string root, FakeSquadReleaseSource release) = await InstalledAsync();
        await LeaveAnUpdateInterruptedAsync(root);

        SquadLifecycleService failing = SquadTestCommands.InMemoryLifecycle(
            _state,
            release,
            FakeSquadRenderer.Responding(_ => new SquadRenderResult(false, [], [], [], ["The render failed."])));

        SquadRenderValidationException refused = await Assert.ThrowsAsync<SquadRenderValidationException>(() =>
            failing.InstallAsync(new SquadInstallRequest(
                root,
                SquadDeploymentScope.Project,
                [SquadTarget.Codex],
                Version: "1.2.4")));

        Assert.Contains(Notice, refused.Message, StringComparison.Ordinal);
        Assert.Contains("The render failed.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpdateWhoseRenderFailsAfterRecoveringStillSaysAJournalledTransactionWasFinished()
    {
        (string root, FakeSquadReleaseSource release) = await InstalledAsync();
        await LeaveAnUpdateInterruptedAsync(root);

        SquadLifecycleService failing = SquadTestCommands.InMemoryLifecycle(
            _state,
            release,
            FakeSquadRenderer.Responding(_ => new SquadRenderResult(false, [], [], [], ["The render failed."])));

        SquadRenderValidationException refused = await Assert.ThrowsAsync<SquadRenderValidationException>(() =>
            failing.UpdateAsync(new SquadUpdateRequest(
                root,
                SquadDeploymentScope.Project,
                [SquadTarget.Codex],
                Version: "1.2.4")));

        Assert.Contains(Notice, refused.Message, StringComparison.Ordinal);
        Assert.Contains("The render failed.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureOfAFileKindAfterRecoveringKeepsItsTypeAndCarriesTheNoticeBesideIt()
    {
        (string root, FakeSquadReleaseSource release) = await InstalledAsync();
        await LeaveAnUpdateInterruptedAsync(root);
        IOException diskFull = new("The disk is full.");
        SquadLifecycleService failing = SquadTestCommands.InMemoryLifecycle(
            _state,
            release,
            FakeSquadRenderer.Responding(_ => throw diskFull));

        IOException refused = await Assert.ThrowsAsync<IOException>(() =>
            failing.UpdateAsync(new SquadUpdateRequest(
                root,
                SquadDeploymentScope.Project,
                [SquadTarget.Codex],
                Version: "1.2.4")));

        Assert.Same(diskFull, refused);
        Assert.Contains(
            "Recovered an unfinished Squad transaction",
            string.Join(" ", SquadLifecycleService.RecoveryNotices(refused)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADryRunOverAPendingJournalWarnsThatItChangedNothing()
    {
        (string root, FakeSquadReleaseSource release) = await InstalledAsync();
        await LeaveAnUpdateInterruptedAsync(root);
        string journal = Path.Combine(root, ".kyber-weave", ".squad-transaction");
        Assert.True(_state.Files.DirectoryExists(journal));
        SquadLifecycleService lifecycle = SquadTestCommands.InMemoryLifecycle(
            _state,
            release,
            FakeSquadRenderer.Responding(Rendered));

        SquadLifecycleResult dryRun = await lifecycle.UpdateAsync(new SquadUpdateRequest(
            root,
            SquadDeploymentScope.Project,
            [SquadTarget.Codex],
            Version: "1.2.4",
            DryRun: true));

        Assert.True(dryRun.Success, string.Join("; ", dryRun.Errors ?? []));
        Assert.Contains(
            dryRun.Warnings ?? [],
            warning => warning.Contains("unfinished Squad transaction", StringComparison.Ordinal) &&
                       warning.Contains("--dry-run", StringComparison.Ordinal));
        Assert.True(_state.Files.DirectoryExists(journal));
    }

    [Fact]
    public async Task AnUninstallDryRunOverAPendingJournalWarnsThatItChangedNothing()
    {
        (string root, FakeSquadReleaseSource release) = await InstalledAsync();
        await LeaveAnUpdateInterruptedAsync(root);
        string journal = Path.Combine(root, ".kyber-weave", ".squad-transaction");
        SquadLifecycleService lifecycle = SquadTestCommands.InMemoryLifecycle(
            _state,
            release,
            FakeSquadRenderer.Responding(Rendered));

        SquadLifecycleResult dryRun = await lifecycle.UninstallAsync(new SquadUninstallRequest(
            root,
            SquadDeploymentScope.Project,
            DryRun: true));

        Assert.True(dryRun.Success, string.Join("; ", dryRun.Errors ?? []));
        Assert.Contains(
            dryRun.Warnings ?? [],
            warning => warning.Contains("unfinished Squad transaction", StringComparison.Ordinal) &&
                       warning.Contains("--dry-run", StringComparison.Ordinal));
        Assert.True(_state.Files.DirectoryExists(journal));
    }

    private async Task<(string Root, FakeSquadReleaseSource Release)> InstalledAsync()
    {
        string root = _state.CreateRoot("recovery-notice");
        FakeSquadReleaseSource release = new() { Files = _state.Files };
        SquadLifecycleService lifecycle = SquadTestCommands.InMemoryLifecycle(
            _state,
            release,
            FakeSquadRenderer.Responding(Rendered));

        SquadLifecycleResult install = await lifecycle.InstallAsync(new SquadInstallRequest(
            root,
            SquadDeploymentScope.Project,
            [SquadTarget.Codex],
            Version: "1.2.3"));
        Assert.True(install.Success, string.Join("; ", install.Errors ?? []));
        return (root, release);
    }

    /// <summary>Kills an update part-way, as a killed process would, and leaves its journal for the next command.</summary>
    /// <param name="root">The installed root.</param>
    private async Task LeaveAnUpdateInterruptedAsync(string root)
    {
        FakeSquadReleaseSource release = new() { Files = _state.Files };
        SquadLifecycleService lifecycle = SquadTestCommands.InMemoryLifecycle(
            _state,
            release,
            FakeSquadRenderer.Responding(Rendered));
        int mutating = 0;
        _state.Files.KillWhen(operation =>
            InMemorySquadCrashSafetyFixture.IsMutating(operation) && ++mutating == 9);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => lifecycle.UpdateAsync(new SquadUpdateRequest(
                root,
                SquadDeploymentScope.Project,
                [SquadTarget.Codex],
                Version: "1.2.4")));
        }
        finally
        {
            _state.Files.ClearFaults();
        }
    }

    private static SquadRenderResult Rendered(SquadRenderRequest request) =>
        new(true, [.. request.Targets.Select(Conductor)], [], [], []);

    private static SquadDeploymentFile Conductor(SquadTarget target) =>
        new(
            $".{SquadTargetCatalog.GetToken(target)}/agents/conductor.md",
            Encoding.UTF8.GetBytes("conductor\n"),
            SquadTargetCatalog.GetToken(target));
}

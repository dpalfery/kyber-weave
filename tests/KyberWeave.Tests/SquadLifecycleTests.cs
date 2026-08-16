using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task K8 Test Suite: Pins install, update, and uninstall lifecycle orchestration across
/// project and global scopes, verifying lease management, preflight, rollback, exact-match adoption,
/// user-edit preservation, and isolated user paths.
/// Covers Test Contract K8 from docs/plans/2026-08-14-kyber-squad-unified-agent-skill-deployment.md.
/// </summary>
public sealed class SquadLifecycleTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    #region Install Lifecycle Contracts

    [Fact]
    public async Task Install_WhenCleanProject_AcquiresLeaseRendersProjectionsAndWritesReceipt()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "clean-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex, SquadTarget.Claude],
            Version: "1.2.3");

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Receipt);
        Assert.NotNull(result.Lock);

        // Verify rendered files exist in target root
        Assert.True(File.Exists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));
        Assert.True(File.Exists(Path.Combine(targetRoot, ".claude", "agents", "architect.md")));

        // Verify state files exist in .kyber-weave
        string lockPath = Path.Combine(targetRoot, ".kyber-weave", "squad.lock.yml");
        string receiptPath = Path.Combine(targetRoot, ".kyber-weave", "squad.receipt.json");
        Assert.True(File.Exists(lockPath));
        Assert.True(File.Exists(receiptPath));

        SquadReceipt? persistedReceipt = stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project);
        Assert.NotNull(persistedReceipt);
        Assert.Equal(SquadDeploymentScope.Project, persistedReceipt.Scope);
        Assert.NotEmpty(persistedReceipt.Files);
        Assert.All(persistedReceipt.Files, f => Assert.False(f.Adopted));

        // Verify release downloaded and APM invoked
        Assert.Single(releaseSource.Requests);
        Assert.Equal("1.2.3", releaseSource.Requests[0].Version);
        Assert.Single(apmRunner.RenderRequests);
        Assert.Equal(2, apmRunner.RenderRequests[0].Targets.Count);
    }

    [Fact]
    public async Task Install_DryRun_PerformsPreflightAndRenderWithoutModifyingTargetOrState()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "dry-run-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3",
            DryRun: true);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.NotNull(result.Plan);

        // Target directory must be completely untouched
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetRoot));

        // State directory must not exist
        Assert.False(Directory.Exists(Path.Combine(targetRoot, ".kyber-weave")));
        Assert.Null(stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));

        // Preflight release download and APM render were executed
        Assert.Single(releaseSource.Requests);
        Assert.Single(apmRunner.RenderRequests);
    }

    [Fact]
    public async Task Install_WhenConflictExistsWithoutAdopt_FailsWithoutOverwriting()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "conflict-project");
        string conflictingFile = Path.Combine(targetRoot, ".codex", "agents", "architect.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(conflictingFile)!);
        string unmanagedContent = "custom unmanaged agent content";
        await File.WriteAllTextAsync(conflictingFile, unmanagedContent);

        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3",
            Adopt: false);

        // Act & Assert
        await Assert.ThrowsAsync<SquadDeploymentConflictException>(() => service.InstallAsync(request));

        // Existing file must be byte-for-byte unchanged
        Assert.Equal(unmanagedContent, await File.ReadAllTextAsync(conflictingFile));

        // No state or other target files written
        Assert.False(Directory.Exists(Path.Combine(targetRoot, ".kyber-weave")));
        Assert.False(File.Exists(Path.Combine(targetRoot, ".codex", "agents", "code-reviewer.toml")));
    }

    [Fact]
    public async Task Install_WithAdopt_AdoptsIdenticalFilesAndWritesReceipt()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "adopt-project");
        string existingAgentPath = Path.Combine(targetRoot, ".codex", "agents", "architect.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(existingAgentPath)!);

        // Write content identical to what FakeApmRunner generates
        string canonicalBody = FakeApmRunner.GetAgentBody("architect");
        string identicalContent = $"name = \"architect\"\ndescription = \"Native Codex agent for architect.\"\ninstructions = \"\"\"\n{canonicalBody}\"\"\"\n";
        await File.WriteAllTextAsync(existingAgentPath, identicalContent);

        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3",
            Adopt: true);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Receipt);

        SquadOwnedFile adoptedEntry = Assert.Single(
            result.Receipt.Files,
            f => f.RelativePath == ".codex/agents/architect.toml");
        Assert.True(adoptedEntry.Adopted);

        // Other rendered files should be marked Adopted = false
        SquadOwnedFile nonAdoptedEntry = Assert.Single(
            result.Receipt.Files,
            f => f.RelativePath == ".codex/agents/code-reviewer.toml");
        Assert.False(nonAdoptedEntry.Adopted);

        // Existing file content was preserved
        Assert.Equal(identicalContent, await File.ReadAllTextAsync(existingAgentPath));
    }

    [Fact]
    public async Task Install_WhenRenderPlanThrows_RollsBackAllStagedFilesAndReleasesLease()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "rollback-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();

        // Throwing observer simulating unexpected crash during file application
        ThrowingTransactionObserver observer = new(SquadTransactionStepKind.FileApplied);
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore, observer: observer);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3");

        // Act & Assert: first run fails and triggers rollback
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(request));

        // Verify all target files rolled back (no partial writes left behind)
        Assert.False(File.Exists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));
        Assert.False(File.Exists(Path.Combine(targetRoot, ".kyber-weave", "squad.receipt.json")));

        // Verify lease was released by running a clean install immediately
        SquadLifecycleService cleanService = new(releaseSource, apmRunner, stateStore);
        SquadLifecycleResult cleanResult = await cleanService.InstallAsync(request);
        Assert.True(cleanResult.Success);
    }

    [Fact]
    public async Task Install_WhenAlreadyInstalledWithMatchingReceipt_IsIdempotentNoOp()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "idempotent-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3");

        // Initial install
        SquadLifecycleResult firstResult = await service.InstallAsync(request);
        Assert.True(firstResult.Success);

        // Act: Re-run install with identical parameters
        SquadLifecycleResult secondResult = await service.InstallAsync(request);

        // Assert
        Assert.True(secondResult.Success);
        Assert.True(File.Exists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));
    }

    [Fact]
    public async Task Install_WhenAlreadyInstalledWithMismatch_FailsWithoutOverwriting()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "mismatch-install-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        // Initial install with Codex
        await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3"));

        // Act & Assert: Attempt install with different target (Claude) without update -> fails
        SquadInstallRequest mismatchRequest = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Claude],
            Version: "1.2.3");

        await Assert.ThrowsAsync<SquadDeploymentConflictException>(() => service.InstallAsync(mismatchRequest));
    }

    [Fact]
    public async Task Install_GlobalScope_UsesIsolatedUserPathsAndDoesNotTouchRealHome()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "global-target");
        Directory.CreateDirectory(targetRoot);

        string isolatedUserData = Path.Combine(_temp.Path, "isolated-user-paths");
        FakeSquadUserPaths userPaths = new(isolatedUserData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Global,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3");

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);

        // State files are stored in isolated user data directory
        string globalStateDir = stateStore.ResolveStateDirectory(targetRoot, SquadDeploymentScope.Global);
        Assert.StartsWith(Path.GetFullPath(isolatedUserData), Path.GetFullPath(globalStateDir), StringComparison.Ordinal);
        Assert.True(File.Exists(stateStore.ResolveLockPath(targetRoot, SquadDeploymentScope.Global)));
        Assert.True(File.Exists(stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global)));

        // APM runner received UserScopeDirectory matching the isolated path
        ApmRenderRequest apmReq = Assert.Single(apmRunner.RenderRequests);
        Assert.Equal(isolatedUserData, apmReq.UserScopeDirectory);
        Assert.Equal(SquadDeploymentScope.Global, apmReq.Scope);
    }

    #endregion

    #region Update Lifecycle Contracts

    [Fact]
    public async Task Update_WhenInstalled_ReplacesManagedFilesAndUpdatesReceipt()
    {
        // Arrange: Perform initial install
        string targetRoot = Path.Combine(_temp.Path, "update-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3"));

        // User edits one managed file locally
        string architectFile = Path.Combine(targetRoot, ".codex", "agents", "architect.toml");
        string locallyEditedContent = await File.ReadAllTextAsync(architectFile) + "\n# Local operator modifications";
        await File.WriteAllTextAsync(architectFile, locallyEditedContent);

        // Part A: Update without ReplaceManaged -> locally edited file must be preserved
        SquadUpdateRequest updateRequestDefault = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Version: "1.2.4",
            ReplaceManaged: false);

        SquadLifecycleResult updateResultDefault = await service.UpdateAsync(updateRequestDefault);

        Assert.True(updateResultDefault.Success);
        Assert.Equal(locallyEditedContent, await File.ReadAllTextAsync(architectFile));

        // Part B: Update with ReplaceManaged = true -> locally edited file is replaced
        SquadUpdateRequest updateRequestReplace = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Version: "1.2.4",
            ReplaceManaged: true);

        SquadLifecycleResult updateResultReplace = await service.UpdateAsync(updateRequestReplace);

        Assert.True(updateResultReplace.Success);
        Assert.NotEqual(locallyEditedContent, await File.ReadAllTextAsync(architectFile));
    }

    [Fact]
    public async Task Update_WhenNotInstalled_FailsAndDirectsToInstall()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "not-installed-update-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        SquadUpdateRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Version: "1.2.4");

        // Act & Assert
        await Assert.ThrowsAsync<SquadDeploymentConflictException>(() => service.UpdateAsync(request));
    }

    [Fact]
    public async Task Update_DryRun_PerformsPreflightWithoutModifyingTargetOrState()
    {
        // Arrange: Initial install
        string targetRoot = Path.Combine(_temp.Path, "update-dry-run-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3"));

        string receiptBefore = await File.ReadAllTextAsync(Path.Combine(targetRoot, ".kyber-weave", "squad.receipt.json"));

        // Act: Update in dry-run mode
        SquadUpdateRequest dryRunRequest = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Version: "1.2.4",
            DryRun: true);

        SquadLifecycleResult result = await service.UpdateAsync(dryRunRequest);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.NotNull(result.Plan);

        // State receipt must be completely unchanged
        string receiptAfter = await File.ReadAllTextAsync(Path.Combine(targetRoot, ".kyber-weave", "squad.receipt.json"));
        Assert.Equal(receiptBefore, receiptAfter);
    }

    #endregion

    #region Uninstall Lifecycle Contracts

    [Fact]
    public async Task Uninstall_RemovesOnlyManagedFilesAndCleansReceipt()
    {
        // Arrange: Perform initial install
        string targetRoot = Path.Combine(_temp.Path, "uninstall-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3"));

        // Add unmanaged user file
        string unmanagedFile = Path.Combine(targetRoot, "user-config.json");
        await File.WriteAllTextAsync(unmanagedFile, "{\"custom\": true}");

        // Act: Uninstall
        SquadUninstallRequest uninstallRequest = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project);

        SquadLifecycleResult result = await service.UninstallAsync(uninstallRequest);

        // Assert
        Assert.True(result.Success);

        // Managed files removed
        Assert.False(File.Exists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));

        // Unmanaged file preserved
        Assert.True(File.Exists(unmanagedFile));
        Assert.Equal("{\"custom\": true}", await File.ReadAllTextAsync(unmanagedFile));

        // State receipt and lock removed when all files uninstalled
        Assert.Null(stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
        Assert.Null(stateStore.ReadLock(targetRoot, SquadDeploymentScope.Project));
    }

    [Fact]
    public async Task Uninstall_WhenNotInstalled_IsSuccessfulNoOp()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "not-installed-uninstall-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        SquadUninstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project);

        // Act
        SquadLifecycleResult result = await service.UninstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Null(result.Receipt);
    }

    [Fact]
    public async Task Uninstall_WhenLocallyEditedFileExists_PreservesEditedFileAndRetainsInReceipt()
    {
        // Arrange: Perform initial install
        string targetRoot = Path.Combine(_temp.Path, "uninstall-modified-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3"));

        // Locally modify one managed file
        string architectFile = Path.Combine(targetRoot, ".codex", "agents", "architect.toml");
        string modifiedContent = await File.ReadAllTextAsync(architectFile) + "\n# Modified by operator";
        await File.WriteAllTextAsync(architectFile, modifiedContent);

        // Act: Uninstall
        SquadUninstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project);

        SquadLifecycleResult result = await service.UninstallAsync(request);

        // Assert
        Assert.True(result.Success);

        // Modified file is preserved
        Assert.True(File.Exists(architectFile));
        Assert.Equal(modifiedContent, await File.ReadAllTextAsync(architectFile));

        // Unmodified managed files are removed
        Assert.False(File.Exists(Path.Combine(targetRoot, ".codex", "agents", "code-reviewer.toml")));

        // Retained receipt records the preserved file
        SquadReceipt? retainedReceipt = stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project);
        Assert.NotNull(retainedReceipt);
        SquadOwnedFile retained = Assert.Single(retainedReceipt.Files);
        Assert.Equal(".codex/agents/architect.toml", retained.RelativePath);
    }

    [Fact]
    public async Task Uninstall_DryRun_PerformsPreflightWithoutDeletingFiles()
    {
        // Arrange: Initial install
        string targetRoot = Path.Combine(_temp.Path, "uninstall-dry-run-project");
        Directory.CreateDirectory(targetRoot);
        string userData = Path.Combine(_temp.Path, "user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeApmRunner apmRunner = new();
        SquadLifecycleService service = new(releaseSource, apmRunner, stateStore);

        await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3"));

        // Act: Dry-run uninstall
        SquadUninstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            DryRun: true);

        SquadLifecycleResult result = await service.UninstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.NotNull(result.Plan);

        // Files and receipt remain completely intact
        Assert.True(File.Exists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));
        Assert.NotNull(stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
    }

    #endregion

    #region Helper Classes

    private sealed class ThrowingTransactionObserver(SquadTransactionStepKind failAtStep) : ISquadTransactionObserver
    {
        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind == failAtStep)
            {
                throw new InvalidOperationException($"Simulated transaction failure at step {step.Kind}");
            }
        }
    }

    #endregion
}

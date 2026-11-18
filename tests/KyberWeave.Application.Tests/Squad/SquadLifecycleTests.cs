using KyberWeave.Testing.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>
/// Task K8 Test Suite: Pins install, update, and uninstall lifecycle orchestration across
/// project and global scopes, verifying lease management, preflight, rollback, exact-match adoption,
/// user-edit preservation, and isolated user paths.
/// Covers Test Contract K8 from docs/plans/2026-08-14-kyber-squad-unified-agent-skill-deployment.md.
/// </summary>
public sealed class SquadLifecycleTests
{
    private readonly InMemorySquadState _state = new();

    /// <summary>The home-level state directory for a project, keyed by its physical root.</summary>
    private string HomeProjectStateDirectory(ISquadUserPaths userPaths, string projectRoot) =>
        Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "projects",
            SquadPhysicalRootIdentities.Resolve(projectRoot, _state.Semantics).Key);

    /// <summary>A project that already holds a Codex install at 1.2.3: the starting point for an install, update or uninstall.</summary>
    private async Task InstallTemplateAsync(string projectRoot)
    {
        _state.Files.CreateDirectory(projectRoot);
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, new FakeSquadRenderer());

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            projectRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Codex],
            Version: "1.2.3"));
        Assert.True(result.Success, $"The template install failed: {string.Join("; ", result.Errors ?? [])}");
    }

    #region Install Lifecycle Contracts

    [Fact]
    public async Task Install_WhenCleanProject_AcquiresLeaseRendersProjectionsAndWritesReceipt()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "clean-project");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
        Assert.True(_state.Files.FileExists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));
        Assert.True(_state.Files.FileExists(Path.Combine(targetRoot, ".claude", "agents", "architect.md")));

        // Verify state files exist beneath the home store, keyed by the project's physical root
        string lockPath = Path.Combine(HomeProjectStateDirectory(userPaths, targetRoot), "squad.lock.yml");
        string receiptPath = Path.Combine(HomeProjectStateDirectory(userPaths, targetRoot), "squad.receipt.json");
        Assert.True(_state.Files.FileExists(lockPath));
        Assert.True(_state.Files.FileExists(receiptPath));

        SquadReceipt? persistedReceipt = stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project);
        Assert.NotNull(persistedReceipt);
        Assert.Equal(SquadDeploymentScope.Project, persistedReceipt.Scope);
        Assert.NotEmpty(persistedReceipt.Files);
        Assert.All(persistedReceipt.Files, f => Assert.False(f.Adopted));

        // Verify release downloaded and APM invoked
        Assert.Single(releaseSource.Requests);
        Assert.Equal("1.2.3", releaseSource.Requests[0].Version);
        Assert.Single(renderer.RenderRequests);
        Assert.Equal(2, renderer.RenderRequests[0].Targets.Count);
    }

    [Fact]
    public async Task Install_DryRun_PerformsPreflightAndRenderWithoutModifyingTargetOrState()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "dry-run-project");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
        Assert.Empty(_state.Files.EnumerateEntries(targetRoot));

        // State directory must not exist
        Assert.False(_state.Files.DirectoryExists(Path.Combine(targetRoot, ".kyber-weave")));
        Assert.Null(stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));

        // Preflight release download and APM render were executed
        Assert.Single(releaseSource.Requests);
        Assert.Single(renderer.RenderRequests);
    }

    [Fact]
    public async Task Install_WhenConflictExistsWithoutAdopt_FailsWithoutOverwriting()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "conflict-project");
        string conflictingFile = Path.Combine(targetRoot, ".codex", "agents", "architect.toml");
        _state.Files.CreateDirectory(Path.GetDirectoryName(conflictingFile)!);
        string unmanagedContent = "custom unmanaged agent content";
        _state.Files.WriteAllText(conflictingFile, unmanagedContent);

        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3",
            Adopt: false);

        // Act & Assert
        await Assert.ThrowsAsync<SquadDeploymentConflictException>(() => service.InstallAsync(request));

        // Existing file must be byte-for-byte unchanged
        Assert.Equal(unmanagedContent, _state.Files.ReadAllText(conflictingFile));

        // No state or other target files written
        Assert.False(_state.Files.DirectoryExists(Path.Combine(targetRoot, ".kyber-weave")));
        Assert.False(_state.Files.FileExists(Path.Combine(targetRoot, ".codex", "agents", "code-reviewer.toml")));
    }

    [Fact]
    public async Task Install_WithAdopt_AdoptsIdenticalFilesAndWritesReceipt()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "adopt-project");
        string existingAgentPath = Path.Combine(targetRoot, ".codex", "agents", "architect.toml");
        _state.Files.CreateDirectory(Path.GetDirectoryName(existingAgentPath)!);

        // Write content identical to what FakeSquadRenderer generates
        string canonicalBody = FakeSquadRenderer.GetAgentBody("architect");
        string identicalContent = $"name = \"architect\"\ndescription = \"Native Codex agent for architect.\"\ninstructions = \"\"\"\n{canonicalBody}\"\"\"\n";
        _state.Files.WriteAllText(existingAgentPath, identicalContent);

        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
        Assert.Equal(identicalContent, _state.Files.ReadAllText(existingAgentPath));
    }

    [Fact]
    public async Task Install_WhenRenderPlanThrows_RollsBackAllStagedFilesAndReleasesLease()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "rollback-project");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };

        // Throwing observer simulating unexpected crash during file application
        ThrowingTransactionObserver observer = new(SquadTransactionStepKind.FileApplied);
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer, observer: observer);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3");

        // Act & Assert: first run fails and triggers rollback
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(request));

        // Verify all target files rolled back (no partial writes left behind)
        Assert.False(_state.Files.FileExists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));
        Assert.False(_state.Files.FileExists(Path.Combine(HomeProjectStateDirectory(userPaths, targetRoot), "squad.receipt.json")));

        // Verify lease was released by running a clean install immediately
        SquadLifecycleService cleanService = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);
        SquadLifecycleResult cleanResult = await cleanService.InstallAsync(request);
        Assert.True(cleanResult.Success);
    }

    [Fact]
    public async Task Install_WhenAlreadyInstalledWithMatchingReceipt_IsIdempotentNoOp()
    {
        // Arrange: a project that already holds the class template's install
        string targetRoot = Path.Combine(_state.Files.Root, "work", "idempotent-project");
        ISquadUserPaths userPaths = _state.UserPaths;
        await InstallTemplateAsync(targetRoot);
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Codex],
            Version: "1.2.3");

        // Act: Re-run install with identical parameters
        SquadLifecycleResult secondResult = await service.InstallAsync(request);

        // Assert
        Assert.True(secondResult.Success);
        Assert.True(_state.Files.FileExists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));
    }

    [Fact]
    public async Task Install_WhenAlreadyInstalledWithMismatch_FailsWithoutOverwriting()
    {
        // Arrange: a project that already holds the class template's Codex install
        string targetRoot = Path.Combine(_state.Files.Root, "work", "mismatch-install-project");
        ISquadUserPaths userPaths = _state.UserPaths;
        await InstallTemplateAsync(targetRoot);
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
        string targetRoot = Path.Combine(_state.Files.Root, "work", "global-target");
        _state.Files.CreateDirectory(targetRoot);

        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
        Assert.Equal(
            Path.GetFullPath(Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "global")),
            Path.GetFullPath(globalStateDir));
        Assert.True(_state.Files.FileExists(stateStore.ResolveLockPath(targetRoot, SquadDeploymentScope.Global)));
        Assert.True(_state.Files.FileExists(stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global)));

        // APM runner received UserScopeDirectory matching the isolated path
        SquadRenderRequest apmReq = Assert.Single(renderer.RenderRequests);
        Assert.Equal(userPaths.ApplicationDataDirectory, apmReq.UserScopeDirectory);
        Assert.Equal(SquadDeploymentScope.Global, apmReq.Scope);
    }

    #endregion

    #region Update Lifecycle Contracts

    [Fact]
    public async Task Update_WhenInstalled_ReplacesManagedFilesAndUpdatesReceipt()
    {
        // Arrange: a project that already holds the class template's install
        string targetRoot = Path.Combine(_state.Files.Root, "work", "update-project");
        ISquadUserPaths userPaths = _state.UserPaths;
        await InstallTemplateAsync(targetRoot);
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // User edits one managed file locally
        string architectFile = Path.Combine(targetRoot, ".codex", "agents", "architect.toml");
        string locallyEditedContent = _state.Files.ReadAllText(architectFile) + "\n# Local operator modifications";
        _state.Files.WriteAllText(architectFile, locallyEditedContent);

        // Part A: Update without ReplaceManaged -> locally edited file must be preserved
        SquadUpdateRequest updateRequestDefault = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Version: "1.2.4",
            ReplaceManaged: false);

        SquadLifecycleResult updateResultDefault = await service.UpdateAsync(updateRequestDefault);

        Assert.True(updateResultDefault.Success);
        Assert.Equal(locallyEditedContent, _state.Files.ReadAllText(architectFile));

        // Part B: Update with ReplaceManaged = true -> locally edited file is replaced
        SquadUpdateRequest updateRequestReplace = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Version: "1.2.4",
            ReplaceManaged: true);

        SquadLifecycleResult updateResultReplace = await service.UpdateAsync(updateRequestReplace);

        Assert.True(updateResultReplace.Success);
        Assert.NotEqual(locallyEditedContent, _state.Files.ReadAllText(architectFile));
    }

    [Fact]
    public async Task Update_WhenNotInstalled_FailsAndDirectsToInstall()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "not-installed-update-project");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
        // Arrange: a project that already holds the class template's install
        string targetRoot = Path.Combine(_state.Files.Root, "work", "update-dry-run-project");
        ISquadUserPaths userPaths = _state.UserPaths;
        await InstallTemplateAsync(targetRoot);
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        string receiptBefore = _state.Files.ReadAllText(Path.Combine(HomeProjectStateDirectory(userPaths, targetRoot), "squad.receipt.json"));

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
        string receiptAfter = _state.Files.ReadAllText(Path.Combine(HomeProjectStateDirectory(userPaths, targetRoot), "squad.receipt.json"));
        Assert.Equal(receiptBefore, receiptAfter);
    }

    #endregion

    #region Uninstall Lifecycle Contracts

    [Fact]
    public async Task Uninstall_RemovesOnlyManagedFilesAndCleansReceipt()
    {
        // Arrange: a project that already holds the class template's install
        string targetRoot = Path.Combine(_state.Files.Root, "work", "uninstall-project");
        ISquadUserPaths userPaths = _state.UserPaths;
        await InstallTemplateAsync(targetRoot);
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // Add unmanaged user file
        string unmanagedFile = Path.Combine(targetRoot, "user-config.json");
        _state.Files.WriteAllText(unmanagedFile, "{\"custom\": true}");

        // Act: Uninstall
        SquadUninstallRequest uninstallRequest = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project);

        SquadLifecycleResult result = await service.UninstallAsync(uninstallRequest);

        // Assert
        Assert.True(result.Success);

        // Managed files removed
        Assert.False(_state.Files.FileExists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));

        // Unmanaged file preserved
        Assert.True(_state.Files.FileExists(unmanagedFile));
        Assert.Equal("{\"custom\": true}", _state.Files.ReadAllText(unmanagedFile));

        // State receipt and lock removed when all files uninstalled
        Assert.Null(stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
        Assert.Null(stateStore.ReadLock(targetRoot, SquadDeploymentScope.Project));
    }

    [Fact]
    public async Task Uninstall_WhenNotInstalled_IsSuccessfulNoOp()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "not-installed-uninstall-project");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
        // Arrange: a project that already holds the class template's install
        string targetRoot = Path.Combine(_state.Files.Root, "work", "uninstall-modified-project");
        ISquadUserPaths userPaths = _state.UserPaths;
        await InstallTemplateAsync(targetRoot);
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // Locally modify one managed file
        string architectFile = Path.Combine(targetRoot, ".codex", "agents", "architect.toml");
        string modifiedContent = _state.Files.ReadAllText(architectFile) + "\n# Modified by operator";
        _state.Files.WriteAllText(architectFile, modifiedContent);

        // Act: Uninstall
        SquadUninstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project);

        SquadLifecycleResult result = await service.UninstallAsync(request);

        // Assert
        Assert.True(result.Success);

        // Modified file is preserved
        Assert.True(_state.Files.FileExists(architectFile));
        Assert.Equal(modifiedContent, _state.Files.ReadAllText(architectFile));

        // Unmodified managed files are removed
        Assert.False(_state.Files.FileExists(Path.Combine(targetRoot, ".codex", "agents", "code-reviewer.toml")));

        // Retained receipt records the preserved file
        SquadReceipt? retainedReceipt = stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project);
        Assert.NotNull(retainedReceipt);
        SquadOwnedFile retained = Assert.Single(retainedReceipt.Files);
        Assert.Equal(".codex/agents/architect.toml", retained.RelativePath);
    }

    [Fact]
    public async Task Uninstall_DryRun_PerformsPreflightWithoutDeletingFiles()
    {
        // Arrange: a project that already holds the class template's install
        string targetRoot = Path.Combine(_state.Files.Root, "work", "uninstall-dry-run-project");
        ISquadUserPaths userPaths = _state.UserPaths;
        await InstallTemplateAsync(targetRoot);
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
        Assert.True(_state.Files.FileExists(Path.Combine(targetRoot, ".codex", "agents", "architect.toml")));
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

using KyberWeave.Testing.Squad;
using System.Security.Cryptography;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>
///     Verifies Squad install and update lifecycle behaviors when applying, persisting, and diffing model presets.
///     Acceptance criteria:
///     - 2.1: Squad re-renders selected targets with preset through same transaction update uses.
///     - 2.2: Squad SHALL NOT modify a deployed file outside that transaction.
///     - 3.1: Squad SHALL record active preset per scope and target in Squad state.
///     - 3.2: WHEN update runs, Squad SHALL re-apply recorded preset.
///     - 3.4: WHEN recorded preset no longer exists, update SHALL fail before writing and name missing preset.
/// </summary>
public sealed class SquadModelPresetLifecycleTests
{
    private const string FastPresetYaml = """
                                          schema: kyber-squad.model-preset/v1
                                          profiles:
                                            deep-planning:
                                              default: inherit
                                              copilot: gpt-4o
                                            general:
                                              default: gpt-4o-mini
                                              copilot: gpt-4o-mini
                                          """;

    private const string QualityPresetYaml = """
                                             schema: kyber-squad.model-preset/v1
                                             profiles:
                                               deep-planning:
                                                 default: o3-mini
                                                 copilot: o3-mini
                                                 claude: claude-3-7-sonnet
                                               general:
                                                 default: gpt-4o
                                                 copilot: gpt-4o
                                                 claude: claude-3-7-sonnet
                                             """;

    private const string ClaudeFastPresetYaml = """
                                                schema: kyber-squad.model-preset/v1
                                                profiles:
                                                  deep-planning:
                                                    claude: claude-3-5-haiku
                                                  general:
                                                    claude: claude-3-5-haiku
                                                """;

    private const string ClaudeQualityPresetYaml = """
                                                   schema: kyber-squad.model-preset/v1
                                                   profiles:
                                                     deep-planning:
                                                       claude: claude-3-7-sonnet
                                                     general:
                                                       claude: claude-3-7-sonnet
                                                   """;

    private const string UnknownCategoryPresetYaml = """
                                                     schema: kyber-squad.model-preset/v1
                                                     profiles:
                                                       nonexistent-profile-category:
                                                         default: custom-unsupported-model
                                                     """;

    private readonly InMemorySquadState _state = new();

    [Fact]
    public async Task InstallWithPresetWritesV2LockAndReceiptHashesMatchDeployedFiles()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "install-preset-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        Dictionary<SquadTarget, string> presets = new()
        {
            [SquadTarget.Copilot] = "fast"
        };

        SquadInstallRequest request = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: presets);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Lock);
        Assert.Equal("kyber-squad.lock/v2", result.Lock.Schema);

        Assert.Single(renderer.RenderRequests);
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? appliedPresets = renderer.RenderRequests[0].ModelPresets;
        Assert.NotNull(appliedPresets);
        Assert.True(appliedPresets.ContainsKey(SquadTarget.Copilot));
        Assert.Equal("fast", appliedPresets[SquadTarget.Copilot].Name);

        SquadLock? persistedLock = stateStore.ReadLock(targetRoot, SquadDeploymentScope.Project);
        Assert.NotNull(persistedLock);
        Assert.Equal("kyber-squad.lock/v2", persistedLock.Schema);

        SquadReceipt? receipt = stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project);
        Assert.NotNull(receipt);
        Assert.NotEmpty(receipt.Files);

        foreach (SquadOwnedFile file in receipt.Files)
        {
            string diskPath = ResolveTargetPath(targetRoot, file.RelativePath);
            Assert.True(_state.Files.FileExists(diskPath));
            byte[] diskBytes = _state.Files.ReadAllBytes(diskPath);
            string diskHash = Convert.ToHexStringLower(SHA256.HashData(diskBytes));
            Assert.Equal(file.Sha256, diskHash);
        }
    }

    [Fact]
    public async Task UpdateWithNullModelPresetsReappliesRecordedPresetToNewRelease()
    {
        // Arrange — an install that recorded "fast" is the starting point; the update is the subject.
        string targetRoot = Path.Combine(_state.Files.Root, "work", "update-reapply-preset-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);
        await InstallPresetProjectAsync(
            targetRoot,
            [SquadTarget.Copilot],
            new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "fast" },
                        ("fast", FastPresetYaml));

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // Act — update specifying ModelPresets = null must preserve and re-apply recorded preset
        SquadUpdateRequest updateRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            Version: "1.1.0",
            ModelPresets: null);

        SquadLifecycleResult updateResult = await service.UpdateAsync(updateRequest);

        // Assert
        Assert.True(updateResult.Success);
        Assert.NotNull(updateResult.Lock);
        Assert.Equal("kyber-squad.lock/v2", updateResult.Lock.Schema);

        Assert.Single(renderer.RenderRequests);
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? updatePresets = renderer.RenderRequests[0].ModelPresets;
        Assert.NotNull(updatePresets);
        Assert.True(updatePresets.ContainsKey(SquadTarget.Copilot));
        Assert.Equal("fast", updatePresets[SquadTarget.Copilot].Name);

        SquadLock? updatedLock = stateStore.ReadLock(targetRoot, SquadDeploymentScope.Project);
        Assert.NotNull(updatedLock);
        Assert.Equal("kyber-squad.lock/v2", updatedLock.Schema);
    }

    [Fact]
    public async Task UpdateChangingOnlyClaudePresetLeavesSecondTargetPresetUnchanged()
    {
        // Arrange — an install recording one preset per target is the starting point.
        string targetRoot = Path.Combine(_state.Files.Root, "work", "update-partial-override-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        await WritePresetAsync(userPaths, "claude-fast", ClaudeFastPresetYaml);
        await WritePresetAsync(userPaths, "copilot-fast", FastPresetYaml);
        await WritePresetAsync(userPaths, "claude-quality", ClaudeQualityPresetYaml);
        await InstallPresetProjectAsync(
            targetRoot,
            [SquadTarget.Claude, SquadTarget.Copilot],
            new Dictionary<SquadTarget, string>
            {
                [SquadTarget.Claude] = "claude-fast",
                [SquadTarget.Copilot] = "copilot-fast"
            },
                        ("claude-fast", ClaudeFastPresetYaml),
                        ("copilot-fast", FastPresetYaml));

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // Act — update overriding only Claude target's preset
        SquadUpdateRequest updateRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            Version: "1.1.0",
            ModelPresets: new Dictionary<SquadTarget, string>
            {
                [SquadTarget.Claude] = "claude-quality"
            });

        SquadLifecycleResult updateResult = await service.UpdateAsync(updateRequest);

        // Assert
        Assert.True(updateResult.Success);
        Assert.Single(renderer.RenderRequests);
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? updatePresets = renderer.RenderRequests[0].ModelPresets;
        Assert.NotNull(updatePresets);
        Assert.Equal("claude-quality", updatePresets[SquadTarget.Claude].Name);
        Assert.Equal("copilot-fast", updatePresets[SquadTarget.Copilot].Name);
    }

    [Fact]
    public async Task UpdateWhenRecordedPresetMissingThrowsConflictExceptionAndAbortsBeforeDownload()
    {
        // Arrange — an install that recorded "ephemeral" is the starting point; deleting its preset file
        // is what the update must refuse.
        string targetRoot = Path.Combine(_state.Files.Root, "work", "update-missing-preset-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        string presetPath = await WritePresetAsync(userPaths, "ephemeral", FastPresetYaml);
        await InstallPresetProjectAsync(
            targetRoot,
            [SquadTarget.Copilot],
            new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "ephemeral" },
                        ("ephemeral", FastPresetYaml));

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // Record snapshots before deleting preset and running update
        Dictionary<string, byte[]> rootSnapshotBefore = CaptureDirectorySnapshot(targetRoot);
        Dictionary<string, byte[]> homeSnapshotBefore = CaptureDirectorySnapshot(userPaths.HomeDirectory);

        // Delete recorded preset file
        _state.Files.DeleteFile(presetPath);
        homeSnapshotBefore.Remove(presetPath);

        // Act
        SquadUpdateRequest updateRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            Version: "1.1.0",
            ModelPresets: null);

        SquadDeploymentConflictException exception =
            await Assert.ThrowsAsync<SquadDeploymentConflictException>(() => service.UpdateAsync(updateRequest));

        // Assert
        Assert.Contains("copilot", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ephemeral", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ephemeral.yml", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kyber-weave squad preset clear --target copilot", exception.Message,
            StringComparison.OrdinalIgnoreCase);

        // Must fail closed without calling release source download
        Assert.Empty(releaseSource.Requests);

        // Target root and home store snapshots must remain completely untouched
        Dictionary<string, byte[]> rootSnapshotAfter = CaptureDirectorySnapshot(targetRoot);
        Dictionary<string, byte[]> homeSnapshotAfter = CaptureDirectorySnapshot(userPaths.HomeDirectory);
        AssertDirectorySnapshotsEqual(rootSnapshotBefore, rootSnapshotAfter);
        AssertDirectorySnapshotsEqual(homeSnapshotBefore, homeSnapshotAfter);
    }

    [Fact]
    public async Task InstallWhenPresetMissingThrowsConflictExceptionAndAbortsBeforeDownload()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "install-missing-preset-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadInstallRequest request = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: new Dictionary<SquadTarget, string>
            {
                [SquadTarget.Copilot] = "nonexistent-preset"
            });

        // Act
        SquadDeploymentConflictException exception =
            await Assert.ThrowsAsync<SquadDeploymentConflictException>(() => service.InstallAsync(request));

        // Assert
        Assert.Contains("copilot", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nonexistent-preset", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nonexistent-preset.yml", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kyber-weave squad preset clear --target copilot", exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Empty(releaseSource.Requests);
        Assert.Empty(_state.Files.EnumerateFiles(targetRoot, "*", recursive: true));
    }

    [Fact]
    public async Task InstallWithPresetReferencingUnknownCategoryThrowsSourceValidationExceptionBeforeWriting()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "unknown-category-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        await WritePresetAsync(userPaths, "bad-category", UnknownCategoryPresetYaml);

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadInstallRequest request = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: new Dictionary<SquadTarget, string>
            {
                [SquadTarget.Copilot] = "bad-category"
            });

        // Act
        SquadSourceValidationException exception =
            await Assert.ThrowsAsync<SquadSourceValidationException>(() => service.InstallAsync(request));

        // Assert
        Assert.Contains("nonexistent-profile-category", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_state.Files.EnumerateFiles(targetRoot, "*", recursive: true));
    }

    [Fact]
    public async Task DryRunInstallWithPresetReturnsModelChangesAndWritesNothingToDisk()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "dry-run-install-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadInstallRequest request = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            DryRun: true,
            ModelPresets: new Dictionary<SquadTarget, string>
            {
                [SquadTarget.Copilot] = "fast"
            });

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.NotNull(result.ModelChanges);
        Assert.NotEmpty(result.ModelChanges);
        Assert.All(result.ModelChanges, item => { Assert.False(string.IsNullOrWhiteSpace(item.Agent)); });

        // Dry run must write nothing to target root or state store
        Assert.Empty(_state.Files.EnumerateFiles(targetRoot, "*", recursive: true));
        Assert.Null(stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
        Assert.Null(stateStore.ReadLock(targetRoot, SquadDeploymentScope.Project));
    }

    [Fact]
    public async Task TwoDistinctProjectRootsMaintainIndependentPresetMaps()
    {
        // Arrange — two installs, each recording its own preset, are the starting points.
        string projectRoot1 = Path.Combine(_state.Files.Root, "work", "project-1");
        string projectRoot2 = Path.Combine(_state.Files.Root, "work", "project-2");
        _state.Files.CreateDirectory(projectRoot1);
        _state.Files.CreateDirectory(projectRoot2);

        ISquadUserPaths userPaths = _state.UserPaths;
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);
        await WritePresetAsync(userPaths, "quality", QualityPresetYaml);

        await InstallPresetProjectAsync(
            projectRoot1,
            [SquadTarget.Copilot],
            new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "fast" },
                        ("fast", FastPresetYaml));
        await InstallPresetProjectAsync(
            projectRoot2,
            [SquadTarget.Copilot],
            new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "quality" },
                        ("quality", QualityPresetYaml));

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // Act — update Project 1 with ModelPresets = null
        SquadUpdateRequest update1 = new(
            projectRoot1,
            SquadDeploymentScope.Project,
            Version: "1.1.0",
            ModelPresets: null);

        SquadLifecycleResult updateResult1 = await service.UpdateAsync(update1);
        Assert.True(updateResult1.Success);

        // Assert — Project 1 re-applies "fast", Project 2's persisted lock remains "quality"
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>?
            lastRenderPresets = renderer.RenderRequests[^1].ModelPresets;
        Assert.NotNull(lastRenderPresets);
        Assert.Equal("fast", lastRenderPresets[SquadTarget.Copilot].Name);

        SquadLock? lock1 = stateStore.ReadLock(projectRoot1, SquadDeploymentScope.Project);
        SquadLock? lock2 = stateStore.ReadLock(projectRoot2, SquadDeploymentScope.Project);
        Assert.NotNull(lock1);
        Assert.NotNull(lock2);
        Assert.Equal("kyber-squad.lock/v2", lock1.Schema);
        Assert.Equal("kyber-squad.lock/v2", lock2.Schema);
    }

    [Fact]
    public async Task UpdateWithEmptyModelPresetsMapClearsActivePresets()
    {
        // Arrange — an install that recorded "fast" is the starting point.
        string targetRoot = Path.Combine(_state.Files.Root, "work", "update-clear-presets-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);
        await InstallPresetProjectAsync(
            targetRoot,
            [SquadTarget.Copilot],
            new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "fast" },
                        ("fast", FastPresetYaml));

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // Act — update with empty map clears presets
        SquadUpdateRequest updateRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            Version: "1.1.0",
            ModelPresets: new Dictionary<SquadTarget, string>());

        SquadLifecycleResult updateResult = await service.UpdateAsync(updateRequest);

        // Assert
        Assert.True(updateResult.Success);
        Assert.Single(renderer.RenderRequests);
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? updatePresets = renderer.RenderRequests[0].ModelPresets;
        Assert.True(updatePresets is null || updatePresets.Count == 0);
    }

    [Fact]
    public async Task InstallAsyncWithInstanceScopeAndMultipleTargetsThrowsArgumentException()
    {
        string targetRoot = Path.Combine(_state.Files.Root, "work", "instance-multi-target-install");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadInstallRequest request = new(
            targetRoot,
            SquadDeploymentScope.Instance,
            [SquadTarget.Claude, SquadTarget.Codex],
            Version: "1.0.0");

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => service.InstallAsync(request));
        Assert.Contains("requires exactly one target", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsyncWithInstanceScopeAndMultipleTargetsThrowsArgumentException()
    {
        string targetRoot = Path.Combine(_state.Files.Root, "work", "instance-multi-target-update");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadUpdateRequest request = new(
            targetRoot,
            SquadDeploymentScope.Instance,
            [SquadTarget.Claude, SquadTarget.Codex]);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(request));
        Assert.Contains("requires exactly one target", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallWithPresetAndCorruptExtractedSquadSourceThrowsSourceValidationExceptionWithoutSwallowing()
    {
        string targetRoot = Path.Combine(_state.Files.Root, "work", "corrupt-source-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = new()
        {
            Files = _state.Files,
            FileContents = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["squad.yml"] = "name: kyber-squad\nschema: invalid-schema-v999\n",
                ["toolchain.yml"] =
                    "schema: kyber-squad.toolchain/v1\nrequired-features: []\nvalidated-release: null\n",
            },
            AssetName = "asset",
            DownloadUri = new Uri("https://example.com/asset.zip"),
        };
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadInstallRequest request = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: new Dictionary<SquadTarget, string>
            {
                [SquadTarget.Copilot] = "fast"
            });

        await Assert.ThrowsAsync<SquadSourceValidationException>(() => service.InstallAsync(request));
    }

    [Fact]
    public async Task CommittedInstallWritesCanonicalSnapshotKeyedByReleaseVersion()
    {
        // Arrange — no presets: source resolution must run whenever the extraction root is
        // available, not only when presets are loaded.
        string targetRoot = Path.Combine(_state.Files.Root, "work", "install-snapshot-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadInstallRequest request = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0");

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        SquadCanonicalProfileStore snapshots = new(userPaths, _state.Files);
        SquadCanonicalProfiles? snapshot = snapshots.TryRead("1.0.0");
        Assert.NotNull(snapshot);
        Assert.Equal("1.0.0", snapshot.SquadVersion);
        Assert.NotEmpty(snapshot.Profiles.Profiles);
        Assert.NotEmpty(snapshot.AgentProfiles);
        Assert.All(snapshot.AgentProfiles,
            kvp => Assert.True(snapshot.Profiles.Profiles.ContainsKey(kvp.Value),
                $"Agent '{kvp.Key}' maps to unknown profile category '{kvp.Value}'."));
        Assert.Equal("1.0.0", snapshots.TryReadLatest()?.SquadVersion);
    }

    [Fact]
    public async Task CommittedUpdateWritesCanonicalSnapshotKeyedByReleaseVersion()
    {
        // Arrange — an install of 1.0.0, with the snapshot it wrote, is the starting point.
        string targetRoot = Path.Combine(_state.Files.Root, "work", "update-snapshot-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        await InstallPresetProjectAsync(
            targetRoot,
            [SquadTarget.Copilot],
            new Dictionary<SquadTarget, string>());

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // Act
        SquadUpdateRequest updateRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            Version: "1.1.0");

        SquadLifecycleResult updateResult = await service.UpdateAsync(updateRequest);

        // Assert — the update snapshot is keyed by the new release version; the older
        // snapshot is retained (no pruning) and the newest read resolves to the update.
        Assert.True(updateResult.Success);
        SquadCanonicalProfileStore snapshots = new(userPaths, _state.Files);
        SquadCanonicalProfiles? updated = snapshots.TryRead("1.1.0");
        Assert.NotNull(updated);
        Assert.Equal("1.1.0", updated.SquadVersion);
        Assert.NotEmpty(updated.Profiles.Profiles);
        Assert.NotEmpty(updated.AgentProfiles);
        Assert.NotNull(snapshots.TryRead("1.0.0"));
        Assert.Equal("1.1.0", snapshots.TryReadLatest()?.SquadVersion);
    }

    [Fact]
    public async Task DryRunInstallAndUpdateWriteNoCanonicalSnapshot()
    {
        // Arrange
        string targetRoot = Path.Combine(_state.Files.Root, "work", "dry-run-snapshot-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        // Act — dry-run install modifies no state, so it must not write a snapshot.
        SquadInstallRequest dryInstall = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            DryRun: true);

        SquadLifecycleResult dryInstallResult = await service.InstallAsync(dryInstall);

        // Assert
        Assert.True(dryInstallResult.Success);
        SquadCanonicalProfileStore snapshots = new(userPaths, _state.Files);
        Assert.Null(snapshots.TryRead("1.0.0"));
        Assert.Null(snapshots.TryReadLatest());

        // Act — a committed install followed by a dry-run update: only the committed
        // version gains a snapshot.
        SquadInstallRequest installRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0");

        SquadLifecycleResult installResult = await service.InstallAsync(installRequest);
        Assert.True(installResult.Success);

        SquadUpdateRequest dryUpdate = new(
            targetRoot,
            SquadDeploymentScope.Project,
            Version: "1.1.0",
            DryRun: true);

        SquadLifecycleResult dryUpdateResult = await service.UpdateAsync(dryUpdate);

        // Assert
        Assert.True(dryUpdateResult.Success);
        Assert.NotNull(snapshots.TryRead("1.0.0"));
        Assert.Null(snapshots.TryRead("1.1.0"));
        Assert.Equal("1.0.0", snapshots.TryReadLatest()?.SquadVersion);
    }

    [Fact]
    public async Task CommittedInstallWithUnwritableSnapshotWarnsAndKeepsDeployment()
    {
        // Arrange — a directory at the snapshot file path makes the atomic rename fail
        // after the deployment has already committed.
        string targetRoot = Path.Combine(_state.Files.Root, "work", "snapshot-failure-root");
        _state.Files.CreateDirectory(targetRoot);
        ISquadUserPaths userPaths = _state.UserPaths;
        string blocker = Path.Combine(
            userPaths.HomeDirectory, ".kyber-weave", "squad", "profiles", "1.0.0.yml");
        _state.Files.CreateDirectory(blocker);

        SquadStateStore stateStore = _state.Store;
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

        SquadInstallRequest request = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0");

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert — the deployment succeeds and the warning names the remedy.
        Assert.True(result.Success);
        Assert.NotNull(result.Warnings);
        Assert.Contains(result.Warnings,
            warning => warning.Contains("squad update", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
        Assert.NotNull(stateStore.ReadLock(targetRoot, SquadDeploymentScope.Project));
        Assert.Null(new SquadCanonicalProfileStore(userPaths, _state.Files).TryRead("1.0.0"));
    }

    /// <summary>
    ///     A project that already holds an install recording <paramref name="presetNames"/>, with the preset files it
    ///     names written into the home: the starting point for an update.
    /// </summary>
    private async Task InstallPresetProjectAsync(
        string projectRoot,
        IReadOnlyList<SquadTarget> targets,
        IReadOnlyDictionary<SquadTarget, string> presetNames,
        params (string Name, string Yaml)[] presets)
    {
        _state.Files.CreateDirectory(projectRoot);
        foreach ((string name, string yaml) in presets) await WritePresetAsync(_state.UserPaths, name, yaml);

        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, new FakeSquadRenderer());
        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            projectRoot,
            SquadDeploymentScope.Project,
            targets,
            Version: "1.0.0",
            ModelPresets: presetNames.Count == 0 ? null : presetNames));
        Assert.True(result.Success, $"The prior install failed: {string.Join("; ", result.Errors ?? [])}");
    }

    private FakeSquadReleaseSource ReleaseSource()
    {
        string source = Path.Combine(_state.Files.Root, "release", "kyber-squad");
        _ = CanonicalSquadTree.CopyInto(_state.Files, source);
        return FakeSquadReleaseSource.FromDirectory(source, _state.Files);
    }

    private async Task<string> WritePresetAsync(ISquadUserPaths userPaths, string name, string yaml)
    {
        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        _state.Files.CreateDirectory(presetsDir);
        string presetPath = Path.Combine(presetsDir, $"{name}.yml");
        _state.Files.WriteAllText(presetPath, yaml);
        return presetPath;
    }

    private string ResolveTargetPath(string root, string relativePath)
    {
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private Dictionary<string, byte[]> CaptureDirectorySnapshot(string root)
    {
        Dictionary<string, byte[]> snapshot = new(StringComparer.Ordinal);
        if (!_state.Files.DirectoryExists(root)) return snapshot;

        foreach (string file in _state.Files.EnumerateFiles(root, "*", recursive: true))
            snapshot[file] = _state.Files.ReadAllBytes(file);

        return snapshot;
    }

    private static void AssertDirectorySnapshotsEqual(
        Dictionary<string, byte[]> expected,
        Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<string, byte[]> kvp in expected)
        {
            Assert.True(actual.TryGetValue(kvp.Key, out byte[]? actualBytes),
                $"File '{kvp.Key}' missing from snapshot.");
            Assert.True(actualBytes.SequenceEqual(kvp.Value), $"File '{kvp.Key}' contents differed from snapshot.");
        }
    }
}

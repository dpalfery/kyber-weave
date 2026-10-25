using System.Security.Cryptography;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies Squad install and update lifecycle behaviors when applying, persisting, and diffing model presets.
/// Acceptance criteria:
/// - 2.1: Squad re-renders selected targets with preset through same transaction update uses.
/// - 2.2: Squad SHALL NOT modify a deployed file outside that transaction.
/// - 3.1: Squad SHALL record active preset per scope and target in Squad state.
/// - 3.2: WHEN update runs, Squad SHALL re-apply recorded preset.
/// - 3.4: WHEN recorded preset no longer exists, update SHALL fail before writing and name missing preset.
/// </summary>
public sealed class SquadModelPresetLifecycleTests : IDisposable
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

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public async Task InstallWithPresetWritesV2LockAndReceiptHashesMatchDeployedFiles()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "install-preset-root");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

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
            Assert.True(File.Exists(diskPath));
            byte[] diskBytes = await File.ReadAllBytesAsync(diskPath);
            string diskHash = Convert.ToHexStringLower(SHA256.HashData(diskBytes));
            Assert.Equal(file.Sha256, diskHash);
        }
    }

    [Fact]
    public async Task UpdateWithNullModelPresetsReappliesRecordedPresetToNewRelease()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "update-reapply-preset-root");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadInstallRequest installRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "fast" });

        SquadLifecycleResult installResult = await service.InstallAsync(installRequest);
        Assert.True(installResult.Success);

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

        Assert.Equal(2, renderer.RenderRequests.Count);
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? updatePresets = renderer.RenderRequests[1].ModelPresets;
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
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "update-partial-override-root");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        await WritePresetAsync(userPaths, "claude-fast", ClaudeFastPresetYaml);
        await WritePresetAsync(userPaths, "copilot-fast", FastPresetYaml);
        await WritePresetAsync(userPaths, "claude-quality", ClaudeQualityPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadInstallRequest installRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Claude, SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: new Dictionary<SquadTarget, string>
            {
                [SquadTarget.Claude] = "claude-fast",
                [SquadTarget.Copilot] = "copilot-fast"
            });

        SquadLifecycleResult installResult = await service.InstallAsync(installRequest);
        Assert.True(installResult.Success);

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
        Assert.Equal(2, renderer.RenderRequests.Count);
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? updatePresets = renderer.RenderRequests[1].ModelPresets;
        Assert.NotNull(updatePresets);
        Assert.Equal("claude-quality", updatePresets[SquadTarget.Claude].Name);
        Assert.Equal("copilot-fast", updatePresets[SquadTarget.Copilot].Name);
    }

    [Fact]
    public async Task UpdateWhenRecordedPresetMissingThrowsConflictExceptionAndAbortsBeforeDownload()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "update-missing-preset-root");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string presetPath = await WritePresetAsync(userPaths, "ephemeral", FastPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadInstallRequest installRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: new Dictionary<SquadTarget, string>
            {
                [SquadTarget.Copilot] = "ephemeral"
            });

        SquadLifecycleResult installResult = await service.InstallAsync(installRequest);
        Assert.True(installResult.Success);
        Assert.Single(releaseSource.Requests);

        // Record snapshots before deleting preset and running update
        Dictionary<string, byte[]> rootSnapshotBefore = CaptureDirectorySnapshot(targetRoot);
        Dictionary<string, byte[]> homeSnapshotBefore = CaptureDirectorySnapshot(userPaths.HomeDirectory);

        // Delete recorded preset file
        File.Delete(presetPath);
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
        Assert.Single(releaseSource.Requests);

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
        string targetRoot = Path.Combine(_temp.Path, "install-missing-preset-root");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

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
        Assert.Empty(Directory.GetFiles(targetRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InstallWithPresetReferencingUnknownCategoryThrowsSourceValidationExceptionBeforeWriting()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "unknown-category-root");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        await WritePresetAsync(userPaths, "bad-category", UnknownCategoryPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

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
        Assert.Empty(Directory.GetFiles(targetRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DryRunInstallWithPresetReturnsModelChangesAndWritesNothingToDisk()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "dry-run-install-root");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

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
        Assert.Empty(Directory.GetFiles(targetRoot, "*", SearchOption.AllDirectories));
        Assert.Null(stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Project));
        Assert.Null(stateStore.ReadLock(targetRoot, SquadDeploymentScope.Project));
    }

    [Fact]
    public async Task TwoDistinctProjectRootsMaintainIndependentPresetMaps()
    {
        // Arrange
        string projectRoot1 = Path.Combine(_temp.Path, "project-1");
        string projectRoot2 = Path.Combine(_temp.Path, "project-2");
        Directory.CreateDirectory(projectRoot1);
        Directory.CreateDirectory(projectRoot2);

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);
        await WritePresetAsync(userPaths, "quality", QualityPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        // Install Project 1 with "fast" preset
        SquadInstallRequest request1 = new(
            projectRoot1,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "fast" });

        SquadLifecycleResult result1 = await service.InstallAsync(request1);
        Assert.True(result1.Success);

        // Install Project 2 with "quality" preset
        SquadInstallRequest request2 = new(
            projectRoot2,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "quality" });

        SquadLifecycleResult result2 = await service.InstallAsync(request2);
        Assert.True(result2.Success);

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
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "update-clear-presets-root");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        await WritePresetAsync(userPaths, "fast", FastPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadInstallRequest installRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: new Dictionary<SquadTarget, string> { [SquadTarget.Copilot] = "fast" });

        SquadLifecycleResult installResult = await service.InstallAsync(installRequest);
        Assert.True(installResult.Success);

        // Act — update with empty map clears presets
        SquadUpdateRequest updateRequest = new(
            targetRoot,
            SquadDeploymentScope.Project,
            Version: "1.1.0",
            ModelPresets: new Dictionary<SquadTarget, string>());

        SquadLifecycleResult updateResult = await service.UpdateAsync(updateRequest);

        // Assert
        Assert.True(updateResult.Success);
        Assert.Equal(2, renderer.RenderRequests.Count);
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? updatePresets = renderer.RenderRequests[1].ModelPresets;
        Assert.True(updatePresets is null || updatePresets.Count == 0);
    }

    [Fact]
    public async Task InstallAsyncWithInstanceScopeAndMultipleTargetsThrowsArgumentException()
    {
        string targetRoot = Path.Combine(_temp.Path, "instance-multi-target-install");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

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
        string targetRoot = Path.Combine(_temp.Path, "instance-multi-target-update");
        Directory.CreateDirectory(targetRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadUpdateRequest request = new(
            targetRoot,
            SquadDeploymentScope.Instance,
            [SquadTarget.Claude, SquadTarget.Codex]);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(request));
        Assert.Contains("requires exactly one target", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> WritePresetAsync(ISquadUserPaths userPaths, string name, string yaml)
    {
        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        string presetPath = Path.Combine(presetsDir, $"{name}.yml");
        await File.WriteAllTextAsync(presetPath, yaml);
        return presetPath;
    }

    private static string ResolveTargetPath(string root, string relativePath)
    {
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static Dictionary<string, byte[]> CaptureDirectorySnapshot(string root)
    {
        Dictionary<string, byte[]> snapshot = new(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return snapshot;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            snapshot[file] = File.ReadAllBytes(file);
        }

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

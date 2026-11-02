using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Verifies Squad project model preset selection persistence, canonical path keying,
///     project tree isolation, and lifecycle interaction.
///     Acceptance criteria:
///     - 6.1: Squad SHALL store a project's preset selection under ~/.kyber-weave/, keyed by canonical path.
///     - 6.2: Squad SHALL NOT write a preset or selection file into the project's tree.
///     - 6.3: A project selection SHALL NOT change a global install.
/// </summary>
public sealed class SquadModelPresetSelectionTests : IDisposable
{
    private const string FastPresetYaml = """
                                          schema: kyber-squad.model-preset/v1
                                          profiles:
                                            general:
                                              default: gpt-4o-mini
                                              copilot: gpt-4o-mini
                                          """;

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void ReadReturnsNullWhenSelectionFileDoesNotExist()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "empty-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);

        // Act
        SquadModelPresetSelection? selection = store.Read(projectRoot);

        // Assert
        Assert.Null(selection);
    }

    [Fact]
    public void SetPersistsSelectionFileUnderUserHomeSelectionsDirectory()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "test-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);

        // Act
        store.Set(projectRoot, SquadTarget.Claude, "zai");

        // Assert
        string canonicalPath = SquadPhysicalRootIdentity.Resolve(projectRoot).PhysicalPath;
        string rootKey = SquadPhysicalRootIdentity.KeyOf(canonicalPath);
        string expectedFilePath =
            Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "selections", $"{rootKey}.yml");

        Assert.True(File.Exists(expectedFilePath), $"Expected selection file at '{expectedFilePath}'.");

        string content = File.ReadAllText(expectedFilePath);
        Assert.Contains("schema: kyber-squad.project-selection/v1", content, StringComparison.Ordinal);
        Assert.Contains($"root: {canonicalPath}", content, StringComparison.Ordinal);
        Assert.Contains("model-presets:", content, StringComparison.Ordinal);
        Assert.Contains("claude: zai", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadReturnsStoredSelectionWithConfiguredTargets()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "read-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);
        store.Set(projectRoot, SquadTarget.Claude, "zai");

        // Act
        SquadModelPresetSelection? selection = store.Read(projectRoot);

        // Assert
        Assert.NotNull(selection);
        string canonicalPath = SquadPhysicalRootIdentity.Resolve(projectRoot).PhysicalPath;
        Assert.Equal(canonicalPath, selection.Root);
        Assert.NotNull(selection.ModelPresets);
        Assert.True(selection.ModelPresets.TryGetValue(SquadTarget.Claude, out string? presetName));
        Assert.Equal("zai", presetName);
    }

    [Fact]
    public void SetMultipleTargetsPreservesAllPresetMappings()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "multi-target-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);

        // Act
        store.Set(projectRoot, SquadTarget.Claude, "zai");
        store.Set(projectRoot, SquadTarget.Copilot, "fast");
        SquadModelPresetSelection? selection = store.Read(projectRoot);

        // Assert
        Assert.NotNull(selection);
        Assert.Equal(2, selection.ModelPresets.Count);
        Assert.Equal("zai", selection.ModelPresets[SquadTarget.Claude]);
        Assert.Equal("fast", selection.ModelPresets[SquadTarget.Copilot]);
    }

    [Fact]
    public void SetOverwritesExistingTargetPresetWithoutAlteringOtherTargets()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "overwrite-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);
        store.Set(projectRoot, SquadTarget.Claude, "zai");
        store.Set(projectRoot, SquadTarget.Copilot, "fast");

        // Act
        store.Set(projectRoot, SquadTarget.Claude, "custom-claude");
        SquadModelPresetSelection? selection = store.Read(projectRoot);

        // Assert
        Assert.NotNull(selection);
        Assert.Equal(2, selection.ModelPresets.Count);
        Assert.Equal("custom-claude", selection.ModelPresets[SquadTarget.Claude]);
        Assert.Equal("fast", selection.ModelPresets[SquadTarget.Copilot]);
    }

    [Fact]
    public void ClearRemovesSpecifiedTargetFromSelection()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "clear-target-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);
        store.Set(projectRoot, SquadTarget.Claude, "zai");
        store.Set(projectRoot, SquadTarget.Copilot, "fast");

        // Act
        store.Clear(projectRoot, SquadTarget.Claude);
        SquadModelPresetSelection? selection = store.Read(projectRoot);

        // Assert
        Assert.NotNull(selection);
        Assert.Single(selection.ModelPresets);
        Assert.False(selection.ModelPresets.ContainsKey(SquadTarget.Claude));
        Assert.Equal("fast", selection.ModelPresets[SquadTarget.Copilot]);
    }

    [Fact]
    public void ClearDeletesSelectionFileWhenAllTargetsCleared()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "clear-all-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);
        store.Set(projectRoot, SquadTarget.Claude, "zai");

        string canonicalPath = SquadPhysicalRootIdentity.Resolve(projectRoot).PhysicalPath;
        string rootKey = SquadPhysicalRootIdentity.KeyOf(canonicalPath);
        string expectedFilePath =
            Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "selections", $"{rootKey}.yml");
        Assert.True(File.Exists(expectedFilePath));

        // Act
        store.Clear(projectRoot, SquadTarget.Claude);

        // Assert
        Assert.False(File.Exists(expectedFilePath), "Selection file should be deleted when selection becomes empty.");
        Assert.Null(store.Read(projectRoot));
    }

    [Fact]
    public void AtomicWritesDoNotLeaveTemporaryFilesOnSuccess()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "atomic-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);

        // Act
        store.Set(projectRoot, SquadTarget.Claude, "zai");

        // Assert
        string selectionsDirectory = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "selections");
        Assert.True(Directory.Exists(selectionsDirectory));

        string[] nonYmlFiles = Directory.GetFiles(selectionsDirectory, "*.*")
            .Where(static f => !f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(nonYmlFiles);
    }

    [Fact]
    public void SymlinkedAndCanonicalSpellingsResolveToIdenticalSelection()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string canonicalProjectRoot = Path.Combine(_temp.Path, "canonical-project");
        Directory.CreateDirectory(canonicalProjectRoot);

        string symlinkProjectRoot = Path.Combine(_temp.Path, "symlinked-project");
        Directory.CreateSymbolicLink(symlinkProjectRoot, canonicalProjectRoot);

        SquadModelPresetSelectionStore store = new(userPaths);

        // Act
        store.Set(canonicalProjectRoot, SquadTarget.Claude, "zai");
        SquadModelPresetSelection? fromSymlink = store.Read(symlinkProjectRoot);

        // Assert
        Assert.NotNull(fromSymlink);
        Assert.Equal("zai", fromSymlink.ModelPresets[SquadTarget.Claude]);

        string keyFromCanonical = SquadPhysicalRootIdentity.Resolve(canonicalProjectRoot).Key;
        string keyFromSymlink = SquadPhysicalRootIdentity.Resolve(symlinkProjectRoot).Key;
        Assert.Equal(keyFromCanonical, keyFromSymlink);
    }

    [Fact]
    public void SquadDoesNotWritePresetOrSelectionFileIntoProjectTree()
    {
        // Arrange (AC 6.2)
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "isolated-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);

        // Act
        store.Set(projectRoot, SquadTarget.Claude, "zai");
        _ = store.Read(projectRoot);
        store.Clear(projectRoot, SquadTarget.Claude);

        // Assert
        string[] filesInProject = Directory.GetFileSystemEntries(projectRoot, "*", SearchOption.AllDirectories);
        Assert.Empty(filesInProject);
    }

    [Fact]
    public async Task GlobalInstallIgnoresProjectSelection()
    {
        // Arrange (AC 6.3)
        string projectRoot = Path.Combine(_temp.Path, "global-install-project");
        Directory.CreateDirectory(projectRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));

        SquadModelPresetSelectionStore selectionStore = new(userPaths);
        selectionStore.Set(projectRoot, SquadTarget.Copilot, "fast");

        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        await File.WriteAllTextAsync(Path.Combine(presetsDir, "fast.yml"), FastPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore, selectionStore: selectionStore);

        SquadInstallRequest request = new(
            projectRoot,
            SquadDeploymentScope.Global,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: null);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Single(renderer.RenderRequests);
        Assert.Null(renderer.RenderRequests[0].ModelPresets);
    }

    [Fact]
    public async Task ProjectInstallWithNullModelPresetsAppliesStoredProjectSelection()
    {
        // Arrange (AC 6.3)
        string projectRoot = Path.Combine(_temp.Path, "project-install-selection");
        Directory.CreateDirectory(projectRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));

        SquadModelPresetSelectionStore selectionStore = new(userPaths);
        selectionStore.Set(projectRoot, SquadTarget.Copilot, "fast");

        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        await File.WriteAllTextAsync(Path.Combine(presetsDir, "fast.yml"), FastPresetYaml);

        SquadStateStore stateStore = new(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore, selectionStore: selectionStore);

        SquadInstallRequest request = new(
            projectRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: null);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Single(renderer.RenderRequests);
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? appliedPresets = renderer.RenderRequests[0].ModelPresets;
        Assert.NotNull(appliedPresets);
        Assert.True(appliedPresets.ContainsKey(SquadTarget.Copilot));
        Assert.Equal("fast", appliedPresets[SquadTarget.Copilot].Name);
    }

    [Theory]
    [InlineData("Invalid Name")]
    [InlineData("injected\n  root: bad")]
    [InlineData("-starts-with-hyphen")]
    public void SetRejectsInvalidPresetNamesWithArgumentException(string invalidPresetName)
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "test-invalid-preset");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);

        Assert.Throws<ArgumentException>(() => store.Set(projectRoot, SquadTarget.Claude, invalidPresetName));
    }

    [Fact]
    public void SetAndReadRoundTripsProjectPathWithYamlSpecialCharacters()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "project # with special characters");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = new(userPaths);

        store.Set(projectRoot, SquadTarget.Claude, "fast");
        SquadModelPresetSelection? selection = store.Read(projectRoot);

        Assert.NotNull(selection);
        string expectedPath = SquadPhysicalRootIdentity.Resolve(projectRoot).PhysicalPath;
        Assert.Equal(expectedPath, selection.Root);
        Assert.True(selection.ModelPresets.TryGetValue(SquadTarget.Claude, out string? presetName));
        Assert.Equal("fast", presetName);
    }
}

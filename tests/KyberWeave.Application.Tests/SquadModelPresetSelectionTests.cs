using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
///     Verifies Squad project model preset selection persistence, canonical path keying,
///     and project tree isolation, over the in-memory file system.
///     Acceptance criteria:
///     - 6.1: Squad SHALL store a project's preset selection under ~/.kyber-weave/, keyed by canonical path.
///     - 6.2: Squad SHALL NOT write a preset or selection file into the project's tree.
/// </summary>
public sealed class SquadModelPresetSelectionTests
{
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly FakeSquadUserPaths _userPaths;
    private readonly SquadFileSystemPathSemantics _semantics;
    private readonly SquadModelPresetSelectionStore _store;

    public SquadModelPresetSelectionTests()
    {
        _userPaths = new FakeSquadUserPaths(
            Path.Combine(_files.Root, "home", "data"),
            Path.Combine(_files.Root, "home", "dev"));
        _semantics = new SquadFileSystemPathSemantics(new FakePathResolver(_files));
        _store = new SquadModelPresetSelectionStore(_userPaths, _files, _semantics);
    }

    [Fact]
    public void ReadReturnsNullWhenSelectionFileDoesNotExist()
    {
        // Arrange
        string projectRoot = Path.Combine(_files.Root, "work", "empty-project");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;

        // Act
        SquadModelPresetSelection? selection = store.Read(projectRoot);

        // Assert
        Assert.Null(selection);
    }

    [Fact]
    public void SetPersistsSelectionFileUnderUserHomeSelectionsDirectory()
    {
        // Arrange
        string projectRoot = Path.Combine(_files.Root, "work", "test-project");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;

        // Act
        store.Set(projectRoot, SquadTarget.Claude, "zai");

        // Assert
        string canonicalPath = SquadPhysicalRootIdentities.Resolve(projectRoot, _semantics).PhysicalPath;
        string rootKey = SquadPhysicalRootIdentities.KeyOf(canonicalPath);
        string expectedFilePath =
            Path.Combine(_userPaths.HomeDirectory, ".kyber-weave", "squad", "selections", $"{rootKey}.yml");

        Assert.True(_files.FileExists(expectedFilePath), $"Expected selection file at '{expectedFilePath}'.");

        string content = _files.ReadAllText(expectedFilePath);
        Assert.Contains("schema: kyber-squad.project-selection/v1", content, StringComparison.Ordinal);
        Assert.Contains($"root: {canonicalPath}", content, StringComparison.Ordinal);
        Assert.Contains("model-presets:", content, StringComparison.Ordinal);
        Assert.Contains("claude: zai", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadReturnsStoredSelectionWithConfiguredTargets()
    {
        // Arrange
        string projectRoot = Path.Combine(_files.Root, "work", "read-project");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;
        store.Set(projectRoot, SquadTarget.Claude, "zai");

        // Act
        SquadModelPresetSelection? selection = store.Read(projectRoot);

        // Assert
        Assert.NotNull(selection);
        string canonicalPath = SquadPhysicalRootIdentities.Resolve(projectRoot, _semantics).PhysicalPath;
        Assert.Equal(canonicalPath, selection.Root);
        Assert.NotNull(selection.ModelPresets);
        Assert.True(selection.ModelPresets.TryGetValue(SquadTarget.Claude, out string? presetName));
        Assert.Equal("zai", presetName);
    }

    [Fact]
    public void SetMultipleTargetsPreservesAllPresetMappings()
    {
        // Arrange
        string projectRoot = Path.Combine(_files.Root, "work", "multi-target-project");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;

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
        string projectRoot = Path.Combine(_files.Root, "work", "overwrite-project");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;
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
        string projectRoot = Path.Combine(_files.Root, "work", "clear-target-project");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;
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
        string projectRoot = Path.Combine(_files.Root, "work", "clear-all-project");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;
        store.Set(projectRoot, SquadTarget.Claude, "zai");

        string canonicalPath = SquadPhysicalRootIdentities.Resolve(projectRoot, _semantics).PhysicalPath;
        string rootKey = SquadPhysicalRootIdentities.KeyOf(canonicalPath);
        string expectedFilePath =
            Path.Combine(_userPaths.HomeDirectory, ".kyber-weave", "squad", "selections", $"{rootKey}.yml");
        Assert.True(_files.FileExists(expectedFilePath));

        // Act
        store.Clear(projectRoot, SquadTarget.Claude);

        // Assert
        Assert.False(_files.FileExists(expectedFilePath), "Selection file should be deleted when selection becomes empty.");
        Assert.Null(store.Read(projectRoot));
    }

    [Fact]
    public void SquadDoesNotWritePresetOrSelectionFileIntoProjectTree()
    {
        // Arrange (AC 6.2)
        string projectRoot = Path.Combine(_files.Root, "work", "isolated-project");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;

        // Act
        store.Set(projectRoot, SquadTarget.Claude, "zai");
        _ = store.Read(projectRoot);
        store.Clear(projectRoot, SquadTarget.Claude);

        // Assert
        Assert.Empty(_files.EnumerateEntries(projectRoot));
    }

    [Theory]
    [InlineData("Invalid Name")]
    [InlineData("injected\n  root: bad")]
    [InlineData("-starts-with-hyphen")]
    public void SetRejectsInvalidPresetNamesWithArgumentException(string invalidPresetName)
    {
        string projectRoot = Path.Combine(_files.Root, "work", "test-invalid-preset");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;

        Assert.Throws<ArgumentException>(() => store.Set(projectRoot, SquadTarget.Claude, invalidPresetName));
    }

    [Fact]
    public void SetAndReadRoundTripsProjectPathWithYamlSpecialCharacters()
    {
        string projectRoot = Path.Combine(_files.Root, "work", "project # with special characters");
        _files.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = _store;

        store.Set(projectRoot, SquadTarget.Claude, "fast");
        SquadModelPresetSelection? selection = store.Read(projectRoot);

        Assert.NotNull(selection);
        string expectedPath = SquadPhysicalRootIdentities.Resolve(projectRoot, _semantics).PhysicalPath;
        Assert.Equal(expectedPath, selection.Root);
        Assert.True(selection.ModelPresets.TryGetValue(SquadTarget.Claude, out string? presetName));
        Assert.Equal("fast", presetName);
    }
}

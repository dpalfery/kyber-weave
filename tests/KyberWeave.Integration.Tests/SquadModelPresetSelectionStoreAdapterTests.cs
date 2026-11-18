using KyberWeave.Testing.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
///     Proves the selection store's physical adapter on a real disk: symbolic links resolve to one canonical
///     selection, and a write leaves no temporary file behind.
/// </summary>
public sealed class SquadModelPresetSelectionStoreAdapterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void AtomicWritesDoNotLeaveTemporaryFilesOnSuccess()
    {
        // Arrange
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));
        string projectRoot = Path.Combine(_temp.Path, "atomic-project");
        Directory.CreateDirectory(projectRoot);
        SquadModelPresetSelectionStore store = RealSquadSelectionStore.Create(userPaths);

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

        SquadModelPresetSelectionStore store = RealSquadSelectionStore.Create(userPaths);

        // Act
        store.Set(canonicalProjectRoot, SquadTarget.Claude, "zai");
        SquadModelPresetSelection? fromSymlink = store.Read(symlinkProjectRoot);

        // Assert
        Assert.NotNull(fromSymlink);
        Assert.Equal("zai", fromSymlink.ModelPresets[SquadTarget.Claude]);

        string keyFromCanonical = SquadPhysicalRootIdentities.Resolve(canonicalProjectRoot, SquadFileSystemPathSemantics.Instance).Key;
        string keyFromSymlink = SquadPhysicalRootIdentities.Resolve(symlinkProjectRoot, SquadFileSystemPathSemantics.Instance).Key;
        Assert.Equal(keyFromCanonical, keyFromSymlink);
    }
}

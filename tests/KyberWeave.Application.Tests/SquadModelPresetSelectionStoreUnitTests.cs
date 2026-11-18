using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Proves <see cref="SquadModelPresetSelectionStore"/> through its file-system and path seams alone: no test here
/// touches a real directory.
/// </summary>
public sealed class SquadModelPresetSelectionStoreUnitTests
{
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly string _home;
    private readonly string _project;
    private readonly SquadModelPresetSelectionStore _store;

    public SquadModelPresetSelectionStoreUnitTests()
    {
        _home = Path.Combine(_files.Root, "home", "dev");
        _project = Path.Combine(_files.Root, "work", "project");
        _files.CreateDirectory(_project);
        _store = new SquadModelPresetSelectionStore(
            new FakeSquadUserPaths(Path.Combine(_files.Root, "home", "data"), _home),
            _files,
            new SquadFileSystemPathSemantics(new FakePathResolver(_files)));
    }

    [Fact]
    public void ReadReturnsNullWhenNoSelectionWasStored()
    {
        Assert.Null(_store.Read(_project));
    }

    [Fact]
    public void SetThenReadRoundTripsThePresetUnderTheHomeSelectionsDirectory()
    {
        _store.Set(_project, SquadTarget.Claude, "zai");

        SquadModelPresetSelection? selection = _store.Read(_project);

        Assert.NotNull(selection);
        Assert.Equal("zai", selection.ModelPresets[SquadTarget.Claude]);
        string selectionsDirectory = Path.Combine(_home, ".kyber-weave", "squad", "selections");
        string file = Assert.Single(_files.EnumerateFiles(selectionsDirectory, "*.yml", recursive: false));
        Assert.StartsWith(selectionsDirectory, file, StringComparison.Ordinal);
    }

    [Fact]
    public void SetLeavesNoTemporaryFileBehind()
    {
        _store.Set(_project, SquadTarget.Claude, "zai");

        string selectionsDirectory = Path.Combine(_home, ".kyber-weave", "squad", "selections");
        Assert.Empty(_files.EnumerateFiles(selectionsDirectory, "*.tmp", recursive: false));
    }

    [Fact]
    public void ClearOfTheLastTargetDeletesTheSelectionFile()
    {
        _store.Set(_project, SquadTarget.Claude, "zai");

        _store.Clear(_project, SquadTarget.Claude);

        Assert.Null(_store.Read(_project));
        string selectionsDirectory = Path.Combine(_home, ".kyber-weave", "squad", "selections");
        Assert.Empty(_files.EnumerateFiles(selectionsDirectory, "*.yml", recursive: false));
    }

    [Fact]
    public void ClearOfOneTargetKeepsTheOthers()
    {
        _store.Set(_project, SquadTarget.Claude, "zai");
        _store.Set(_project, SquadTarget.Codex, "fast");

        _store.Clear(_project, SquadTarget.Claude);

        SquadModelPresetSelection? selection = _store.Read(_project);
        Assert.NotNull(selection);
        Assert.Single(selection.ModelPresets);
        Assert.Equal("fast", selection.ModelPresets[SquadTarget.Codex]);
    }

    [Fact]
    public void ClearOfTheProjectDeletesEverySelection()
    {
        _store.Set(_project, SquadTarget.Claude, "zai");
        _store.Set(_project, SquadTarget.Codex, "fast");

        _store.Clear(_project);

        Assert.Null(_store.Read(_project));
    }

    [Fact]
    public void ReadRejectsAnInvalidSelectionFile()
    {
        _store.Set(_project, SquadTarget.Claude, "zai");
        string selectionsDirectory = Path.Combine(_home, ".kyber-weave", "squad", "selections");
        string file = Assert.Single(_files.EnumerateFiles(selectionsDirectory, "*.yml", recursive: false));
        _files.WriteAllText(file, "schema: [unterminated");

        Assert.Throws<InvalidDataException>(() => _store.Read(_project));
    }

    [Fact]
    public void SetRejectsAPresetNameThatIsNotAToken()
    {
        Assert.Throws<ArgumentException>(() => _store.Set(_project, SquadTarget.Claude, "Not A Token"));
    }
}

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
    private const string Home = "/home/dev";
    private const string Project = "/work/project";

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly SquadModelPresetSelectionStore _store;

    public SquadModelPresetSelectionStoreUnitTests()
    {
        _files.CreateDirectory(Project);
        _store = new SquadModelPresetSelectionStore(
            new FakeSquadUserPaths("/home/data", Home),
            _files,
            new SquadFileSystemPathSemantics(new FakePathResolver(_files)));
    }

    [Fact]
    public void ReadReturnsNullWhenNoSelectionWasStored()
    {
        Assert.Null(_store.Read(Project));
    }

    [Fact]
    public void SetThenReadRoundTripsThePresetUnderTheHomeSelectionsDirectory()
    {
        _store.Set(Project, SquadTarget.Claude, "zai");

        SquadModelPresetSelection? selection = _store.Read(Project);

        Assert.NotNull(selection);
        Assert.Equal("zai", selection.ModelPresets[SquadTarget.Claude]);
        string selectionsDirectory = Path.Combine(Home, ".kyber-weave", "squad", "selections");
        string file = Assert.Single(_files.EnumerateFiles(selectionsDirectory, "*.yml", recursive: false));
        Assert.StartsWith(selectionsDirectory, file, StringComparison.Ordinal);
    }

    [Fact]
    public void SetLeavesNoTemporaryFileBehind()
    {
        _store.Set(Project, SquadTarget.Claude, "zai");

        string selectionsDirectory = Path.Combine(Home, ".kyber-weave", "squad", "selections");
        Assert.Empty(_files.EnumerateFiles(selectionsDirectory, "*.tmp", recursive: false));
    }

    [Fact]
    public void ClearOfTheLastTargetDeletesTheSelectionFile()
    {
        _store.Set(Project, SquadTarget.Claude, "zai");

        _store.Clear(Project, SquadTarget.Claude);

        Assert.Null(_store.Read(Project));
        string selectionsDirectory = Path.Combine(Home, ".kyber-weave", "squad", "selections");
        Assert.Empty(_files.EnumerateFiles(selectionsDirectory, "*.yml", recursive: false));
    }

    [Fact]
    public void ClearOfOneTargetKeepsTheOthers()
    {
        _store.Set(Project, SquadTarget.Claude, "zai");
        _store.Set(Project, SquadTarget.Codex, "fast");

        _store.Clear(Project, SquadTarget.Claude);

        SquadModelPresetSelection? selection = _store.Read(Project);
        Assert.NotNull(selection);
        Assert.Single(selection.ModelPresets);
        Assert.Equal("fast", selection.ModelPresets[SquadTarget.Codex]);
    }

    [Fact]
    public void ClearOfTheProjectDeletesEverySelection()
    {
        _store.Set(Project, SquadTarget.Claude, "zai");
        _store.Set(Project, SquadTarget.Codex, "fast");

        _store.Clear(Project);

        Assert.Null(_store.Read(Project));
    }

    [Fact]
    public void ReadRejectsAnInvalidSelectionFile()
    {
        _store.Set(Project, SquadTarget.Claude, "zai");
        string selectionsDirectory = Path.Combine(Home, ".kyber-weave", "squad", "selections");
        string file = Assert.Single(_files.EnumerateFiles(selectionsDirectory, "*.yml", recursive: false));
        _files.WriteAllText(file, "schema: [unterminated");

        Assert.Throws<InvalidDataException>(() => _store.Read(Project));
    }

    [Fact]
    public void SetRejectsAPresetNameThatIsNotAToken()
    {
        Assert.Throws<ArgumentException>(() => _store.Set(Project, SquadTarget.Claude, "Not A Token"));
    }
}

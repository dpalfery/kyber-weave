using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Review;

/// <summary>The evidence store writes a file whole through a sibling temporary, over an in-memory file system.</summary>
public sealed class FileReviewEvidenceStoreTests
{
    private static readonly string Folder = Path.GetFullPath(Path.Combine(new InMemoryFileSystem().RootPath, "evidence"));
    private static readonly string Target = Path.Combine(Folder, "round-1.md");

    private readonly InMemoryFileSystem _files = new();
    private readonly FileReviewEvidenceStore _store;

    public FileReviewEvidenceStoreTests() => _store = new FileReviewEvidenceStore(_files);

    [Fact]
    public void EnsuringAFolderCreatesItAndItsParents()
    {
        string nested = Path.Combine(Folder, "a", "b");

        _store.EnsureFolder(nested);

        Assert.True(_files.DirectoryExists(nested));
    }

    [Fact]
    public void EnsuringAFolderThatExistsKeepsItsFiles()
    {
        _files.CreateDirectory(Folder);
        _files.WriteAllText(Target, "kept");

        _store.EnsureFolder(Folder);

        Assert.Equal("kept", _files.ReadAllText(Target));
    }

    [Fact]
    public void WritingCreatesTheFileAndLeavesNoTemporary()
    {
        _store.EnsureFolder(Folder);

        _store.WriteAtomically(Target, "evidence");

        Assert.Equal("evidence", _files.ReadAllText(Target));
        Assert.False(_files.FileExists(Target + ".tmp"));
    }

    [Fact]
    public void WritingReplacesEarlierText()
    {
        _store.EnsureFolder(Folder);
        _store.WriteAtomically(Target, "first");

        _store.WriteAtomically(Target, "second");

        Assert.Equal("second", _files.ReadAllText(Target));
    }

    [Fact]
    public void AFolderThatIsMissingFailsTheWrite() =>
        Assert.ThrowsAny<IOException>(() => _store.WriteAtomically(Target, "evidence"));

    [Fact]
    public void AFailedMoveLeavesTheEarlierFileWhole()
    {
        _store.EnsureFolder(Folder);
        _store.WriteAtomically(Target, "first");
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.MoveFile));

        Assert.ThrowsAny<Exception>(() => _store.WriteAtomically(Target, "second"));

        Assert.Equal("first", _files.ReadAllText(Target));
    }
}

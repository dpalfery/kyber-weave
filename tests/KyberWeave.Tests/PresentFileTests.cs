using KyberWeave.Core.Docs.Closeout;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A file that is there or not is read in one step, so "absent" and the file's text never come from two moments, and a
/// file that is there but cannot be read is never mistaken for an absent one.
/// </summary>
public sealed class PresentFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AFileThatIsThereReadsAsItsText()
    {
        string path = Path.Combine(_temp.Path, "a.json");
        File.WriteAllText(path, "{}");

        Assert.Equal("{}", PresentFile.ReadIfPresent(path));
    }

    [Fact]
    public void AnEmptyFileReadsAsEmptyNotAbsent()
    {
        string path = Path.Combine(_temp.Path, "empty.json");
        File.WriteAllText(path, string.Empty);

        Assert.Equal(string.Empty, PresentFile.ReadIfPresent(path));
    }

    [Fact]
    public void AMissingFileAndAMissingFolderReadAsAbsent()
    {
        Assert.Null(PresentFile.ReadIfPresent(Path.Combine(_temp.Path, "none.json")));
        Assert.Null(PresentFile.ReadIfPresent(Path.Combine(_temp.Path, "no-folder", "none.json")));
    }

    [Fact]
    public void AFolderInThePlaceOfTheFileIsAFailureNotAnAbsence()
    {
        string path = Path.Combine(_temp.Path, "dir.json");
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<Exception>(() => PresentFile.ReadIfPresent(path));
    }
}

using Xunit;

namespace KyberWeave.Application.Tests.Hooks;

/// <summary>The in-memory read fake keeps the failure types the file-system seam documents for a missing path.</summary>
public sealed class FakeFileSystemTests
{
    [Fact]
    public void ReadsOfAMissingFileThrowFileNotFound()
    {
        FakeFileSystem files = new();
        string missing = FakeFileSystem.Abs("absent.txt");

        _ = Assert.Throws<FileNotFoundException>(() => files.ReadAllText(missing));
        _ = Assert.Throws<FileNotFoundException>(() => files.ReadAllBytes(missing));
        _ = Assert.Throws<FileNotFoundException>(() => files.GetLength(missing));
        _ = Assert.Throws<FileNotFoundException>(() => files.GetAttributes(missing));
    }

    [Fact]
    public void ReadLinesDefersTheFailureUntilEnumeration()
    {
        FakeFileSystem files = new();
        IEnumerable<string> lines = files.ReadLines(FakeFileSystem.Abs("absent.txt"));

        _ = Assert.Throws<FileNotFoundException>(() => lines.ToList());
    }
}

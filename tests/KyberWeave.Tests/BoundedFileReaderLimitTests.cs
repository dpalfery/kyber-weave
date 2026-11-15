using System.Text;
using KyberWeave.Core.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The bound is validated up front: a limit that would overflow the read is refused with the right exception.</summary>
public sealed class BoundedFileReaderLimitTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(BoundedFileReader.MaxLimitBytes + 1)]
    public void ALimitOutsideTheSupportedRangeIsRefusedForAStreamAndForAFile(long limit)
    {
        string path = Path.Combine(_temp.Path, "a.txt");
        File.WriteAllText(path, "abc");
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("abc"));

        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedFileReader.ReadAllText(stream, limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedFileReader.ReadAllText(path, limit));
    }

    [Fact]
    public void TheLargestSupportedLimitAndZeroStillRead()
    {
        string path = Path.Combine(_temp.Path, "a.txt");
        File.WriteAllText(path, "abc");
        string empty = Path.Combine(_temp.Path, "empty.txt");
        File.WriteAllText(empty, string.Empty);

        Assert.Equal("abc", BoundedFileReader.ReadAllText(path, BoundedFileReader.MaxLimitBytes));
        Assert.Equal(string.Empty, BoundedFileReader.ReadAllText(empty, 0));
        Assert.Throws<InvalidDataException>(() => BoundedFileReader.ReadAllText(path, 0));
    }
}

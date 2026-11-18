using System.Security;
using KyberWeave.Core.FileSystem;
using Xunit;

namespace KyberWeave.Kernel.Tests;

/// <summary>
/// Every catch around a file read, stat or enumeration answers "could not be read" for the whole set of file-system
/// failures (<see cref="FileFailure"/>), not a hand-picked part of it.
/// </summary>
public sealed class FileFailureGuardTests
{
    [Fact]
    public void TheFilterTakesEveryFileSystemFailureAndNothingElse()
    {
        Exception[] failures =
        [
            new IOException(), new UnauthorizedAccessException(), new SecurityException(),
            new ArgumentException("path"), new NotSupportedException(), new PathTooLongException(),
        ];

        Assert.All(failures, failure => Assert.True(FileFailure.Is(failure), failure.GetType().Name));
        Assert.False(FileFailure.Is(new InvalidOperationException()));
        Assert.False(FileFailure.Is(new InvalidDataException()));
    }

    [Fact]
    public void ABadReadLimitIsAProgrammingErrorNotAFileFailure()
    {
        string path = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "any.txt");

        ArgumentOutOfRangeException negative = Assert.Throws<ArgumentOutOfRangeException>(() => BoundedFileReader.ReadAllText(path, -1));
        ArgumentOutOfRangeException tooLarge = Assert.Throws<ArgumentOutOfRangeException>(
            () => BoundedFileReader.ReadAllText(path, BoundedFileReader.MaxLimitBytes + 1));

        Assert.False(FileFailure.Is(negative));
        Assert.False(FileFailure.Is(tooLarge));
        Assert.True(FileFailure.Is(new ArgumentNullException("path")));
    }
}

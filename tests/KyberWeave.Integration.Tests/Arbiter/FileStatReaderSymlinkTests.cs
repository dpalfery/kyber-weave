using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Integration.Tests.Arbiter;

/// <summary>
/// The Arbiter's file stats answer for the file a symbolic link names, not the link. On Linux and macOS a
/// link's own length is the length of its target path, so measuring the link would let an oversized document
/// pass the size cap and would make the in-flight ledger read from the wrong offset.
/// </summary>
public sealed class FileStatReaderSymlinkTests : IDisposable
{
    private const string Content = "twenty-six bytes of text!\n";

    private readonly TempDirectory _temp = new();
    private readonly FileStatReader _stats = new();

    /// <summary>Deletes the temporary folder.</summary>
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void GetLengthOnALinkReturnsTheTargetsLength()
    {
        SkipOnWindows();
        string link = LinkToTarget(out _);

        Assert.Equal(Content.Length, _stats.GetLength(link));
    }

    [Fact]
    public void GetLengthOnALinkToALinkReturnsTheFinalTargetsLength()
    {
        SkipOnWindows();
        string first = LinkToTarget(out _);
        string second = Path.Combine(_temp.Path, "second-link.md");
        File.CreateSymbolicLink(second, first);

        Assert.Equal(Content.Length, _stats.GetLength(second));
    }

    [Fact]
    public void GetLengthOnABrokenLinkThrowsFileNotFoundLikeAMissingFile()
    {
        SkipOnWindows();
        string link = Path.Combine(_temp.Path, "broken-link.md");
        File.CreateSymbolicLink(link, Path.Combine(_temp.Path, "no-such-file.md"));

        Assert.Throws<FileNotFoundException>(() => _stats.GetLength(link));
    }

    [Fact]
    public void GetLastWriteTimeUtcOnALinkReturnsTheTargetsTime()
    {
        SkipOnWindows();
        string link = LinkToTarget(out string target);
        DateTime targetTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, targetTime);

        Assert.Equal(targetTime, _stats.GetLastWriteTimeUtc(link));
    }

    [Fact]
    public void GetLastWriteTimeUtcOnABrokenLinkReturnsTheEpochLikeAMissingFile()
    {
        SkipOnWindows();
        string link = Path.Combine(_temp.Path, "broken-link.md");
        File.CreateSymbolicLink(link, Path.Combine(_temp.Path, "no-such-file.md"));

        Assert.Equal(DateTime.FromFileTimeUtc(0), _stats.GetLastWriteTimeUtc(link));
    }

    [Fact]
    public void GetLengthOnALinkLoopThrowsIOException()
    {
        SkipOnWindows();

        Assert.ThrowsAny<IOException>(() => _stats.GetLength(LinkLoop()));
    }

    [Fact]
    public void GetLastWriteTimeUtcOnALinkLoopThrowsIOException()
    {
        SkipOnWindows();

        Assert.ThrowsAny<IOException>(() => _stats.GetLastWriteTimeUtc(LinkLoop()));
    }

    [Fact]
    public void ArbiterLogByteLengthOnALinkReturnsTheTargetsLength()
    {
        SkipOnWindows();
        string link = LinkToTarget(out _);

        Assert.Equal(Content.Length, new ArbiterLogFiles().ByteLength(link));
    }

    [Fact]
    public void ArbiterLogByteLengthOnABrokenLinkIsZeroLikeAMissingLog()
    {
        SkipOnWindows();
        string link = Path.Combine(_temp.Path, "broken-link.jsonl");
        File.CreateSymbolicLink(link, Path.Combine(_temp.Path, "no-such-log.jsonl"));

        Assert.Equal(0, new ArbiterLogFiles().ByteLength(link));
    }

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symbolic link needs a privilege Windows does not grant by default.");
        }
    }

    private string LinkLoop()
    {
        string first = Path.Combine(_temp.Path, "loop-a.md");
        string second = Path.Combine(_temp.Path, "loop-b.md");
        File.CreateSymbolicLink(first, second);
        File.CreateSymbolicLink(second, first);
        return first;
    }

    /// <summary>Writes the target file under a long name, so the link's own length differs from the target's.</summary>
    private string LinkToTarget(out string target)
    {
        target = Path.Combine(_temp.Path, "a-target-file-whose-path-is-much-longer-than-its-content.md");
        File.WriteAllText(target, Content);
        string link = Path.Combine(_temp.Path, "link.md");
        File.CreateSymbolicLink(link, target);
        return link;
    }
}

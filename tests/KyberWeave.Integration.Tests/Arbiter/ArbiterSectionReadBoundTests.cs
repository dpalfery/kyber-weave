using System.Text;
using KyberWeave.Core.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 4538775151 and the later one about a second bounded-read idiom: every bounded read of a file goes
/// through <see cref="BoundedFileReader"/>, which counts the bytes actually read, so a file swapped for a larger one
/// after a size check, or one that reports a smaller size than it holds, is refused and never read whole.
/// </summary>
public sealed class ArbiterSectionReadBoundTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void BoundedRead_FileOneByteOverTheLimit_IsRefusedNotRead()
    {
        const long limit = 64;
        string path = Path.Combine(_temp.Path, "over.md");
        File.WriteAllText(path, new string('a', (int)limit + 1));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => BoundedFileReader.ReadAllText(path, limit));

        Assert.Contains((limit + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedRead_FileExactlyAtTheLimit_IsRead()
    {
        const long limit = 64;
        string path = Path.Combine(_temp.Path, "at.md");
        File.WriteAllText(path, new string('a', (int)limit));

        string text = BoundedFileReader.ReadAllText(path, limit);

        Assert.Equal((int)limit, text.Length);
    }

    [Fact]
    public void BoundedRead_StreamThatReportsASmallerLengthThanItHolds_IsStillRefused()
    {
        const long limit = 64;
        using LyingLengthStream grown = new(new byte[limit + 10]);

        Assert.Throws<InvalidDataException>(() => BoundedFileReader.ReadAllText(grown, limit));
    }

    [Fact]
    public void BoundedRead_StreamThatReportsASmallerLengthButStaysUnderTheLimit_IsRead()
    {
        const long limit = 64;
        using LyingLengthStream stream = new(Encoding.UTF8.GetBytes(new string('a', (int)limit)));

        Assert.Equal((int)limit, BoundedFileReader.ReadAllText(stream, limit).Length);
    }

    [Fact]
    public void BoundedRead_ByteOrderMark_IsNotPartOfTheText()
    {
        string path = Path.Combine(_temp.Path, "bom.md");
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("hello")]);

        Assert.Equal("hello", BoundedFileReader.ReadAllText(path, 64));
    }

    [Fact]
    public void BoundedLines_SplitLikeReadAllLinesAndRefuseAnOversizedFile()
    {
        string path = Path.Combine(_temp.Path, "lines.md");
        File.WriteAllText(path, "one\r\ntwo\nthree\rfour");

        Assert.Equal(File.ReadAllLines(path), BoundedFileReader.ReadAllLines(path, 64));
        Assert.Throws<InvalidDataException>(() => BoundedFileReader.ReadAllLines(path, 5));
    }

    /// <summary>A stream that holds more than its <see cref="Length"/> says, as a file does that grows after it was measured.</summary>
    private sealed class LyingLengthStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

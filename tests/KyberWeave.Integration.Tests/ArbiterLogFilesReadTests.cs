using System.Globalization;
using System.Text;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;
using KyberWeave.Tests.Arbiter;

namespace KyberWeave.Tests;

/// <summary>
/// The lock-free reader of the Arbiter's append-only logs. A log that is not there yet, or whose folder is not, reads
/// as empty; a writer mid-line is never read as a record.
/// </summary>
public sealed class ArbiterLogFilesReadTests : IDisposable
{
    private static readonly ArbiterLogFiles Logs = new();
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AFileThatIsNotThereReadsAsNoLines()
    {
        Directory.CreateDirectory(_temp.Path);

        Assert.Empty(Logs.ReadCompleteLines(Path.Combine(_temp.Path, "ledger.jsonl")));
    }

    [Fact]
    public void AFolderThatIsNotThereReadsAsNoLines()
    {
        Assert.Empty(Logs.ReadCompleteLines(Path.Combine(_temp.Path, "no-such-folder", "ledger.jsonl")));
    }

    [Fact]
    public void AnEmptyFileReadsAsNoLines()
    {
        string path = Path.Combine(_temp.Path, "ledger.jsonl");
        File.WriteAllText(path, string.Empty);

        Assert.Empty(Logs.ReadCompleteLines(path));
    }

    [Fact]
    public void OnlyCompleteLinesAreReadAndATornFinalLineIsDropped()
    {
        string path = Path.Combine(_temp.Path, "ledger.jsonl");
        File.WriteAllText(path, "{\"a\":1}\n{\"b\":2}\n{\"c\":");

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], Logs.ReadCompleteLines(path));
    }

    [Fact]
    public void OnlyTheWindowAtTheEndOfAnOversizedLogIsReadAndEveryLineInItIsWhole()
    {
        string path = Path.Combine(_temp.Path, "ledger.jsonl");
        const int lineBytes = 1000;
        long lines = (ArbiterLogFiles.MaxReadBytes / (lineBytes + 1)) + 200;
        using (StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            for (long i = 0; i < lines; i++)
            {
                writer.Write(i.ToString("D8", CultureInfo.InvariantCulture).PadRight(lineBytes, 'x'));
                writer.Write('\n');
            }
        }

        long length = new FileInfo(path).Length;
        long firstKept = (long)Math.Ceiling((length - ArbiterLogFiles.MaxReadBytes) / (double)(lineBytes + 1));

        IReadOnlyList<string> read = Logs.ReadCompleteLines(path);

        Assert.Equal(lines - firstKept, read.Count);
        Assert.StartsWith(firstKept.ToString("D8", CultureInfo.InvariantCulture), read[0], StringComparison.Ordinal);
        Assert.StartsWith((lines - 1).ToString("D8", CultureInfo.InvariantCulture), read[^1], StringComparison.Ordinal);
        Assert.All(read, line => Assert.Equal(lineBytes, line.Length));
    }

    [Fact]
    public void AWindowThatOpensInsideAMultiByteCharacterDropsOnlyThatFragmentLine()
    {
        string path = Path.Combine(_temp.Path, "ledger.jsonl");
        File.WriteAllText(path, "first\n\u00e9\u00e9\u00e9\u00e9\nsecond\nthird\n");
        long length = new FileInfo(path).Length;

        // The window opens one byte into the second line's first two-byte character.
        IReadOnlyList<string> read = Logs.ReadTailLines(path, length - "first\n".Length - 1, keepUnterminatedFinalLine: false);

        Assert.Equal(["second", "third"], read);
    }

    [Fact]
    public void ALogThatCannotBeOpenedForReadingReadsAsNoLinesInsteadOfThrowing()
    {
        // A folder where the file belongs fails to open with an access error, the way a file the user may not read does.
        string path = Path.Combine(_temp.Path, InFlightLedger.FileName);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(_temp.Path, DecisionLog.FileName));

        Assert.Empty(Logs.ReadCompleteLines(path));
        Assert.Empty(RealArbiterState.Ledger(_temp.Path).ReadAll());
        Assert.Empty(RealArbiterState.Decisions(_temp.Path).ReadAll());
    }

    [Fact]
    public void TheLengthIsReadOnceSoAFileThatShrinksWhileReadingNeverSeeksBeforeItsStart()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("one\ntwo\nthree\nfour\n");
        using ShrinkingLengthStream stream = new(bytes);

        IReadOnlyList<string> read = Logs.ReadTailLines(stream, 11, keepUnterminatedFinalLine: false);

        Assert.Equal(["three", "four"], read);
        Assert.Equal(1, stream.LengthReads);
    }

    [Fact]
    public void AWindowIsReadInBoundedChunksNotInOneAllocationTheSizeOfTheWindow()
    {
        // Lines that straddle the 64 KiB chunk boundary must still come back whole and in order.
        string[] expected = [.. Enumerable.Range(0, 40).Select(i => i.ToString("D2", CultureInfo.InvariantCulture).PadRight(50_000, 'y'))];
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\n', expected) + "\n");
        using RecordingStream stream = new(bytes);

        IReadOnlyList<string> read = Logs.ReadTailLines(stream, bytes.Length, keepUnterminatedFinalLine: false);

        Assert.Equal(expected, read);
        Assert.InRange(stream.LargestRequest, 1, 64 * 1024);
    }

    [Fact]
    public void AnUnterminatedFinalLineIsKeptOnlyWhenAskedFor()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("one\ntwo\nthr");

        Assert.Equal(["one", "two"], Logs.ReadTailLines(new MemoryStream(bytes), 100, keepUnterminatedFinalLine: false));
        Assert.Equal(["one", "two", "thr"], Logs.ReadTailLines(new MemoryStream(bytes), 100, keepUnterminatedFinalLine: true));
    }

    [Fact]
    public void AWindowThatOpensMidLineNeverKeepsTheFragmentEvenWhenItIsTheOnlyLine()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("abcdefghij\n");

        Assert.Empty(Logs.ReadTailLines(new MemoryStream(bytes), 5, keepUnterminatedFinalLine: true));
    }

    /// <summary>A stream that records the largest buffer any read was offered.</summary>
    private sealed class RecordingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int LargestRequest { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRequest = Math.Max(LargestRequest, count);
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            return base.Read(buffer);
        }
    }

    /// <summary>A stream whose length reads as 0 after the first read, as a file truncated between two reads would.</summary>
    private sealed class ShrinkingLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int LengthReads { get; private set; }

        public override long Length => ++LengthReads == 1 ? base.Length : 0;
    }
}

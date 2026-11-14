using System.Globalization;
using System.Text;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The lock-free reader of the Arbiter's append-only logs. A log that is not there yet, or whose folder is not, reads
/// as empty; a writer mid-line is never read as a record.
/// </summary>
public sealed class ArbiterLogFilesReadTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AFileThatIsNotThereReadsAsNoLines()
    {
        Directory.CreateDirectory(_temp.Path);

        Assert.Empty(ArbiterLogFiles.ReadCompleteLines(Path.Combine(_temp.Path, "ledger.jsonl")));
    }

    [Fact]
    public void AFolderThatIsNotThereReadsAsNoLines()
    {
        Assert.Empty(ArbiterLogFiles.ReadCompleteLines(Path.Combine(_temp.Path, "no-such-folder", "ledger.jsonl")));
    }

    [Fact]
    public void AnEmptyFileReadsAsNoLines()
    {
        string path = Path.Combine(_temp.Path, "ledger.jsonl");
        File.WriteAllText(path, string.Empty);

        Assert.Empty(ArbiterLogFiles.ReadCompleteLines(path));
    }

    [Fact]
    public void OnlyCompleteLinesAreReadAndATornFinalLineIsDropped()
    {
        string path = Path.Combine(_temp.Path, "ledger.jsonl");
        File.WriteAllText(path, "{\"a\":1}\n{\"b\":2}\n{\"c\":");

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], ArbiterLogFiles.ReadCompleteLines(path));
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

        IReadOnlyList<string> read = ArbiterLogFiles.ReadCompleteLines(path);

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
        IReadOnlyList<string> read = ArbiterLogFiles.ReadTailLines(path, length - "first\n".Length - 1, keepUnterminatedFinalLine: false);

        Assert.Equal(["second", "third"], read);
    }

    [Fact]
    public void ALogThatCannotBeOpenedForReadingReadsAsNoLinesInsteadOfThrowing()
    {
        // A folder where the file belongs fails to open with an access error, the way a file the user may not read does.
        string path = Path.Combine(_temp.Path, InFlightLedger.FileName);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(_temp.Path, DecisionLog.FileName));

        Assert.Empty(ArbiterLogFiles.ReadCompleteLines(path));
        Assert.Empty(new InFlightLedger(_temp.Path).ReadAll());
        Assert.Empty(new DecisionLog(_temp.Path).ReadAll());
    }

    [Fact]
    public void TheLengthIsReadOnceSoAFileThatShrinksWhileReadingNeverSeeksBeforeItsStart()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("one\ntwo\nthree\nfour\n");
        using ShrinkingLengthStream stream = new(bytes);

        IReadOnlyList<string> read = ArbiterLogFiles.ReadTailLines(stream, 11, keepUnterminatedFinalLine: false);

        Assert.Equal(["three", "four"], read);
        Assert.Equal(1, stream.LengthReads);
    }

    /// <summary>A stream whose length reads as 0 after the first read, as a file truncated between two reads would.</summary>
    private sealed class ShrinkingLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int LengthReads { get; private set; }

        public override long Length => ++LengthReads == 1 ? base.Length : 0;
    }
}

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
}

using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>Proves the worker baseline reader and its per-instance stamp cache through the in-memory file system.</summary>
public sealed class SquadWorkerBaselineFileTests
{
    private static readonly string Source = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "baseline-src");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private SquadWorkerBaselineFile NewReader() =>
        new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: Source));

    private string BaselinePath => Path.Combine(
        Source, SquadWorkerBaselineInjector.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteBaseline(string text)
    {
        _files.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
        _files.WriteAllText(BaselinePath, text);
    }

    [Fact]
    public void ATreeWithoutABaselineFileReadsAsNull()
    {
        _files.CreateDirectory(Source);
        SquadWorkerBaselineFile reader = NewReader();

        Assert.Null(reader.ReadBlock(Source));
        Assert.Null(reader.ReadBlock(Source));
    }

    [Fact]
    public void ATreeWithABaselineFileReadsItsBlock()
    {
        WriteBaseline("intro\n<worker-baseline>\nworker text\n</worker-baseline>\n");

        string? block = NewReader().ReadBlock(Source);

        Assert.NotNull(block);
        Assert.Contains("worker text", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ABaselineAddedAfterAMissReadsOnTheNextCall()
    {
        _files.CreateDirectory(Source);
        SquadWorkerBaselineFile reader = NewReader();
        Assert.Null(reader.ReadBlock(Source));

        WriteBaseline("<worker-baseline>\nworker text\n</worker-baseline>");

        Assert.Contains("worker text", reader.ReadBlock(Source), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnchangedFileIsServedFromTheCacheWithoutReadingItAgain()
    {
        WriteBaseline("<worker-baseline>\nworker text\n</worker-baseline>");
        SquadWorkerBaselineFile reader = NewReader();
        string? first = reader.ReadBlock(Source);
        _files.FailWhen(operation => operation.Name == "ReadAllText", new IOException("must not be read again"));

        string? second = reader.ReadBlock(Source);

        Assert.Equal(first, second);
    }

    [Fact]
    public void AChangedFileIsReadAgain()
    {
        WriteBaseline("<worker-baseline>\nfirst text\n</worker-baseline>");
        SquadWorkerBaselineFile reader = NewReader();
        Assert.Contains("first text", reader.ReadBlock(Source), StringComparison.Ordinal);

        WriteBaseline("<worker-baseline>\nsecond, longer text\n</worker-baseline>");

        Assert.Contains("second, longer text", reader.ReadBlock(Source), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoReadersDoNotShareACache()
    {
        WriteBaseline("<worker-baseline>\nworker text\n</worker-baseline>");
        NewReader().ReadBlock(Source);
        _files.FailWhen(operation => operation.Name == "ReadAllText", new IOException("a new reader must read the file"));

        Assert.Throws<IOException>(() => NewReader().ReadBlock(Source));
    }

    [Fact]
    public void ANestedBlockOpenIsRefusedAndNotCached()
    {
        WriteBaseline("<worker-baseline>\n<worker-baseline>\ntext\n</worker-baseline>");
        SquadWorkerBaselineFile reader = NewReader();

        Assert.Throws<SquadRenderValidationException>(() => reader.ReadBlock(Source));
        Assert.Throws<SquadRenderValidationException>(() => reader.ReadBlock(Source));
    }
}

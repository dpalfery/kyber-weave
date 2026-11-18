using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The transcript source reads a transcript's lines unless it is missing or larger than the caller allows.</summary>
public sealed class FileFactoryTranscriptSourceTests
{
    private static readonly string Transcript = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "factory", "session.jsonl"));

    private readonly InMemoryFileSystem _files = new();
    private readonly FileFactoryTranscriptSource _source;

    public FileFactoryTranscriptSourceTests()
    {
        _source = new FileFactoryTranscriptSource(_files);
        _files.CreateDirectory(Path.GetDirectoryName(Transcript)!);
    }

    [Fact]
    public void ATranscriptWithinTheLimitYieldsItsLines()
    {
        _files.WriteAllText(Transcript, "one\ntwo\n");

        Assert.Equal(["one", "two"], _source.ReadLines(Transcript, maxBytes: 1024));
    }

    [Fact]
    public void ATranscriptLargerThanTheLimitYieldsNothing()
    {
        _files.WriteAllText(Transcript, "one\ntwo\n");

        Assert.Null(_source.ReadLines(Transcript, maxBytes: 3));
    }

    [Fact]
    public void ATranscriptThatDoesNotExistYieldsNothing() =>
        Assert.Null(_source.ReadLines(Transcript, maxBytes: 1024));
}

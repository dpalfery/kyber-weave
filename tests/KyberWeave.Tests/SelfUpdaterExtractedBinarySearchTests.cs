using KyberWeave.Cli.Update;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The search for the binary in an extracted archive must stay inside the extraction directory.</summary>
public sealed class SelfUpdaterExtractedBinarySearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kw-extract-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void FindsTheSingleBinaryInANestedFolder()
    {
        string extract = Directory.CreateDirectory(Path.Combine(_root, "extract", "pkg")).FullName;
        string binary = Path.Combine(extract, "kyber-weave");
        File.WriteAllText(binary, "x");

        Assert.Equal(binary, SelfUpdater.TryFindExtractedBinary(Path.Combine(_root, "extract"), "kyber-weave"));
    }

    [Fact]
    public void DoesNotFollowADirectoryLinkOutOfTheExtractionDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "kyber-weave"), "x");
        string extract = Directory.CreateDirectory(Path.Combine(_root, "extract")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(extract, "escape"), outside);

        Assert.Null(SelfUpdater.TryFindExtractedBinary(extract, "kyber-weave"));
    }
}

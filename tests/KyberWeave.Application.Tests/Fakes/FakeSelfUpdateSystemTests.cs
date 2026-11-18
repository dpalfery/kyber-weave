using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Fakes;

/// <summary>The self-update fake keeps the copy rules its seam promises, so a test over it can trust a refusal.</summary>
public sealed class FakeSelfUpdateSystemTests
{
    private static readonly string Root = new InMemoryFileSystem().Root;
    private static readonly string Source = Path.Combine(Root, "source");
    private static readonly string Destination = Path.Combine(Root, "destination");

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeSelfUpdateSystem _system;

    public FakeSelfUpdateSystemTests()
    {
        _files.WriteAllText(Source, "source bytes");
        _files.WriteAllText(Destination, "destination bytes");
        _system = new FakeSelfUpdateSystem(_files);
    }

    [Fact]
    public void CopyFileWithoutOverwriteRefusesAnExistingDestination()
    {
        Assert.Throws<IOException>(() => _system.CopyFile(Source, Destination, overwrite: false));
        Assert.Equal("destination bytes", _files.ReadAllText(Destination));
    }

    [Fact]
    public void CopyFileWithOverwriteReplacesTheDestination()
    {
        _system.CopyFile(Source, Destination, overwrite: true);

        Assert.Equal("source bytes", _files.ReadAllText(Destination));
        Assert.Contains(_system.Copies, copy => copy.Destination == Destination);
    }
}

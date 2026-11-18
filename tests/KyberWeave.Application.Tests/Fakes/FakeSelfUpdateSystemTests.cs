using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Fakes;

/// <summary>The self-update fake keeps the copy rules its seam promises, so a test over it can trust a refusal.</summary>
public sealed class FakeSelfUpdateSystemTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeSelfUpdateSystem _system;

    public FakeSelfUpdateSystemTests()
    {
        _files.WriteAllText("/source", "source bytes");
        _files.WriteAllText("/destination", "destination bytes");
        _system = new FakeSelfUpdateSystem(_files);
    }

    [Fact]
    public void CopyFileWithoutOverwriteRefusesAnExistingDestination()
    {
        Assert.Throws<IOException>(() => _system.CopyFile("/source", "/destination", overwrite: false));
        Assert.Equal("destination bytes", _files.ReadAllText("/destination"));
    }

    [Fact]
    public void CopyFileWithOverwriteReplacesTheDestination()
    {
        _system.CopyFile("/source", "/destination", overwrite: true);

        Assert.Equal("source bytes", _files.ReadAllText("/destination"));
        Assert.Contains(_system.Copies, copy => copy.Destination == "/destination");
    }
}

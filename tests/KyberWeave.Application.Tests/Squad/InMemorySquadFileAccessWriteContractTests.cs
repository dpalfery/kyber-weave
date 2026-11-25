using KyberWeave.Contracts.Repositories;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>Runs the Squad file seam's write contract against the in-memory fake.</summary>
public sealed class InMemorySquadFileAccessWriteContractTests : SquadFileAccessWriteContract
{
    /// <inheritdoc />
    protected override ISquadFileAccess CreateFileAccess() => new InMemorySquadFileAccess(new InMemoryFileSystem());

    /// <summary>A write to a file link keeps the link and fills the file it names, as the seam documents.</summary>
    [Fact]
    public void AWriteToAFileLinkKeepsTheLinkAndFillsItsTarget()
    {
        InMemoryFileSystem fs = new();
        InMemorySquadFileAccess files = new(fs);
        string target = Path.Combine(fs.Root, "dotfiles", "settings.json");
        string home = Path.Combine(fs.Root, "home");
        string link = Path.Combine(home, "settings.json");
        files.WriteAllBytesAtomic(target, [.. "old"u8]);
        files.CreateDirectory(home);
        fs.CreateFileLink(link, target);

        files.WriteAllBytesAtomic(link, [.. "new"u8]);

        Assert.Equal(target, fs.GetLinkTarget(link));
        Assert.Equal("new", files.ReadAllText(target));
    }

    /// <summary>The length reported for a file link is the length of the file it names.</summary>
    [Fact]
    public void GetLengthOnAFileLinkReturnsTheTargetsLength()
    {
        InMemoryFileSystem fs = new();
        InMemorySquadFileAccess files = new(fs);
        string target = Path.Combine(fs.Root, "dotfiles", "settings.json");
        string home = Path.Combine(fs.Root, "home");
        string link = Path.Combine(home, "settings.json");
        byte[] written = [.. "a document of known length"u8];
        files.WriteAllBytesAtomic(target, written);
        files.CreateDirectory(home);
        fs.CreateFileLink(link, target);

        Assert.Equal(written.Length, files.GetLength(link));
    }
}

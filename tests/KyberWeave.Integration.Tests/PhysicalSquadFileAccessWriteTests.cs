using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Runs the Squad file seam's write contract against the real disk, and proves the adapter keeps a secrets file's
/// Unix mode and writes through a symbolic link rather than replacing it.
/// </summary>
public sealed class PhysicalSquadFileAccessWriteTests : SquadFileAccessWriteContract
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <inheritdoc />
    protected override ISquadFileAccess CreateFileAccess() => new PhysicalSquadFileAccess();

    /// <summary>A <c>0600</c> file stays <c>0600</c> after the adapter replaces it.</summary>
    [Fact]
    public void AnOwnerOnlyFileKeepsItsModeAfterAWrite()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes do not exist on Windows.");
            return;
        }

        ISquadFileAccess files = Starting(out string root);
        string path = Path.Combine(root, "secrets.json");
        files.WriteAllBytesAtomic(path, [.. "{\"token\":\"old\"}"u8]);
        File.SetUnixFileMode(path, OwnerOnly);

        files.WriteAllBytesAtomic(path, [.. "{\"token\":\"new\"}"u8]);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
        Assert.Equal("{\"token\":\"new\"}", files.ReadAllText(path));
    }

    /// <summary>A symbolic link stays a link, and the file it names receives the bytes and keeps its mode.</summary>
    [Fact]
    public void ASymlinkedFileStaysALinkWhoseTargetGetsTheBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symbolic link needs a privilege Windows does not grant by default.");
            return;
        }

        ISquadFileAccess files = Starting(out string root);
        string target = Path.Combine(root, "dotfiles", "settings.json");
        string home = Path.Combine(root, "home");
        string link = Path.Combine(home, "settings.json");
        files.WriteAllBytesAtomic(target, [.. "old"u8]);
        File.SetUnixFileMode(target, OwnerOnly);
        files.CreateDirectory(home);
        _ = File.CreateSymbolicLink(link, target);

        files.WriteAllBytesAtomic(link, [.. "new"u8]);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("new", files.ReadAllText(target));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(target));
    }

    /// <summary>The length of a symbolic link is the length of the file it names, as the seam documents.</summary>
    [Fact]
    public void GetLengthOnASymlinkReturnsTheTargetsLength()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symbolic link needs a privilege Windows does not grant by default.");
            return;
        }

        ISquadFileAccess files = Starting(out string root);
        string target = Path.Combine(root, "dotfiles", "settings.json");
        string home = Path.Combine(root, "home");
        string link = Path.Combine(home, "settings.json");
        byte[] written = [.. "a document of known length"u8];
        files.WriteAllBytesAtomic(target, written);
        files.CreateDirectory(home);
        _ = File.CreateSymbolicLink(link, target);

        Assert.Equal(written.Length, files.GetLength(link));
        Assert.Equal(files.GetLength(target), files.GetLength(link));
    }

    /// <summary>A link whose target is gone has no length: it throws the missing-file exception the seam documents.</summary>
    [Fact]
    public void GetLengthOnADanglingSymlinkThrowsFileNotFound()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symbolic link needs a privilege Windows does not grant by default.");
            return;
        }

        ISquadFileAccess files = Starting(out string root);
        files.CreateDirectory(root);
        string link = Path.Combine(root, "settings.json");
        _ = File.CreateSymbolicLink(link, Path.Combine(root, "missing.json"));

        _ = Assert.Throws<FileNotFoundException>(() => files.GetLength(link));
    }
}

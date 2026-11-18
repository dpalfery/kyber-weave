using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the file-system contract against the real disk.</summary>
public sealed class PhysicalFileSystemContractTests : FileSystemContractTests
{
    /// <inheritdoc />
    protected override IWritableFileSystem CreateFileSystem() => new PhysicalFileSystem();

    /// <inheritdoc />
    protected override bool CaseSensitive => OperatingSystem.IsLinux();

    /// <summary>
    /// The adapter must not lock out a program that is still writing the file, because a reader that treats that
    /// failure as an absent answer would report a change it never saw.
    /// </summary>
    [Fact]
    public void AFileAnotherHandleHoldsOpenForWritingCanStillBeRead()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = WriteFile(files, root, "busy.log", "written before the read");

        using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        using Stream stream = files.OpenRead(path);
        using StreamReader reader = new(stream, Encoding.UTF8);

        Assert.Equal("written before the read", reader.ReadToEnd());
    }
}

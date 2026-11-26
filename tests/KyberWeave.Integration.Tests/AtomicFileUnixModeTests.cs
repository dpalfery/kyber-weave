using KyberWeave.Core.FileSystem;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Proves on the real disk that <see cref="AtomicFile"/> keeps a target's Unix mode only when the caller opts in, and
/// that every other overload still gives the replaced file the default mode a new file gets.
/// </summary>
/// <remarks>
/// The default mode depends on the process umask, so no case hard-codes <c>0644</c>: each compares against a control
/// file created in the same directory. The cases that prove the mode is not kept seed the target with an execute
/// bit, which a newly created file never carries, so the assertion holds under any umask.
/// </remarks>
public sealed class AtomicFileUnixModeTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyExecutable = OwnerOnly | UnixFileMode.UserExecute;
    private const string UnixOnly = "Unix file modes do not exist on Windows.";

    private readonly string _root = Directory.CreateTempSubdirectory("kw-atomic-mode-").FullName;

    /// <summary>Removes the scratch directory.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover sits under the system temporary directory.
        }
    }

    /// <summary>With the opt-in, a <c>0600</c> target is still <c>0600</c> after it is replaced.</summary>
    [Fact]
    public void PreservingTheModeKeepsAnOwnerOnlyTargetOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(UnixOnly);
            return;
        }

        string path = Seed("secrets.json", OwnerOnly);

        AtomicFile.WriteAllBytes(path, [.. "new"u8], writeThroughSymlink: false, flushToDisk: false, preserveUnixFileMode: true);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
        Assert.Equal("new", File.ReadAllText(path));
    }

    /// <summary>With the opt-in and a flushed write, the target's mode is kept as well.</summary>
    [Fact]
    public void PreservingTheModeAlsoHoldsForAFlushedWrite()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(UnixOnly);
            return;
        }

        string path = Seed("secrets.json", OwnerOnly);

        AtomicFile.WriteAllBytes(path, [.. "new"u8], writeThroughSymlink: false, flushToDisk: true, preserveUnixFileMode: true);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
        Assert.Equal("new", File.ReadAllText(path));
    }

    /// <summary>Written through a symbolic link, the mode copied is the link target's, and the link stays a link.</summary>
    [Fact]
    public void PreservingTheModeThroughASymlinkKeepsTheTargetsMode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(UnixOnly);
            return;
        }

        string target = Seed("secrets.json", OwnerOnly);
        string link = Path.Combine(_root, "link.json");
        _ = File.CreateSymbolicLink(link, target);

        AtomicFile.WriteAllBytes(link, [.. "new"u8], writeThroughSymlink: true, flushToDisk: false, preserveUnixFileMode: true);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(target));
        Assert.Equal("new", File.ReadAllText(target));
    }

    /// <summary>Without the opt-in, the replaced file takes the default mode, as it always has.</summary>
    [Fact]
    public void NotPreservingTheModeGivesTheDefaultMode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(UnixOnly);
            return;
        }

        string path = Seed("plain.json", OwnerOnlyExecutable);

        AtomicFile.WriteAllBytes(path, [.. "new"u8], writeThroughSymlink: false, flushToDisk: false, preserveUnixFileMode: false);

        Assert.Equal(DefaultMode(), File.GetUnixFileMode(path));
    }

    /// <summary>The original three-argument overload is unchanged: the replaced file takes the default mode.</summary>
    [Fact]
    public void TheOriginalOverloadStillGivesTheDefaultMode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(UnixOnly);
            return;
        }

        string path = Seed("plain.json", OwnerOnlyExecutable);

        AtomicFile.WriteAllBytes(path, [.. "new"u8]);

        Assert.Equal(DefaultMode(), File.GetUnixFileMode(path));
    }

    /// <summary>The flushing four-argument overload is unchanged: the replaced file takes the default mode.</summary>
    [Fact]
    public void TheFlushingOverloadStillGivesTheDefaultMode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(UnixOnly);
            return;
        }

        string path = Seed("plain.json", OwnerOnlyExecutable);

        AtomicFile.WriteAllBytes(path, [.. "new"u8], writeThroughSymlink: false, flushToDisk: true);

        Assert.Equal(DefaultMode(), File.GetUnixFileMode(path));
    }

    /// <summary>With the opt-in and no existing target, there is no mode to keep, so the new file gets the default.</summary>
    [Fact]
    public void ANewFileGetsTheDefaultModeEvenWhenPreserving()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(UnixOnly);
            return;
        }

        string path = Path.Combine(_root, "fresh", "settings.json");

        AtomicFile.WriteAllBytes(path, [.. "new"u8], writeThroughSymlink: true, flushToDisk: false, preserveUnixFileMode: true);

        Assert.Equal(DefaultMode(), File.GetUnixFileMode(path));
        Assert.Equal("new", File.ReadAllText(path));
    }

    private string Seed(string name, UnixFileMode mode)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "old");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);

        return path;
    }

    private UnixFileMode DefaultMode()
    {
        if (OperatingSystem.IsWindows())
            return UnixFileMode.None;

        string control = Path.Combine(_root, $"control-{Guid.NewGuid():N}");
        File.WriteAllText(control, "control");
        return File.GetUnixFileMode(control);
    }
}

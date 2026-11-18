using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>
/// The self-update system's copy against the real disk. The in-memory fake models content only, so this is the run
/// that proves a restored binary is still executable after a backup and restore.
/// </summary>
public sealed class SelfUpdateSystemContractTests : IDisposable
{
    private readonly IWritableFileSystem _files = new KyberWeave.Data.Hooks.PhysicalFileSystem();
    private readonly ISelfUpdateSystem _system = new SelfUpdateSystem();
    private readonly string _root;

    public SelfUpdateSystemContractTests() => _root = _files.CreateTemporaryDirectory("kw-selfupdate-contract-");

    public void Dispose() => _files.DeleteTreeNoFollow(_root);

    [Fact]
    public void CopyFileWithoutOverwriteRefusesAnExistingDestination()
    {
        string source = Path.Combine(_root, "source");
        string destination = Path.Combine(_root, "destination");
        _files.WriteAllText(source, "source");
        _files.WriteAllText(destination, "destination");

        Assert.Throws<IOException>(() => _system.CopyFile(source, destination, overwrite: false));
        Assert.Equal("destination", _files.ReadAllText(destination));
    }

    [Fact]
    public void CopyFileCarriesTheExecutableBitSoARestoredBinaryStillRuns()
    {
        if (OperatingSystem.IsWindows())
            return;

        string source = Path.Combine(_root, "source");
        string backup = Path.Combine(_root, "backup");
        string restored = Path.Combine(_root, "restored");
        _files.WriteAllText(source, "binary");
        UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(source, executable);

        _system.CopyFile(source, backup, overwrite: true);
        _system.CopyFile(backup, restored, overwrite: true);

        Assert.Equal(executable, File.GetUnixFileMode(restored));
    }
}

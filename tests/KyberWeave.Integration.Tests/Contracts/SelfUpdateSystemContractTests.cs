using KyberWeave.Testing.Update;
using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using Xunit;
using KyberWeave.Domain.Update;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>
/// The self-update system's copy against the real disk. The in-memory fake models content only, so this is the run
/// that proves a restored binary is still executable after a backup and restore.
/// </summary>
public sealed class SelfUpdateSystemContractTests : IDisposable
{
    private readonly IWritableFileSystem _files = new KyberWeave.Data.Hooks.PhysicalFileSystem();
    private readonly ISelfUpdateSystem _system = RealSelfUpdateSystem.Create();
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

    [Fact]
    public void ExtractedTarFilesKeepTheirExecutableBitOnTheRealDisk()
    {
        if (OperatingSystem.IsWindows())
            return;

        string archive = Path.Combine(_root, "release.tar.gz");
        _files.WriteAllBytes(archive, ReleaseArchiveBuilder.TarGz(new ReleaseArchiveEntry("kyber-weave", "binary")));

        _system.ExtractArchive(archive, Path.Combine(_root, "extract"), windows: false);

        Assert.Equal("binary", _files.ReadAllText(Path.Combine(_root, "extract", "kyber-weave")));
        Assert.True(File.GetUnixFileMode(Path.Combine(_root, "extract", "kyber-weave")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void AnEntryThatClimbsOutOfTheDestinationIsRefusedOnTheRealDisk()
    {
        string archive = Path.Combine(_root, "hostile.tar.gz");
        _files.WriteAllBytes(archive, ReleaseArchiveBuilder.TarGz(new ReleaseArchiveEntry("../escaped", "pwned")));

        _ = Assert.Throws<KyberWeave.Domain.Update.SelfUpdateException>(
            () => _system.ExtractArchive(archive, Path.Combine(_root, "extract"), windows: false));

        Assert.False(File.Exists(Path.Combine(_root, "escaped")));
    }

    [Fact]
    public async Task AReplacedBinaryIsExecutableAndLeavesNoStagingFileOnTheRealDisk()
    {
        if (OperatingSystem.IsWindows())
            return;

        string install = Path.Combine(_root, "install");
        string destination = Path.Combine(install, "kyber-weave");
        string extracted = Path.Combine(_root, "new-kyber-weave");
        _files.CreateDirectory(install);
        _files.WriteAllText(destination, "old");
        _files.WriteAllText(extracted, "new");

        await _system.ReplaceBinaryAsync(extracted, destination, windows: false, macOs: false, CancellationToken.None);

        Assert.Equal("new", _files.ReadAllText(destination));
        Assert.True(File.GetUnixFileMode(destination).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal([destination], Directory.EnumerateFileSystemEntries(install));
    }

    [Fact]
    public void AnArchiveMadeBySystemTarAsTheReleaseWorkflowDoesExtractsWithItsExecutableBit()
    {
        if (OperatingSystem.IsWindows())
            return;

        string packed = Path.Combine(_root, "packed");
        _files.CreateDirectory(packed);
        string image = Path.Combine(packed, "kyber-weave");
        _files.WriteAllText(image, "binary");
        File.SetUnixFileMode(image, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        string archive = Path.Combine(_root, "kyber-weave-linux-x64.tar.gz");
        ProcessStartInfo tar = new("tar") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-C", packed, "-czf", archive, "kyber-weave" })
            tar.ArgumentList.Add(argument);

        ProcessResult made = ProcessRunner.Run(tar, string.Empty, TimeSpan.FromSeconds(30));
        Assert.Equal(0, made.ExitCode);

        _system.ExtractArchive(archive, Path.Combine(_root, "extract"), windows: false);

        Assert.Equal("binary", _files.ReadAllText(Path.Combine(_root, "extract", "kyber-weave")));
        Assert.True(File.GetUnixFileMode(Path.Combine(_root, "extract", "kyber-weave")).HasFlag(UnixFileMode.UserExecute));
    }
}

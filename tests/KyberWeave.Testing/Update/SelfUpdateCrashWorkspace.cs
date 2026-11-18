using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;
using KyberWeave.Cli.Update;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Testing.Update;

/// <summary>
/// Where one crash-safety case of the self-update runs: an install directory on a file system that can be killed at
/// any operation, and the seams the update is built over.
/// </summary>
public abstract class SelfUpdateCrashWorkspace : IDisposable
{
    /// <summary>Gets the file system the update runs on. Its calls are counted and can be killed.</summary>
    public abstract IWritableFileSystem Files { get; }

    /// <summary>Gets the path resolver over <see cref="Files"/>.</summary>
    public abstract IPathResolver Paths { get; }

    /// <summary>Gets the permission-bit adapter the update installs through.</summary>
    internal abstract IUpdateFileModes Modes { get; }

    /// <summary>Gets the home and working directory the update reads.</summary>
    public abstract FakeProcessEnvironment Environment { get; }

    /// <summary>Gets the directory the binaries are installed in.</summary>
    public abstract string Install { get; }

    /// <summary>Gets how many file-system calls have been made.</summary>
    public abstract int OperationCount { get; }

    /// <summary>Gets whether the process was killed and has not been restarted.</summary>
    public abstract bool Killed { get; }

    /// <summary>Kills the process at the operation <paramref name="count"/> calls from now.</summary>
    /// <param name="count">How many more calls to allow before the killing one is the next, one-based.</param>
    public abstract void KillAfter(int count);

    /// <summary>Fails every call of one seam member on one path name, without killing.</summary>
    /// <param name="predicate">Gets the member name and its first path.</param>
    public abstract void FailWhen(Func<string, string?, bool> predicate);

    /// <summary>Restarts the process: faults and the latch go, the disk stays.</summary>
    public abstract void Restart();

    /// <summary>Reads every file under the install directory without counting a call.</summary>
    /// <returns>The bytes by name relative to the install directory, with forward slashes.</returns>
    public abstract IReadOnlyDictionary<string, byte[]> CaptureInstall();

    /// <inheritdoc />
    public abstract void Dispose();

    /// <summary>Gets the same capture rule for both implementations, over a raw file system.</summary>
    /// <param name="files">A file system that is not faulting.</param>
    /// <param name="install">The install directory.</param>
    /// <returns>The files by relative name.</returns>
    protected static Dictionary<string, byte[]> Capture(IFileSystem files, string install)
    {
        Dictionary<string, byte[]> tree = new(StringComparer.Ordinal);
        if (!files.DirectoryExists(install))
        {
            return tree;
        }

        foreach (string file in files.EnumerateFiles(install, "*", recursive: true))
        {
            string name = Path.GetRelativePath(install, file).Replace('\\', '/');
            tree[name] = files.ReadAllBytes(file);
        }

        return tree;
    }
}

/// <summary>The crash-safety workspace on the in-memory file system, killed with its own latching fault.</summary>
public sealed class InMemorySelfUpdateCrashWorkspace : SelfUpdateCrashWorkspace
{
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    /// <summary>Creates a workspace with an empty install directory.</summary>
    public InMemorySelfUpdateCrashWorkspace()
    {
        Paths = new FakePathResolver(_files);
        Modes = new FakeUpdateFileModes();
        Environment = new FakeProcessEnvironment(
            currentDirectory: "/kw/work",
            homeDirectory: "/kw/home",
            readVariable: _ => null);
        _files.CreateDirectory(Install);
        _files.CreateDirectory("/kw/work");
    }

    /// <inheritdoc />
    public override IWritableFileSystem Files => _files;

    /// <inheritdoc />
    public override IPathResolver Paths { get; }

    /// <inheritdoc />
    internal override IUpdateFileModes Modes { get; }

    /// <inheritdoc />
    public override FakeProcessEnvironment Environment { get; }

    /// <inheritdoc />
    public override string Install => "/kw/install";

    /// <inheritdoc />
    public override int OperationCount => _files.OperationCount;

    /// <inheritdoc />
    public override bool Killed => _files.IsKilled;

    /// <inheritdoc />
    public override void KillAfter(int count) => _files.KillAt(_files.OperationCount + count);

    /// <inheritdoc />
    public override void FailWhen(Func<string, string?, bool> predicate) =>
        _files.FailWhen(operation => predicate(operation.Name, operation.Path));

    /// <inheritdoc />
    public override void Restart() => _files.ClearFaults();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, byte[]> CaptureInstall()
    {
        bool wasKilled = _files.IsKilled;
        if (wasKilled)
        {
            throw new InvalidOperationException("Restart the process before reading the tree.");
        }

        return Capture(_files, Install);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
    }
}

/// <summary>The crash-safety workspace on the real disk, killed through a latching wrapper over the real file system.</summary>
public sealed class PhysicalSelfUpdateCrashWorkspace : SelfUpdateCrashWorkspace
{
    private readonly string _directory;
    private readonly PhysicalFileSystem _disk = new();
    private readonly IWritableFileSystem _files;
    private readonly LatchingFaultFileSystem _control;

    /// <summary>Creates a workspace in an empty scratch directory.</summary>
    /// <param name="directory">A scratch directory the workspace owns; its contents are removed first.</param>
    public PhysicalSelfUpdateCrashWorkspace(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            _disk.DeleteTreeNoFollow(entry);
        }

        _files = LatchingFaultFileSystem.Wrap(_disk);
        _control = LatchingFaultFileSystem.Control(_files);
        Install = Path.Combine(directory, "install");
        string home = Path.Combine(directory, "home");
        Directory.CreateDirectory(Install);
        Directory.CreateDirectory(home);
        Paths = new PhysicalPathResolver(_files);
        Modes = new UnixUpdateFileModes();
        Environment = new FakeProcessEnvironment(
            currentDirectory: directory,
            homeDirectory: home,
            readVariable: _ => null);
    }

    /// <inheritdoc />
    public override IWritableFileSystem Files => _files;

    /// <inheritdoc />
    public override IPathResolver Paths { get; }

    /// <inheritdoc />
    internal override IUpdateFileModes Modes { get; }

    /// <inheritdoc />
    public override FakeProcessEnvironment Environment { get; }

    /// <inheritdoc />
    public override string Install { get; }

    /// <inheritdoc />
    public override int OperationCount => _control.OperationCount;

    /// <inheritdoc />
    public override bool Killed => _control.IsKilled;

    /// <inheritdoc />
    public override void KillAfter(int count) => _control.KillAt(_control.OperationCount + count);

    /// <inheritdoc />
    public override void FailWhen(Func<string, string?, bool> predicate) => _control.FailWhen(predicate);

    /// <inheritdoc />
    public override void Restart() => _control.Restart();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, byte[]> CaptureInstall() => Capture(_disk, Install);

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (string temporary in _control.TemporaryDirectories)
        {
            _disk.DeleteTreeNoFollow(temporary);
        }
    }
}

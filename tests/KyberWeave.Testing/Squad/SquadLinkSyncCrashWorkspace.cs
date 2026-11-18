using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// The workspace an instance link sync crash-safety run needs: a main home, an instance root and a state directory in
/// one tree, the sync over a file system that can stop the process at any operation, and whole-tree snapshots.
/// </summary>
public abstract class SquadLinkSyncCrashWorkspace : IDisposable
{
    /// <summary>Gets the directory holding <c>main</c>, <c>instance</c> and <c>state</c>.</summary>
    public abstract string Root { get; }

    /// <summary>Gets the file system a test seeds and changes the tree through. It is not counted and never stops.</summary>
    public abstract IWritableFileSystem Disk { get; }

    /// <summary>Gets the sync under test.</summary>
    public abstract ISquadInstanceLinkSync Sync { get; }

    /// <summary>Gets the link file system the sync is given; its link operations are counted like every other.</summary>
    public abstract ISquadLinkFileSystem Links { get; }

    /// <summary>Gets how many file-system operations the sync has performed.</summary>
    public abstract int OperationCount { get; }

    /// <summary>Stops the process at one operation, counted from the first one this workspace ever saw.</summary>
    /// <param name="ordinal">The one-based operation number.</param>
    public abstract void KillAt(int ordinal);

    /// <summary>Whether an exception is this workspace's own stop, rather than a failure of the code under test.</summary>
    /// <param name="exception">The exception a run threw.</param>
    /// <returns>True for the injected stop.</returns>
    public abstract bool IsKill(Exception exception);

    /// <summary>Clears the stop, as restarting the process does; the tree survives.</summary>
    public abstract void Restart();

    /// <summary>Gets the main home directory, which the sync only reads.</summary>
    public string MainHome => Path.Combine(Root, "main");

    /// <summary>Gets the instance root the links are created in.</summary>
    public string InstanceRoot => Path.Combine(Root, "instance");

    /// <summary>Gets the directory holding the recorded links.</summary>
    public string StateDirectory => Path.Combine(Root, "state");

    /// <summary>Snapshots every path in the workspace.</summary>
    /// <returns>The tree, keyed by '/'-separated path in ordinal order.</returns>
    public IReadOnlyDictionary<string, SquadTreeEntry> Capture() => SquadStateTreeCapture.Capture(Disk, Root);

    /// <inheritdoc />
    public abstract void Dispose();
}

/// <summary>The link sync crash workspace over <see cref="InMemoryFileSystem"/>: every operation is a fault point.</summary>
public sealed class InMemorySquadLinkSyncCrashWorkspace : SquadLinkSyncCrashWorkspace
{
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly SquadInstanceLinkSync _sync;
    private readonly FileSystemSquadLinkFileSystem _links;

    /// <summary>Creates a workspace over an empty in-memory file system.</summary>
    public InMemorySquadLinkSyncCrashWorkspace()
    {
        _files.CreateDirectory(Root);
        _sync = new SquadInstanceLinkSync(
            _files,
            new FakePathResolver(_files),
            new InProcessLease(),
            new FakeProcessEnvironment(currentDirectory: Path.GetFullPath("/kw/work")));
        _links = new FileSystemSquadLinkFileSystem(_files);
    }

    /// <inheritdoc />
    public override string Root => Path.GetFullPath("/kw/links");

    /// <inheritdoc />
    public override IWritableFileSystem Disk => _files;

    /// <inheritdoc />
    public override ISquadInstanceLinkSync Sync => _sync;

    /// <inheritdoc />
    public override ISquadLinkFileSystem Links => _links;

    /// <inheritdoc />
    public override int OperationCount => _files.OperationCount;

    /// <inheritdoc />
    public override void KillAt(int ordinal) => _files.KillAt(ordinal);

    /// <inheritdoc />
    public override bool IsKill(Exception exception) => exception is InMemoryFileSystemKilledException;

    /// <inheritdoc />
    public override void Restart() => _files.ClearFaults();

    /// <inheritdoc />
    public override void Dispose()
    {
    }
}

/// <summary>
/// The link sync crash workspace over a directory on the real disk, with the real path resolver and mutex lease and a
/// <see cref="FaultInjectingFileSystem"/> in front of the real file system.
/// </summary>
/// <remarks>
/// Every case of one run works in the same directory, because the sync's lease and the recorded link targets are named
/// by paths and two cases could otherwise never be compared. A new workspace empties the directory it is given, and
/// disposing it leaves the directory to its owner.
/// </remarks>
public sealed class PhysicalSquadLinkSyncCrashWorkspace : SquadLinkSyncCrashWorkspace
{
    private readonly PhysicalFileSystem _disk = new();
    private readonly FaultInjectingFileSystem _faulting;
    private readonly SquadInstanceLinkSync _sync;
    private readonly FileSystemSquadLinkFileSystem _links;

    /// <summary>Creates a workspace in an emptied directory.</summary>
    /// <param name="directory">The directory every case of one run shares; it is emptied.</param>
    public PhysicalSquadLinkSyncCrashWorkspace(string directory)
    {
        Root = directory;
        Directory.CreateDirectory(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            _disk.DeleteTreeNoFollow(entry);
        }

        _faulting = new FaultInjectingFileSystem(_disk);
        _sync = new SquadInstanceLinkSync(
            _faulting,
            new PhysicalPathResolver(_disk),
            new MutexLease(),
            new ProcessEnvironment());
        _links = new FileSystemSquadLinkFileSystem(_faulting);
    }

    /// <inheritdoc />
    public override string Root { get; }

    /// <inheritdoc />
    public override IWritableFileSystem Disk => _disk;

    /// <inheritdoc />
    public override ISquadInstanceLinkSync Sync => _sync;

    /// <inheritdoc />
    public override ISquadLinkFileSystem Links => _links;

    /// <inheritdoc />
    public override int OperationCount => _faulting.OperationCount;

    /// <inheritdoc />
    public override void KillAt(int ordinal) => _faulting.KillAt(ordinal);

    /// <inheritdoc />
    public override bool IsKill(Exception exception) => exception is SquadCrashInjectedException;

    /// <inheritdoc />
    public override void Restart() => _faulting.Restart();

    /// <inheritdoc />
    public override void Dispose()
    {
    }
}

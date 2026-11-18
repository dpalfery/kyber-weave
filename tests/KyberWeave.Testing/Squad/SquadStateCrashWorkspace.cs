using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// The workspace a state-migration crash-safety run needs: a store and migration over a file system that can stop the
/// process at any operation, a layout to seed legacy state with, and whole-tree snapshots.
/// </summary>
public abstract class SquadStateCrashWorkspace : IDisposable
{
    /// <summary>Gets the migration under test.</summary>
    public abstract SquadStateMigration Migration { get; }

    /// <summary>Gets where the state lives and how a test writes into it.</summary>
    public abstract SquadStateTestLayout Layout { get; }

    /// <summary>Gets how many file-system operations the migration and store have performed.</summary>
    public abstract int OperationCount { get; }

    /// <summary>Creates a project directory and returns its canonical path.</summary>
    /// <param name="name">The directory name.</param>
    /// <returns>The canonical path.</returns>
    public abstract string CreateRoot(string name);

    /// <summary>Stops the process at one operation, counted from the first one this workspace ever saw.</summary>
    /// <param name="ordinal">The one-based operation number.</param>
    public abstract void KillAt(int ordinal);

    /// <summary>Whether an exception is this workspace's own stop, rather than a failure of the code under test.</summary>
    /// <param name="exception">The exception a run threw.</param>
    /// <returns>True for the injected stop.</returns>
    public abstract bool IsKill(Exception exception);

    /// <summary>Clears the stop, as restarting the process does; the tree survives.</summary>
    public abstract void Restart();

    /// <summary>Snapshots every path in the workspace.</summary>
    /// <returns>The tree, keyed by '/'-separated path in ordinal order.</returns>
    public abstract IReadOnlyDictionary<string, SquadTreeEntry> Capture();

    /// <inheritdoc />
    public abstract void Dispose();
}

/// <summary>The crash workspace over <see cref="InMemorySquadState"/>: every operation is a fault point.</summary>
public sealed class InMemorySquadStateCrashWorkspace : SquadStateCrashWorkspace
{
    private readonly InMemorySquadState _state = new();

    /// <inheritdoc />
    public override SquadStateMigration Migration => _state.Migration;

    /// <inheritdoc />
    public override SquadStateTestLayout Layout => _state.Layout;

    /// <inheritdoc />
    public override int OperationCount => _state.Files.OperationCount;

    /// <inheritdoc />
    public override string CreateRoot(string name) => _state.CreateRoot(name);

    /// <inheritdoc />
    public override void KillAt(int ordinal) => _state.Files.KillAt(ordinal);

    /// <inheritdoc />
    public override bool IsKill(Exception exception) => exception is InMemoryFileSystemKilledException;

    /// <inheritdoc />
    public override void Restart() => _state.Files.ClearFaults();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, SquadTreeEntry> Capture() => _state.Capture();

    /// <inheritdoc />
    public override void Dispose()
    {
    }
}

/// <summary>
/// The crash workspace over a directory on the real disk, with the real path resolver, mutex lease and environment,
/// and a <see cref="FaultInjectingFileSystem"/> in front of the real file system.
/// </summary>
/// <remarks>
/// A root's state is named by a key derived from its physical path, so every case in one run must work in the same
/// directory or the trees of two cases could never be compared. A new workspace therefore empties the directory it is
/// given, and disposing it leaves the directory to its owner.
/// </remarks>
public sealed class PhysicalSquadStateCrashWorkspace : SquadStateCrashWorkspace
{
    private readonly string _directory;
    private readonly PhysicalFileSystem _disk = new();
    private readonly FaultInjectingFileSystem _faulting;
    private readonly SquadStateMigration _migration;
    private readonly SquadStateTestLayout _layout;

    /// <summary>Creates a workspace in an emptied directory.</summary>
    /// <param name="directory">The directory every case of one run shares; it is emptied.</param>
    public PhysicalSquadStateCrashWorkspace(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            _disk.DeleteTreeNoFollow(entry);
        }

        _faulting = new FaultInjectingFileSystem(_disk);
        string home = Path.Combine(directory, "home");
        string applicationData = Path.Combine(directory, "application-data");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(applicationData);
        FakeSquadUserPaths userPaths = new(applicationData, home);
        PhysicalPathResolver paths = new(_faulting);
        SquadFileSystemPathSemantics semantics = new(paths);
        SquadStateStore store = new(userPaths, _faulting, semantics, paths, new ProcessEnvironment(), new MutexLease());
        _migration = new SquadStateMigration(store, userPaths, _faulting, semantics, new MutexLease(), new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        _layout = new SquadStateTestLayout(
            new SquadStateStore(userPaths, _disk, semantics, new PhysicalPathResolver(_disk), new ProcessEnvironment(), new MutexLease()),
            _disk,
            new SquadFileSystemPathSemantics(new PhysicalPathResolver(_disk)));
    }

    /// <inheritdoc />
    public override SquadStateMigration Migration => _migration;

    /// <inheritdoc />
    public override SquadStateTestLayout Layout => _layout;

    /// <inheritdoc />
    public override int OperationCount => _faulting.OperationCount;

    /// <inheritdoc />
    public override string CreateRoot(string name)
    {
        string root = Path.Combine(_directory, "projects", name);
        Directory.CreateDirectory(root);
        return _layout.PhysicalOf(root);
    }

    /// <inheritdoc />
    public override void KillAt(int ordinal) => _faulting.KillAt(ordinal);

    /// <inheritdoc />
    public override bool IsKill(Exception exception) => exception is SquadCrashInjectedException;

    /// <inheritdoc />
    public override void Restart() => _faulting.Restart();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, SquadTreeEntry> Capture() =>
        SquadStateTreeCapture.Capture(_disk, _directory);

    /// <inheritdoc />
    public override void Dispose()
    {
    }
}

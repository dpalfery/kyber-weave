using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests;

namespace KyberWeave.Testing.Squad;

/// <summary>The lifecycle flow whose crash safety is under test.</summary>
public enum SquadCrashFlow
{
    /// <summary>A first install that adopts a file already on disk.</summary>
    Install,

    /// <summary>An update that changes managed files and retains a locally edited one.</summary>
    Update,

    /// <summary>An uninstall that removes managed files and retains a locally edited one.</summary>
    Uninstall
}

/// <summary>What a snapshot found at one path.</summary>
public enum SquadTreeEntryKind
{
    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A regular file.</summary>
    File,

    /// <summary>A symbolic link that names a file.</summary>
    FileSymbolicLink,

    /// <summary>A symbolic link that names a directory.</summary>
    DirectorySymbolicLink
}

/// <summary>One path in a whole-tree snapshot: its kind, its bytes, or a link's target.</summary>
/// <param name="Kind">What exists at the path.</param>
/// <param name="Content">The bytes of a file; null for a directory or a link.</param>
/// <param name="LinkTarget">A link's target; null for anything else.</param>
public sealed record SquadTreeEntry(
    SquadTreeEntryKind Kind,
    ReadOnlyMemory<byte>? Content = null,
    string? LinkTarget = null);

/// <summary>
/// The workspace a crash-safety contract run needs: a tree, a plan for the flow under test, a
/// transaction over it, whole-tree snapshots, and the way this file system stops a flow at a fault
/// point.
/// </summary>
/// <remarks>
/// <para>
/// A <em>fault point</em> is one distinct crash state the file system can be stopped at. The
/// real-disk implementation can only stop where the transaction already reports a durable step or
/// checkpoint, so the windows inside a bulk staging or backup wave are not separately reachable
/// there; it records one point per distinct tree those callbacks expose.
/// </para>
/// <para>
/// A second implementation will run against <c>InMemoryFileSystem</c> once the deployment classes
/// take the file-system seam (tasks 3.2 to 3.4): it enumerates one fault point per file-system
/// operation and kills the fake at that ordinal, which is the only way to stop inside a write or a
/// wave, leaving this suite unchanged.
/// </para>
/// </remarks>
public abstract class SquadCrashSafetyFixture : IDisposable
{
    /// <summary>A managed file that an update changes.</summary>
    public const string ConductorPath = ".codex/agents/conductor.toml";

    /// <summary>A second managed file that an update changes, in a directory of its own so a recovery can be stopped between the two.</summary>
    public const string TwoPath = ".warp/agents/two.md";

    /// <summary>A file the host already had, adopted by the install, whose bytes never change.</summary>
    public const string AdoptedPath = ".codex/agents/adopted.md";

    /// <summary>A receipt-owned file the host edited after installation, which must survive every flow.</summary>
    public const string EditedPath = ".codex/agents/edited.md";

    /// <summary>A file Squad has never managed.</summary>
    public const string ForeignPath = "notes/operator.md";

    /// <summary>Gets the directory the whole tree is rooted at.</summary>
    public abstract string Root { get; }

    /// <summary>Gets the state store the transaction writes its lock, receipt and journal through.</summary>
    public abstract SquadStateStore StateStore { get; }

    /// <summary>
    /// Gets the deployment scope this workspace can prove crash safety for. The kill snapshot covers
    /// only <see cref="Root"/>, so a scope whose journal or state lives outside it cannot use this
    /// workspace: the implementation refuses rather than silently testing the wrong tree.
    /// </summary>
    public abstract SquadDeploymentScope Scope { get; }

    /// <summary>Writes the tree the flow starts from, including the state the flow reads.</summary>
    /// <param name="flow">The flow about to run.</param>
    public abstract void CreateBaseline(SquadCrashFlow flow);

    /// <summary>Runs the flow with no fault and returns every fault point it passed.</summary>
    /// <param name="flow">The flow to run.</param>
    /// <returns>
    /// One name per distinct crash state the flow reached, each understood by
    /// <see cref="ExecuteUnderFault"/>, in the order the flow reached them. A callback whose tree an
    /// earlier callback already left is not repeated.
    /// </returns>
    public abstract IReadOnlyList<string> ExecuteClean(SquadCrashFlow flow);

    /// <summary>
    /// Runs the flow and stops it at <paramref name="faultPoint"/> as a killed process would, then
    /// leaves the tree exactly as it stood at that instant.
    /// </summary>
    /// <param name="flow">The flow to run.</param>
    /// <param name="faultPoint">One of the names <see cref="ExecuteClean"/> returned.</param>
    /// <returns>Null when the flow reached the point and stopped there; a description of what went wrong otherwise.</returns>
    public abstract string? ExecuteUnderFault(SquadCrashFlow flow, string faultPoint);

    /// <summary>
    /// Runs the flow and fails it at <paramref name="faultPoint"/> as a handled error would, leaving
    /// the transaction to roll itself back.
    /// </summary>
    /// <param name="flow">The flow to run.</param>
    /// <param name="faultPoint">One of the names <see cref="ExecuteClean"/> returned.</param>
    /// <returns>Null when the flow reached the point and rolled back; a description of what went wrong otherwise.</returns>
    public abstract string? ExecuteToRollback(SquadCrashFlow flow, string faultPoint);

    /// <summary>Runs recovery twice over the interrupted tree, as a restarted process would.</summary>
    public abstract void Recover();

    /// <summary>Snapshots every path beneath <see cref="Root"/>.</summary>
    /// <returns>The tree, keyed by '/'-separated relative path in ordinal order.</returns>
    public abstract IReadOnlyDictionary<string, SquadTreeEntry> Capture();

    /// <summary>Replaces the whole tree with a snapshot.</summary>
    /// <param name="tree">The snapshot to write.</param>
    public abstract void Restore(IReadOnlyDictionary<string, SquadTreeEntry> tree);

    /// <summary>The baseline paths this flow must never change, whatever fault stopped it.</summary>
    /// <param name="flow">The flow under test.</param>
    /// <returns>The paths, keyed as <see cref="Capture"/> keys them.</returns>
    public abstract IReadOnlyList<string> UntouchedPaths(SquadCrashFlow flow);

    /// <inheritdoc />
    public abstract void Dispose();

    /// <inheritdoc />
    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>Raised by a fixture that stopped a flow at a fault point on purpose.</summary>
public sealed class SquadCrashInjectedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SquadCrashInjectedException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public SquadCrashInjectedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SquadCrashInjectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The real-disk workspace for the crash-safety contract suite.</summary>
public sealed class PhysicalSquadCrashSafetyFixture : SquadCrashSafetyFixture
{
    private static readonly DateTimeOffset InstalledAt = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public override string Root => _temp.Path;

    /// <inheritdoc />
    public override SquadStateStore StateStore => new(new FakeSquadUserPaths(Root));

    /// <inheritdoc />
    public override SquadDeploymentScope Scope => SquadDeploymentScope.Project;

    /// <inheritdoc />
    public override void CreateBaseline(SquadCrashFlow flow)
    {
        RequireEveryStatePathUnderRoot();
        Directory.CreateDirectory(Root);
        Write(ForeignPath, "operator note");

        if (flow == SquadCrashFlow.Install)
        {
            Write(AdoptedPath, "adopted body");
            return;
        }

        Write(ConductorPath, "installed body");
        Write(TwoPath, "installed two");
        Write(AdoptedPath, "adopted body");
        Write(EditedPath, "locally edited body");
        WriteState();
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> ExecuteClean(SquadCrashFlow flow)
    {
        RecordingObserver observer = new(Capture);
        new SquadTransaction(StateStore, observer).Execute(CreatePlan(flow));
        return observer.FaultPoints;
    }

    /// <inheritdoc />
    public override string? ExecuteUnderFault(SquadCrashFlow flow, string faultPoint)
    {
        KillingObserver observer = new(this, faultPoint);
        string? problem = null;
        try
        {
            new SquadTransaction(StateStore, observer).Execute(CreatePlan(flow));
            problem = "The flow completed without reaching the fault point.";
        }
        catch (SquadCrashInjectedException)
        {
        }
        catch (Exception exception)
        {
            problem = $"The flow reported the fault as {exception.GetType().Name}: {exception.Message}";
        }

        if (observer.Interrupted is { } interrupted)
        {
            Restore(interrupted);
        }

        return problem;
    }

    /// <inheritdoc />
    public override string? ExecuteToRollback(SquadCrashFlow flow, string faultPoint)
    {
        RecordingObserver observer = new(failAt: faultPoint);
        try
        {
            new SquadTransaction(StateStore, observer).Execute(CreatePlan(flow));
            return "The flow completed without reaching the fault point.";
        }
        catch (SquadCrashInjectedException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return $"The transaction reported the failure as {exception.GetType().Name}: {exception.Message}";
        }
    }

    /// <inheritdoc />
    public override void Recover()
    {
        new SquadTransaction(StateStore).Recover(Root, SquadDeploymentScope.Project);
        new SquadTransaction(StateStore).Recover(Root, SquadDeploymentScope.Project);
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, SquadTreeEntry> Capture()
    {
        SortedDictionary<string, SquadTreeEntry> tree = new(StringComparer.Ordinal);
        CaptureDirectory(new DirectoryInfo(Root), Root, tree);
        return tree;
    }

    /// <inheritdoc />
    public override void Restore(IReadOnlyDictionary<string, SquadTreeEntry> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        Directory.CreateDirectory(Root);
        DeleteContents(new DirectoryInfo(Root));

        foreach ((string relativePath, SquadTreeEntry _) in tree
                     .Where(pair => pair.Value.Kind == SquadTreeEntryKind.Directory)
                     .OrderBy(pair => Depth(pair.Key)))
        {
            Directory.CreateDirectory(PathOf(relativePath));
        }

        foreach ((string relativePath, SquadTreeEntry entry) in tree
                     .Where(pair => pair.Value.Kind == SquadTreeEntryKind.File))
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, (entry.Content ?? ReadOnlyMemory<byte>.Empty).ToArray());
        }

        foreach ((string relativePath, SquadTreeEntry entry) in tree.Where(pair =>
                     pair.Value.Kind is SquadTreeEntryKind.FileSymbolicLink or
                         SquadTreeEntryKind.DirectorySymbolicLink))
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string target = entry.LinkTarget
                ?? throw new InvalidOperationException($"The snapshot's link '{relativePath}' has no target.");
            if (entry.Kind == SquadTreeEntryKind.DirectorySymbolicLink)
            {
                Directory.CreateSymbolicLink(path, target);
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }
        }

        // Prove the restore before the next case runs on top of it: a snapshot that did not round-trip
        // (a refused link, a read-only file) would otherwise be blamed on the next fault point.
        string? difference = SquadTreeComparison.Difference(tree, Capture());
        if (difference is not null)
        {
            throw new InvalidOperationException(
                $"The kill snapshot did not restore exactly, so the next case would run on a contaminated tree: {difference}");
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> UntouchedPaths(SquadCrashFlow flow) => flow switch
    {
        SquadCrashFlow.Install => [RelativeKey(ForeignPath), RelativeKey(AdoptedPath)],
        SquadCrashFlow.Update or SquadCrashFlow.Uninstall =>
            [RelativeKey(ForeignPath), RelativeKey(AdoptedPath), RelativeKey(EditedPath)],
        _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, null)
    };

    /// <inheritdoc />
    public override void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _temp.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string RelativeKey(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/');

    private static int Depth(string relativePath) =>
        relativePath.Count(character => character == '/');

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static SquadDeploymentFile Rendered(string relativePath, string content) =>
        new(relativePath, Encoding.UTF8.GetBytes(content), "codex");

    private static SquadLock Lock(string version) =>
        new(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["codex"],
            [],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));

    private SquadReceipt BaselineReceipt() =>
        new(
            SquadStateStore.ReceiptSchemaV1,
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(ConductorPath, Digest("installed body"), "codex", false),
                new SquadOwnedFile(TwoPath, Digest("installed two"), "codex", false),
                new SquadOwnedFile(AdoptedPath, Digest("adopted body"), "codex", true),
                new SquadOwnedFile(EditedPath, Digest("installed edited body"), "codex", false)
            ]);

    private SquadDeploymentPlan CreatePlan(SquadCrashFlow flow) => flow switch
    {
        SquadCrashFlow.Install => SquadDeploymentPlanner.CreateInstall(
            Root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [
                Rendered(ConductorPath, "installed body"),
                Rendered(AdoptedPath, "adopted body"),
                Rendered(EditedPath, "installed edited body")
            ],
            [],
            adopt: true,
            new FixedTimeProvider(InstalledAt)),
        SquadCrashFlow.Update => SquadDeploymentPlanner.CreateUpdate(
            Root,
            SquadDeploymentScope.Project,
            Lock("1.2.5"),
            [
                Rendered(ConductorPath, "updated body"),
                Rendered(TwoPath, "updated two"),
                Rendered(AdoptedPath, "adopted body"),
                Rendered(EditedPath, "updated edited body")
            ],
            BaselineReceipt(),
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddDays(1))),
        SquadCrashFlow.Uninstall => SquadDeploymentPlanner.CreateUninstall(
            Root,
            SquadDeploymentScope.Project,
            BaselineReceipt()),
        _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, null)
    };

    /// <summary>
    /// Proves the assumption the kill snapshot rests on: every path the transaction writes state or
    /// a journal to is beneath <see cref="Root"/>, so restoring the snapshot also restores recovery
    /// evidence. A scope whose journal sits elsewhere would make every case prove nothing.
    /// </summary>
    private void RequireEveryStatePathUnderRoot()
    {
        SquadStateStore store = StateStore;
        string[] statePaths =
        [
            store.ResolveLockPath(Root, Scope),
            store.ResolveReceiptPath(Root, Scope),
            store.ResolveTransactionDirectory(Root, Scope),
            store.ResolveFileWorkDirectory(Root, Scope)
        ];
        string rootSpelling = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
        string canonicalRoot = Path.TrimEndingDirectorySeparator(
            SquadPhysicalRootIdentity.Resolve(Root, SquadFileSystemPathSemantics.Instance).PhysicalPath);
        foreach (string statePath in statePaths)
        {
            string full = Path.GetFullPath(statePath);
            string canonical = full.StartsWith(rootSpelling, StringComparison.Ordinal)
                ? canonicalRoot + full[rootSpelling.Length..]
                : full;
            if (!canonical.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The {Scope} workspace resolves '{statePath}' outside its root '{Root}', so a kill snapshot would not restore it. " +
                    "Run that scope through a fixture that snapshots every root the transaction writes.");
            }
        }
    }

    private void WriteState()
    {
        SquadStateStore store = StateStore;
        string lockPath = store.ResolveLockPath(Root, SquadDeploymentScope.Project);
        string receiptPath = store.ResolveReceiptPath(Root, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, store.SerializeLock(Lock("1.2.4")), new UTF8Encoding(false));
        File.WriteAllText(receiptPath, store.SerializeReceipt(BaselineReceipt()), new UTF8Encoding(false));
    }

    private void Write(string relativePath, string content)
    {
        string path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private string PathOf(string relativePath) =>
        Path.Combine(
            Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void CaptureDirectory(DirectoryInfo directory, string root, IDictionary<string, SquadTreeEntry> tree)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            string relativePath = RelativeKey(Path.GetRelativePath(root, entry.FullName));
            if (entry.LinkTarget is not null)
            {
                bool namesDirectory = entry is DirectoryInfo;
                tree.Add(
                    relativePath,
                    new SquadTreeEntry(
                        namesDirectory
                            ? SquadTreeEntryKind.DirectorySymbolicLink
                            : SquadTreeEntryKind.FileSymbolicLink,
                        LinkTarget: entry.LinkTarget));
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                tree.Add(relativePath, new SquadTreeEntry(SquadTreeEntryKind.Directory));
                CaptureDirectory(childDirectory, root, tree);
                continue;
            }

            tree.Add(
                relativePath,
                new SquadTreeEntry(SquadTreeEntryKind.File, File.ReadAllBytes(entry.FullName)));
        }
    }

    private static void DeleteContents(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos().ToArray())
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo)
                {
                    Directory.Delete(entry.FullName);
                }
                else
                {
                    entry.Delete();
                }

                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                DeleteContents(childDirectory);
                childDirectory.Delete();
            }
            else
            {
                entry.Delete();
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    /// <summary>
    /// Records one fault point per crash state the transaction's callbacks expose during a clean
    /// run, or stops the flow at one named point with a handled failure so the transaction rolls
    /// back. A callback whose tree an earlier callback already left is not recorded: a bulk
    /// notification loop fires several callbacks with nothing written between them.
    /// </summary>
    private sealed class RecordingObserver(
        Func<IReadOnlyDictionary<string, SquadTreeEntry>>? capture = null,
        string? failAt = null) : ISquadTransactionCheckpointObserver
    {
        private readonly List<string> _faultPoints = [];
        private readonly HashSet<string> _states = new(StringComparer.Ordinal);
        private int _steps;
        private int _checkpoints;

        public IReadOnlyList<string> FaultPoints => _faultPoints;

        public void AfterStep(SquadTransactionStep transactionStep)
        {
            _steps++;
            Record($"step:{Invariant(transactionStep.Sequence)}");
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            _checkpoints++;
            Record($"checkpoint:{Invariant(checkpoint.Sequence)}");
        }

        private void Record(string point)
        {
            if (string.Equals(failAt, point, StringComparison.Ordinal))
            {
                throw new SquadCrashInjectedException($"The flow failed at '{failAt}'.");
            }

            if (capture is not null && !_states.Add(SquadTreeFingerprint.Of(capture())))
            {
                return;
            }

            _faultPoints.Add(point);
        }
    }

    /// <summary>Snapshots the tree at one callback and then stops the flow there.</summary>
    private sealed class KillingObserver(SquadCrashSafetyFixture owner, string faultPoint) :
        ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, SquadTreeEntry>? Interrupted { get; private set; }

        public void AfterStep(SquadTransactionStep transactionStep)
        {
            if (string.Equals(faultPoint, $"step:{Invariant(transactionStep.Sequence)}", StringComparison.Ordinal))
            {
                Stop();
            }
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (string.Equals(faultPoint, $"checkpoint:{Invariant(checkpoint.Sequence)}", StringComparison.Ordinal))
            {
                Stop();
            }
        }

        private void Stop()
        {
            Interrupted = owner.Capture();
            throw new SquadCrashInjectedException($"The flow was stopped at '{faultPoint}'.");
        }
    }

    private static string Invariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}

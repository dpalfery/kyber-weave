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

    private const string ToolFolder = ".kyber-weave";

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

    /// <summary>
    /// Plans the flow afresh over whatever tree is on disk and executes it, as the next command a user runs would:
    /// no explicit recovery, so an interrupted transaction left behind is the transaction's own to recover.
    /// </summary>
    /// <param name="flow">The flow to run next.</param>
    public abstract void ExecuteNext(SquadCrashFlow flow);

    /// <summary>Snapshots every path beneath <see cref="Root"/>.</summary>
    /// <returns>The tree, keyed by '/'-separated relative path in ordinal order.</returns>
    public abstract IReadOnlyDictionary<string, SquadTreeEntry> Capture();

    /// <summary>Replaces the whole tree with a snapshot.</summary>
    /// <param name="tree">The snapshot to write.</param>
    public abstract void Restore(IReadOnlyDictionary<string, SquadTreeEntry> tree);

    /// <summary>Gets the planner that builds each flow's plan.</summary>
    protected abstract SquadDeploymentPlanner Planner { get; }

    /// <summary>Creates the root and checks the layout the kill snapshot relies on.</summary>
    protected abstract void PrepareRoot();

    /// <summary>Writes a file, creating its parent directories.</summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="content">The UTF-8 text.</param>
    protected abstract void WriteText(string path, string content);

    /// <inheritdoc />
    public void CreateBaseline(SquadCrashFlow flow)
    {
        PrepareRoot();
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
        SquadStateStore store = StateStore;
        WriteText(store.ResolveLockPath(Root, Scope), store.SerializeLock(Lock("1.2.4")));
        WriteText(store.ResolveReceiptPath(Root, Scope), store.SerializeReceipt(BaselineReceipt()));
    }

    /// <summary>The baseline paths this flow must never change, whatever fault stopped it.</summary>
    /// <param name="flow">The flow under test.</param>
    /// <returns>The paths, keyed as <see cref="Capture"/> keys them.</returns>
    public IReadOnlyList<string> UntouchedPaths(SquadCrashFlow flow) => flow switch
    {
        SquadCrashFlow.Install => [RelativeKey(ForeignPath), RelativeKey(AdoptedPath)],
        SquadCrashFlow.Update => [RelativeKey(ForeignPath), RelativeKey(AdoptedPath), RelativeKey(EditedPath)],

        // Uninstall removes the adopted file as it removes every file the install owns and the host has not changed.
        SquadCrashFlow.Uninstall => [RelativeKey(ForeignPath), RelativeKey(EditedPath)],
        _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, null)
    };

    /// <summary>Builds the plan the flow commits.</summary>
    /// <param name="flow">The flow to plan.</param>
    /// <returns>The plan.</returns>
    protected SquadDeploymentPlan CreatePlan(SquadCrashFlow flow) => flow switch
    {
        SquadCrashFlow.Install => Planner.CreateInstall(
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
        SquadCrashFlow.Update => Planner.CreateUpdate(
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
        SquadCrashFlow.Uninstall => Planner.CreateUninstall(
            Root,
            SquadDeploymentScope.Project,
            BaselineReceipt()),
        _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, null)
    };

    /// <summary>
    /// Builds the plan the next command would, from the receipt now on disk as the lifecycle reads it: nothing to
    /// run (null) when the flow has nothing left to do, such as an install over an installed tree.
    /// </summary>
    /// <param name="flow">The flow to run again.</param>
    /// <returns>The plan, or null when there is nothing to run.</returns>
    protected SquadDeploymentPlan? CreateNextPlan(SquadCrashFlow flow)
    {
        // The tool folder, or a journal in it, is what tells a tree caught mid-transaction from one with nothing left to do.
        bool unfinished = Capture().Keys.Any(key =>
            string.Equals(key, ToolFolder, StringComparison.Ordinal) ||
            key.StartsWith(ToolFolder + "/", StringComparison.Ordinal));
        SquadReceipt? onDisk = StateStore.ReadReceipt(Root, Scope);
        if (onDisk is null)
        {
            return flow == SquadCrashFlow.Install || unfinished ? CreatePlan(flow) : null;
        }

        return flow switch
        {
            // An install over an installed tree is refused before any deployment, so the next run is the update
            // that keeps what is there; it still has a transaction to start when a journal was left behind.
            SquadCrashFlow.Install => unfinished
                ? Planner.CreateUpdate(
                    Root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    [
                        Rendered(ConductorPath, "installed body"),
                        Rendered(AdoptedPath, "adopted body"),
                        Rendered(EditedPath, "installed edited body")
                    ],
                    onDisk,
                    [],
                    replaceManaged: false,
                    new FixedTimeProvider(InstalledAt))
                : null,
            SquadCrashFlow.Update => Planner.CreateUpdate(
                Root,
                SquadDeploymentScope.Project,
                Lock("1.2.5"),
                [
                    Rendered(ConductorPath, "updated body"),
                    Rendered(TwoPath, "updated two"),
                    Rendered(AdoptedPath, "adopted body"),
                    Rendered(EditedPath, "updated edited body")
                ],
                onDisk,
                [],
                replaceManaged: false,
                new FixedTimeProvider(InstalledAt.AddDays(1))),
            SquadCrashFlow.Uninstall => Planner.CreateUninstall(Root, SquadDeploymentScope.Project, onDisk),
            _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, null)
        };
    }

    /// <summary>The path of a baseline file under <see cref="Root"/>.</summary>
    /// <param name="relativePath">The '/'-separated path.</param>
    /// <returns>The absolute path.</returns>
    protected string PathOf(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relativePath, string content) => WriteText(PathOf(relativePath), content);

    /// <summary>A snapshot key for a '/'-separated path.</summary>
    /// <param name="relativePath">The path.</param>
    /// <returns>The key.</returns>
    protected static string RelativeKey(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The number of directories above a snapshot key.</summary>
    /// <param name="relativePath">The key.</param>
    /// <returns>The depth.</returns>
    protected static int Depth(string relativePath) =>
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

    private static SquadReceipt BaselineReceipt() =>
        new(
            SquadStateSchemas.ReceiptSchemaV1,
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

    private static readonly DateTimeOffset InstalledAt = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

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
    private readonly TempDirectory _temp = new();
    private string? _committedPoint;

    /// <inheritdoc />
    public override string Root => _temp.Path;

    /// <inheritdoc />
    public override SquadStateStore StateStore => RealSquadState.Store(new FakeSquadUserPaths(Root));

    /// <inheritdoc />
    public override SquadDeploymentScope Scope => SquadDeploymentScope.Project;

    /// <inheritdoc />
    protected override SquadDeploymentPlanner Planner => RealSquadDeployment.Planner;

    /// <inheritdoc />
    protected override void PrepareRoot()
    {
        RequireEveryStatePathUnderRoot();
        Directory.CreateDirectory(Root);
    }

    /// <inheritdoc />
    protected override void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> ExecuteClean(SquadCrashFlow flow)
    {
        RecordingObserver observer = new(Capture);
        RealSquadDeployment.Transaction(StateStore, observer).Execute(CreatePlan(flow));
        _committedPoint = observer.CommittedPoint;
        return observer.FaultPoints;
    }

    /// <inheritdoc />
    public override string? ExecuteUnderFault(SquadCrashFlow flow, string faultPoint)
    {
        KillingObserver observer = new(this, faultPoint);
        string? problem = null;
        try
        {
            RealSquadDeployment.Transaction(StateStore, observer).Execute(CreatePlan(flow));
            problem = "The flow completed without reaching the fault point.";
        }
        catch (SquadCrashInjectedException)
        {
        }
        // The fixture's boundary: report whatever the transaction threw instead of crashing the test host.
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
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
        // A failure at or after the commit marker is a committed deployment whose cleanup failed, not one to roll
        // back: the receipt is already published, and the kill sweep proves the tree it leaves recovers forward.
        if (string.Equals(faultPoint, _committedPoint, StringComparison.Ordinal))
            return null;

        RecordingObserver observer = new(failAt: faultPoint);
        try
        {
            RealSquadDeployment.Transaction(StateStore, observer).Execute(CreatePlan(flow));
            return "The flow completed without reaching the fault point.";
        }
        catch (SquadCrashInjectedException)
        {
            return null;
        }
        // The fixture's boundary: report whatever the transaction threw instead of crashing the test host.
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            return $"The transaction reported the failure as {exception.GetType().Name}: {exception.Message}";
        }
    }

    /// <inheritdoc />
    public override void Recover()
    {
        RealSquadDeployment.Transaction(StateStore).Recover(Root, SquadDeploymentScope.Project);
        RealSquadDeployment.Transaction(StateStore).Recover(Root, SquadDeploymentScope.Project);
    }

    /// <inheritdoc />
    public override void ExecuteNext(SquadCrashFlow flow)
    {
        if (CreateNextPlan(flow) is { } plan)
        {
            RealSquadDeployment.Transaction(StateStore).Execute(plan);
        }
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, SquadTreeEntry> Capture()
    {
        SortedDictionary<string, SquadTreeEntry> tree = new(StringComparer.Ordinal);
        CaptureDirectory(new DirectoryInfo(Root), Root, tree, StateStore.ResolveHomeStateDirectory());
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
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _temp.Dispose();
        }

        base.Dispose(disposing);
    }

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
            SquadPhysicalRootIdentities.Resolve(Root, SquadFileSystemPathSemantics.Instance).PhysicalPath);
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

    private void CaptureDirectory(
        DirectoryInfo directory,
        string root,
        IDictionary<string, SquadTreeEntry> tree,
        string registryDirectory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            string relativePath = RelativeKey(Path.GetRelativePath(root, entry.FullName));
            if (IsRegistryFile(entry, registryDirectory))
                continue;
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
                CaptureDirectory(childDirectory, root, tree, registryDirectory);
                continue;
            }

            tree.Add(
                relativePath,
                new SquadTreeEntry(SquadTreeEntryKind.File, File.ReadAllBytes(entry.FullName)));
        }
    }

    /// <summary>
    /// The home-level project registry, which a snapshot leaves out: it is a cache written after the commit and
    /// rebuilt by a later status run, so a kill after the commit marker leaves a whole tree without it. It is not
    /// content the deployment installs.
    /// </summary>
    /// <param name="entry">The entry under the root.</param>
    /// <param name="registryDirectory">The home state directory the registry sits in.</param>
    /// <returns>True for the registry file itself.</returns>
    private static bool IsRegistryFile(FileSystemInfo entry, string registryDirectory)
    {
        if (!string.Equals(entry.Name, "registry.json", StringComparison.Ordinal))
            return false;

        SquadFileSystemPathSemantics semantics = SquadFileSystemPathSemantics.Instance;
        return string.Equals(
            semantics.Canonicalize(Path.GetDirectoryName(entry.FullName)!),
            semantics.Canonicalize(registryDirectory),
            StringComparison.Ordinal);
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

        public string? CommittedPoint { get; private set; }

        public void AfterStep(SquadTransactionStep transactionStep)
        {
            _steps++;
            Record($"step:{Invariant(transactionStep.Sequence)}");
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            _checkpoints++;
            string point = $"checkpoint:{Invariant(checkpoint.Sequence)}";
            if (checkpoint.Kind == SquadTransactionCheckpointKind.Committed)
                CommittedPoint = point;
            Record(point);
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

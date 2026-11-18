using System.Globalization;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// The crash-safety workspace over the in-memory file system: every mutating file-system operation is a fault point,
/// so a flow is stopped inside a write, a rename and a deletion, which the real disk can reach only at the steps and
/// checkpoints the transaction reports.
/// </summary>
/// <remarks>
/// A kill makes the fake refuse every later operation until <see cref="InMemoryFileSystem.ClearFaults"/>, so the
/// cleanup a <c>catch</c> or <c>finally</c> block would run cannot repair the tree: it is left as a dead process leaves
/// it. A handled failure throws once and lets the transaction roll back.
/// </remarks>
public sealed class InMemorySquadCrashSafetyFixture : SquadCrashSafetyFixture
{
    private static readonly HashSet<string> MutatingOperations = new(StringComparer.Ordinal)
    {
        nameof(InMemoryFileSystem.WriteAllText),
        nameof(InMemoryFileSystem.WriteAllBytes),
        nameof(InMemoryFileSystem.WriteAllTextAtomic),
        nameof(InMemoryFileSystem.WriteAllBytesAtomic),
        nameof(InMemoryFileSystem.MoveFile),
        nameof(InMemoryFileSystem.MoveDirectory),
        nameof(InMemoryFileSystem.DeleteFile),
        nameof(InMemoryFileSystem.DeleteTreeNoFollow),
        nameof(InMemoryFileSystem.DeleteEmptyDirectory),
        nameof(InMemoryFileSystem.CreateDirectory),
        nameof(InMemoryFileSystem.CreateNewFile),
        nameof(InMemoryFileSystem.TryCreateNewFile),
        nameof(InMemoryFileSystem.OpenWriteNew),
        nameof(InMemoryFileSystem.OpenExclusive),
        nameof(InMemoryFileSystem.OpenAppendShared),
        nameof(InMemoryFileSystem.CreateFileLink),
        nameof(InMemoryFileSystem.CreateDirectoryLink)
    };

    // The project registry is a cache written after the commit and rebuilt by a later status run, so a kill between
    // the commit and that write leaves a whole new tree without it. A kill before the journal exists can leave the
    // empty state folders its run had made, which hold nothing for a later run to recover. Neither is a difference
    // in what the deployment installed, so both are left out of every snapshot; the lock and receipt files are not.
    private const string RegistryKey = "home/.kyber-weave/squad/registry.json";
    private const string StateFolderPrefix = "home";

    private readonly InMemorySquadState _state;
    private readonly string _root;
    private int _commitOrdinal;

    /// <summary>
    /// Creates a workspace with an empty project directory. The state sits inside it, as in the real-disk workspace,
    /// so every snapshot and restore covers the lock and receipt as well as the project's files.
    /// </summary>
    public InMemorySquadCrashSafetyFixture()
    {
        _state = new InMemorySquadState(stateRoot: "/kw/projects/crash");
        _root = _state.CreateRoot("crash");
    }

    /// <inheritdoc />
    public override string Root => _root;

    /// <inheritdoc />
    public override SquadStateStore StateStore => _state.Store;

    /// <inheritdoc />
    public override SquadDeploymentScope Scope => SquadDeploymentScope.Project;

    /// <summary>Gets the simulated machine whose lease table the transactions contend in.</summary>
    public KyberWeave.Tests.Fakes.InProcessLeaseHost LeaseHost => _state.LeaseHost;

    /// <summary>Gets the file system, so a test can plant a file or add a fault of its own.</summary>
    public InMemoryFileSystem Files => _state.Files;

    /// <inheritdoc />
    protected override SquadDeploymentPlanner Planner => _state.Planner;

    /// <summary>Builds a transaction over this workspace.</summary>
    /// <param name="observer">Told of each durable step; none when null.</param>
    /// <returns>The transaction.</returns>
    public SquadTransaction Transaction(ISquadTransactionObserver? observer = null) => _state.Transaction(observer);

    /// <summary>Builds the plan for a flow, as the next run would commit it.</summary>
    /// <param name="flow">The flow.</param>
    /// <returns>The plan.</returns>
    public SquadDeploymentPlan Plan(SquadCrashFlow flow) => CreatePlan(flow);

    /// <inheritdoc />
    protected override void PrepareRoot() => _state.Files.CreateDirectory(Root);

    /// <inheritdoc />
    protected override void WriteText(string path, string content)
    {
        _state.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _state.Files.WriteAllText(path, content);
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> ExecuteClean(SquadCrashFlow flow)
    {
        SquadDeploymentPlan plan = CreatePlan(flow);
        int mutating = 0;
        _state.Files.AddFault(FileSystemFault.FailWhen(operation =>
        {
            if (IsMutating(operation))
            {
                mutating++;
            }

            return false;
        }));
        CommitObserver observer = new(() => mutating);
        _state.Transaction(observer).Execute(plan);
        _state.Files.ClearFaults();
        _commitOrdinal = observer.CommitOrdinal;
        return [.. Enumerable.Range(1, mutating).Select(Point)];
    }

    /// <inheritdoc />
    public override string? ExecuteUnderFault(SquadCrashFlow flow, string faultPoint)
    {
        int ordinal = Ordinal(faultPoint);
        SquadDeploymentPlan plan = CreatePlan(flow);
        int mutating = 0;
        _state.Files.KillWhen(operation => IsMutating(operation) && ++mutating == ordinal);
        string? problem = null;
        try
        {
            _state.Transaction().Execute(plan);
            problem = "The flow completed without reaching the fault point.";
        }
        catch (Exception exception) when (IsKill(exception))
        {
        }
        catch (Exception exception)
        {
            problem = $"The flow reported the fault as {exception.GetType().Name}: {exception.Message}";
        }

        _state.Files.ClearFaults();
        return problem;
    }

    /// <inheritdoc />
    public override string? ExecuteToRollback(SquadCrashFlow flow, string faultPoint)
    {
        int ordinal = Ordinal(faultPoint);
        if (ordinal > _commitOrdinal)
        {
            // A failure after the receipt is published is a commit that did not finish cleaning up, not a deployment
            // to roll back; the kill sweep proves the tree it leaves recovers.
            return null;
        }

        SquadDeploymentPlan plan = CreatePlan(flow);
        int mutating = 0;
        _state.Files.FailWhen(
            operation => IsMutating(operation) && ++mutating == ordinal,
            new IOException($"The disk failed at mutating operation {ordinal}."));
        try
        {
            _state.Transaction().Execute(plan);
            return "The flow completed without reaching the fault point.";
        }
        catch (Exception exception) when (!IsKill(exception))
        {
            return null;
        }
        finally
        {
            _state.Files.ClearFaults();
        }
    }

    /// <inheritdoc />
    public override void Recover()
    {
        _state.Transaction().Recover(Root, SquadDeploymentScope.Project);
        _state.Transaction().Recover(Root, SquadDeploymentScope.Project);
    }

    private static bool IsLeftOutOfSnapshots(KeyValuePair<string, SquadTreeEntry> entry) =>
        string.Equals(entry.Key, RegistryKey, StringComparison.Ordinal) ||
        (entry.Value.Kind == SquadTreeEntryKind.Directory &&
         (string.Equals(entry.Key, StateFolderPrefix, StringComparison.Ordinal) ||
          entry.Key.StartsWith(StateFolderPrefix + "/", StringComparison.Ordinal)));

    /// <inheritdoc />
    public override void ExecuteNext(SquadCrashFlow flow)
    {
        if (CreateNextPlan(flow) is { } plan)
        {
            _state.Transaction().Execute(plan);
        }
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, SquadTreeEntry> Capture() =>
        SquadStateTreeCapture.Capture(_state.Files, Root)
            .Where(entry => !IsLeftOutOfSnapshots(entry))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    /// <inheritdoc />
    public override void Restore(IReadOnlyDictionary<string, SquadTreeEntry> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        InMemoryFileSystem files = _state.Files;
        files.CreateDirectory(Root);
        foreach (FileSystemEntry entry in files.EnumerateEntries(Root).ToList())
        {
            files.DeleteTreeNoFollow(entry.Path);
        }

        foreach ((string key, SquadTreeEntry entry) in tree.OrderBy(pair => Depth(pair.Key)).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string path = PathOf(key);
            switch (entry.Kind)
            {
                case SquadTreeEntryKind.Directory:
                    files.CreateDirectory(path);
                    break;
                case SquadTreeEntryKind.File:
                    files.CreateDirectory(Path.GetDirectoryName(path)!);
                    files.WriteAllBytes(path, (entry.Content ?? ReadOnlyMemory<byte>.Empty).ToArray());
                    break;
                default:
                    files.CreateDirectory(Path.GetDirectoryName(path)!);
                    string target = entry.LinkTarget
                        ?? throw new InvalidOperationException($"The snapshot's link '{key}' has no target.");
                    if (entry.Kind == SquadTreeEntryKind.DirectorySymbolicLink)
                    {
                        files.CreateDirectoryLink(path, target);
                    }
                    else
                    {
                        files.CreateFileLink(path, target);
                    }

                    break;
            }
        }

        string? difference = SquadTreeComparison.Difference(tree, Capture());
        if (difference is not null)
        {
            throw new InvalidOperationException(
                $"The snapshot did not restore exactly, so the next case would run on a contaminated tree: {difference}");
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Whether an exception is the fake's own stop.</summary>
    /// <param name="exception">The exception a run threw.</param>
    /// <returns>True for a kill, possibly wrapped.</returns>
    public static bool IsKill(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is InMemoryFileSystemKilledException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Names the fault point at one mutating operation.</summary>
    /// <param name="ordinal">The one-based number among the mutating operations of a run.</param>
    /// <returns>The point's name.</returns>
    public static string Point(int ordinal) => $"op:{ordinal.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Whether an operation changes the tree.</summary>
    /// <param name="operation">The operation about to run.</param>
    /// <returns>True for a write, move, deletion or creation.</returns>
    public static bool IsMutating(FileSystemOperation operation) => MutatingOperations.Contains(operation.Name);

    private static int Ordinal(string faultPoint) =>
        int.Parse(faultPoint["op:".Length..], CultureInfo.InvariantCulture);

    private sealed class CommitObserver(Func<int> mutatingSoFar) : ISquadTransactionObserver
    {
        public int CommitOrdinal { get; private set; }

        public void AfterStep(SquadTransactionStep transactionStep)
        {
            if (transactionStep.Kind == SquadTransactionStepKind.ReceiptApplied)
            {
                CommitOrdinal = mutatingSoFar();
            }
        }
    }
}

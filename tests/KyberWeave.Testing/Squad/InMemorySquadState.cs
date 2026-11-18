using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// The Squad state store and migration built over an in-memory file system, path resolver, lease host and clock:
/// the whole state layout in a unit test, with no disk, no mutex and no wall clock.
/// </summary>
public sealed class InMemorySquadState
{
    /// <summary>The instant the fake clock starts at.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a state over a file system and lease host of its own.</summary>
    /// <param name="leaseHost">The simulated machine whose lease table the migration contends in; a new one when null.</param>
    public InMemorySquadState(InProcessLeaseHost? leaseHost = null)
    {
        Files = new InMemoryFileSystem(caseSensitive: true);
        string home = Path.GetFullPath("/kw/home");
        string applicationData = Path.GetFullPath("/kw/application-data");
        Files.CreateDirectory(home);
        Files.CreateDirectory(applicationData);
        UserPaths = new FakeSquadUserPaths(applicationData, home);
        Paths = new FakePathResolver(Files);
        Semantics = new SquadFileSystemPathSemantics(Paths);
        Environment = new FakeProcessEnvironment(currentDirectory: Path.GetFullPath("/kw/work"));
        Time = new FakeTimeProvider(Start);
        LeaseHost = leaseHost ?? new InProcessLeaseHost();
        Store = new SquadStateStore(UserPaths, Files, Semantics, Paths, Environment);
        Migration = new SquadStateMigration(Store, UserPaths, Files, Semantics, new InProcessLease(LeaseHost), Time);
        Layout = new SquadStateTestLayout(Store, Files, Semantics);
        Planner = new SquadDeploymentPlanner(Files, Semantics, Paths);
    }

    /// <summary>Gets the file system everything reads and writes.</summary>
    public InMemoryFileSystem Files { get; }

    /// <summary>Gets the user paths the state is anchored to.</summary>
    public FakeSquadUserPaths UserPaths { get; }

    /// <summary>Gets the path seam over <see cref="Files"/>.</summary>
    public FakePathResolver Paths { get; }

    /// <summary>Gets the deployment's path semantics over <see cref="Paths"/>.</summary>
    public SquadFileSystemPathSemantics Semantics { get; }

    /// <summary>Gets the process environment.</summary>
    public FakeProcessEnvironment Environment { get; }

    /// <summary>Gets the clock.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>Gets the simulated machine whose leases the migration contends in.</summary>
    public InProcessLeaseHost LeaseHost { get; }

    /// <summary>Gets the store.</summary>
    public SquadStateStore Store { get; }

    /// <summary>Gets the migration.</summary>
    public SquadStateMigration Migration { get; }

    /// <summary>Gets where the state lives and how a test writes into it.</summary>
    public SquadStateTestLayout Layout { get; }

    /// <summary>Gets the deployment planner over the in-memory file system.</summary>
    public SquadDeploymentPlanner Planner { get; }

    /// <summary>Builds a deployment transaction over the in-memory file system, lease host and path rules.</summary>
    /// <param name="observer">Told of each durable step; none when null.</param>
    /// <returns>A new transaction.</returns>
    public SquadTransaction Transaction(ISquadTransactionObserver? observer = null) =>
        new(Store, Files, Semantics, new InProcessLease(LeaseHost), new FakeIdGenerator(), observer);

    /// <summary>Creates a project directory and returns its canonical path.</summary>
    /// <param name="name">The directory name under the projects folder.</param>
    /// <returns>The physical path.</returns>
    public string CreateRoot(string name)
    {
        string root = Path.GetFullPath($"/kw/projects/{name}");
        Files.CreateDirectory(root);
        return Layout.PhysicalOf(root);
    }

    /// <summary>Snapshots every path in the file system.</summary>
    /// <returns>The tree, keyed by '/'-separated path in ordinal order.</returns>
    public IReadOnlyDictionary<string, SquadTreeEntry> Capture() =>
        SquadStateTreeCapture.Capture(Files, Path.GetFullPath("/"));
}

using KyberWeave.Contracts.Host;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Every writer of the home-level project registry takes the registry lease inside the state store, so a writer is
/// covered whether it is the transaction, the migration or a listing that rebuilds a lost entry.
/// </summary>
public sealed class SquadRegistryLeaseTests
{
    private readonly InProcessLeaseHost _host = new();
    private readonly SpyLease _spy;
    private readonly InMemorySquadState _state;

    public SquadRegistryLeaseTests()
    {
        _spy = new SpyLease(new InProcessLease(_host));
        _state = new InMemorySquadState(_host, _spy);
    }

    private string RegistryLease => SquadLeaseNames.ForRegistry(_state.Store.ResolveHomeStateDirectory());

    [Fact]
    public void RegisteringAProjectTakesTheRegistryLease()
    {
        _state.Store.RegisterProject(_state.CreateRoot("alpha"), ["codex"], SquadStateTestLayout.InstalledAt);

        Assert.Contains(RegistryLease, _spy.Names);
    }

    [Fact]
    public void UnregisteringAProjectTakesTheRegistryLease()
    {
        string root = _state.CreateRoot("alpha");
        _state.Store.RegisterProject(root, ["codex"], SquadStateTestLayout.InstalledAt);
        _spy.Names.Clear();

        Assert.True(_state.Store.UnregisterProject(root));

        Assert.Contains(RegistryLease, _spy.Names);
    }

    [Fact]
    public void ListingThatRebuildsALostEntryTakesTheRegistryLeaseToWriteItBack()
    {
        string root = _state.CreateRoot("alpha");
        SquadReceipt receipt = new(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Project,
            root,
            SquadStateTestLayout.InstalledAt,
            [],
            [SquadStateTestLayout.OwnedFile(".codex/agents/alpha.toml", "alpha")]);
        _state.Layout.WriteText(
            Path.Combine(_state.Layout.HomeProjectDirectory(root), "squad.receipt.json"),
            _state.Store.SerializeReceipt(receipt));

        _ = _state.Store.ListProjects();

        Assert.Contains(RegistryLease, _spy.Names);
    }

    [Fact]
    public void ListingThatRebuildsNothingTakesNoLease()
    {
        _ = _state.Store.ListProjects();

        Assert.DoesNotContain(RegistryLease, _spy.Names);
    }

    [Fact]
    public void ImportingAProjectRegistersItUnderTheRegistryLease()
    {
        string repo = _state.CreateRoot("repo");
        _state.Layout.WriteInRepoState(repo, SquadStateTestLayout.Lock());

        _ = _state.Migration.ImportProject(repo);

        Assert.Contains(RegistryLease, _spy.Names);
    }

    [Fact]
    public void AWriterWaitsForAnotherWriterThatReleasesWithinTheTimeout()
    {
        string root = _state.CreateRoot("alpha");
        IDisposable other = new InProcessLease(_host).Acquire(RegistryLease, TimeSpan.Zero);
        Thread release = new(() =>
        {
            Thread.Sleep(60);
            other.Dispose();
        });
        release.Start();

        _state.Store.RegisterProject(root, ["codex"], SquadStateTestLayout.InstalledAt);

        release.Join();
        Assert.NotNull(_state.Store.FindProject(root));
    }

    private sealed class SpyLease(ILease inner) : ILease
    {
        public List<string> Names { get; } = [];

        public IDisposable Acquire(string name, TimeSpan timeout)
        {
            Names.Add(name);
            return inner.Acquire(name, timeout);
        }
    }
}

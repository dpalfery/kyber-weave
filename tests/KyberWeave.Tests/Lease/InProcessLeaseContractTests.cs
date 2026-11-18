using KyberWeave.Contracts.Host;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Lease;

/// <summary>Runs the lease contract against the in-process fake, so unit tests can trust its contention reports.</summary>
public sealed class InProcessLeaseContractTests : LeaseContractTests
{
    private readonly InProcessLeaseHost _host = new();

    /// <inheritdoc />
    protected override ILease CreateLease() => new InProcessLease(_host);

    /// <inheritdoc />
    protected override (ILease Holder, ILease Contender) CreateTwoLeasesOnOneMachine() =>
        (new InProcessLease(_host), new InProcessLease(_host));

    /// <inheritdoc />
    protected override IDisposable HoldElsewhere(string name) => new InProcessLease(_host).Hold(name);

    [Fact]
    public void IndependentFakesDoNotShareLeases()
    {
        InProcessLease first = new();
        InProcessLease second = new();
        string name = $"kyber-weave-independent-lease-{Guid.NewGuid():N}";

        using IDisposable firstLease = first.Acquire(name, GenerousTimeout);
        using IDisposable secondLease = second.Acquire(name, GenerousTimeout);
    }
}

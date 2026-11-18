using KyberWeave.Contracts.Host;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Lease;

/// <summary>Runs the lease contract against the in-process fake, so unit tests can trust its contention reports.</summary>
public sealed class InProcessLeaseContractTests : LeaseContractTests
{
    private readonly InProcessLease _lease = new();

    /// <inheritdoc />
    protected override ILease CreateLease() => _lease;

    /// <inheritdoc />
    protected override IDisposable HoldElsewhere(string name) => _lease.Hold(name);
}

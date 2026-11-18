using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Credentials;

/// <summary>
/// The availability probe decides skip-or-run for the integration contract suite. It must never turn a
/// broken store into a skip, or CI stays green while the adapter is wrong.
/// </summary>
public sealed class CredentialStoreAvailabilityTests
{
    private const string ProbeOrigin = "https://kyber-weave-contract-probe.invalid";

    [Fact]
    public void ReportsAStoreThatAnswersAnAbsentOriginAsAvailable()
    {
        Assert.Null(CredentialStoreAvailability.Probe(new FakeCredentialStore(), ProbeOrigin));
    }

    [Fact]
    public void ReportsAStoreThatAnswersAnAbsentOriginWithAValueAsAvailable()
    {
        FakeCredentialStore store = new();
        store.Seed(ProbeOrigin, "sk-should-not-be-there");

        Assert.Null(CredentialStoreAvailability.Probe(store, ProbeOrigin));
    }

    [Fact]
    public void ReportsAMissingSecretToolAsUnavailable()
    {
        FakeCredentialStore store = new() { ReadFailure = new SecretServiceUnavailableException() };

        Assert.NotNull(CredentialStoreAvailability.Probe(store, ProbeOrigin));
    }

    [Fact]
    public void ReportsALockedKeychainAsUnavailable()
    {
        FakeCredentialStore store = new() { ReadFailure = new KeychainUnavailableException("The keychain is locked.") };

        Assert.NotNull(CredentialStoreAvailability.Probe(store, ProbeOrigin));
    }

    [Fact]
    public void ReportsAPlatformWithoutAStoreAsUnavailable()
    {
        FakeCredentialStore store = new() { ReadFailure = new PlatformNotSupportedException() };

        Assert.NotNull(CredentialStoreAvailability.Probe(store, ProbeOrigin));
    }

    [Fact]
    public void ReportsAToolThatCannotStartAsUnavailable()
    {
        FakeCredentialStore store = new() { ReadFailure = new System.ComponentModel.Win32Exception("No such file or directory") };

        Assert.NotNull(CredentialStoreAvailability.Probe(store, ProbeOrigin));
    }

    [Fact]
    public void RaisesAnUnexpectedFailureInsteadOfSkippingIt()
    {
        FakeCredentialStore store = new()
        {
            ReadFailure = new InvalidOperationException("The store answered the probe with the wrong key."),
        };

        _ = Assert.Throws<InvalidOperationException>(() => CredentialStoreAvailability.Probe(store, ProbeOrigin));
    }

    [Fact]
    public void NeverWritesAProbeKey()
    {
        FakeCredentialStore store = new();

        _ = CredentialStoreAvailability.Probe(store, ProbeOrigin);

        Assert.Empty(store.Writes);
    }
}

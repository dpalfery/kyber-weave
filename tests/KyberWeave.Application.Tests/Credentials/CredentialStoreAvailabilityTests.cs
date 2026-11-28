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
    public async Task ReportsAStoreThatAnswersAnAbsentOriginAsAvailable()
    {
        Assert.Null(await CredentialStoreAvailability.ProbeAsync(new FakeCredentialStore(), ProbeOrigin));
    }

    [Fact]
    public async Task ReportsAStoreThatAnswersAnAbsentOriginWithAValueAsAvailable()
    {
        FakeCredentialStore store = new();
        store.Seed(ProbeOrigin, "sk-should-not-be-there");

        Assert.Null(await CredentialStoreAvailability.ProbeAsync(store, ProbeOrigin));
    }

    [Fact]
    public async Task ReportsAMissingSecretToolAsUnavailable()
    {
        FakeCredentialStore store = new() { ReadFailure = new SecretServiceUnavailableException() };

        Assert.NotNull(await CredentialStoreAvailability.ProbeAsync(store, ProbeOrigin));
    }

    [Fact]
    public async Task ReportsALockedKeychainAsUnavailable()
    {
        FakeCredentialStore store = new() { ReadFailure = new KeychainUnavailableException("The keychain is locked.") };

        Assert.NotNull(await CredentialStoreAvailability.ProbeAsync(store, ProbeOrigin));
    }

    [Fact]
    public async Task ReportsAPlatformWithoutAStoreAsUnavailable()
    {
        FakeCredentialStore store = new() { ReadFailure = new PlatformNotSupportedException() };

        Assert.NotNull(await CredentialStoreAvailability.ProbeAsync(store, ProbeOrigin));
    }

    [Fact]
    public async Task ReportsAToolThatCannotStartAsUnavailable()
    {
        FakeCredentialStore store = new() { ReadFailure = new System.ComponentModel.Win32Exception("No such file or directory") };

        Assert.NotNull(await CredentialStoreAvailability.ProbeAsync(store, ProbeOrigin));
    }

    [Fact]
    public async Task RaisesAnUnexpectedFailureInsteadOfSkippingIt()
    {
        FakeCredentialStore store = new()
        {
            ReadFailure = new InvalidOperationException("The store answered the probe with the wrong key."),
        };

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => CredentialStoreAvailability.ProbeAsync(store, ProbeOrigin));
    }

    [Fact]
    public async Task NeverWritesAProbeKey()
    {
        FakeCredentialStore store = new();

        _ = await CredentialStoreAvailability.ProbeAsync(store, ProbeOrigin);

        Assert.Empty(store.Writes);
    }
}

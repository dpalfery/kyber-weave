using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Data.Arbiter.Credentials;
using KyberWeave.Data.Host;
using KyberWeave.Tests.Credentials;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>
/// Runs the credential contract against the store this operating system offers. The suite skips when the host has no
/// usable store, which is the usual case for a Linux runner without <c>libsecret-tools</c> and for a locked keychain.
/// </summary>
public sealed class OperatingSystemCredentialStoreContractTests : CredentialStoreContractTests
{
    private string? _unavailable;

    /// <summary>Probes the host's store once, so each test skips rather than fails where there is none.</summary>
    /// <returns>A task that completes once the probe has answered.</returns>
    public new async ValueTask InitializeAsync()
    {
        await base.InitializeAsync().ConfigureAwait(false);
        _unavailable = await CredentialStoreAvailability.ProbeAsync(
            OperatingSystemCredentialStore.Create(new ProcessEnvironment()),
            $"https://kyber-weave-contract-probe-{Guid.NewGuid():N}.invalid").ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ICredentialStore CreateStore()
    {
        if (_unavailable is { } reason)
        {
            Assert.Skip(reason);
        }

        return OperatingSystemCredentialStore.Create(new ProcessEnvironment());
    }
}

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
    private static readonly Lazy<string?> Unavailable = new(() =>
        CredentialStoreAvailability.Probe(
            OperatingSystemCredentialStore.Create(new ProcessEnvironment()),
            $"https://kyber-weave-contract-probe-{Guid.NewGuid():N}.invalid"));

    /// <inheritdoc />
    protected override ICredentialStore CreateStore()
    {
        if (Unavailable.Value is { } reason)
        {
            Assert.Skip(reason);
        }

        return OperatingSystemCredentialStore.Create(new ProcessEnvironment());
    }
}

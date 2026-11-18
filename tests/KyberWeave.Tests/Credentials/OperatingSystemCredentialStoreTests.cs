using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Data.Arbiter.Credentials;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Credentials;

/// <summary>
/// The store the selector returns is decided by the environment seam, so a test states the platform instead of
/// running on it.
/// </summary>
public sealed class OperatingSystemCredentialStoreTests
{
    [Theory]
    [InlineData(OperatingSystemKind.Windows, typeof(WindowsCredentialStore))]
    [InlineData(OperatingSystemKind.MacOS, typeof(MacKeychainCredentialStore))]
    [InlineData(OperatingSystemKind.Linux, typeof(SecretServiceCredentialStore))]
    [InlineData(OperatingSystemKind.FreeBSD, typeof(SecretServiceCredentialStore))]
    [InlineData(OperatingSystemKind.Unknown, typeof(SecretServiceCredentialStore))]
    public void ReturnsTheStoreThePlatformOffers(OperatingSystemKind platform, Type expected)
    {
        FakeProcessEnvironment environment = new(operatingSystem: platform);

        ICredentialStore store = OperatingSystemCredentialStore.Create(environment);

        Assert.IsType(expected, store);
    }

    [Fact]
    public void RejectsAMissingEnvironment()
    {
        _ = Assert.Throws<ArgumentNullException>(() => OperatingSystemCredentialStore.Create(null!));
    }
}

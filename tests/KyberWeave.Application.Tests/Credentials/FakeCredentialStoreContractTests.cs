using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Credentials;

/// <summary>Runs the credential contract against the fake, so unit tests can trust what it stores and reports.</summary>
public sealed class FakeCredentialStoreContractTests : CredentialStoreContractTests
{
    private readonly FakeCredentialStoreHost _host = new();

    /// <inheritdoc />
    protected override ICredentialStore CreateStore() => new FakeCredentialStore(_host);

    [Fact]
    public void IndependentFakesDoNotShareCredentials()
    {
        FakeCredentialStore first = new();
        FakeCredentialStore second = new();
        string origin = NewOrigin();

        first.Write(origin, "sk-contract-isolated");

        Assert.Null(second.Read(origin));
    }

    [Fact]
    public void RecordsTheOriginsACallerReadWriteAndDeleted()
    {
        FakeCredentialStore store = new();
        string origin = NewOrigin();

        store.Write(origin, "sk-contract-recorded");
        _ = store.Read(origin);
        store.Delete(origin);

        Assert.Equal([(origin, "sk-contract-recorded")], store.Writes);
        Assert.Equal([origin], store.Reads);
        Assert.Equal([origin], store.Deletes);
        Assert.Empty(store.Entries);
    }
}

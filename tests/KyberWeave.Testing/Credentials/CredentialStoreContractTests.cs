using KyberWeave.Core.Arbiter.Credentials;
using Xunit;

namespace KyberWeave.Tests.Credentials;

/// <summary>
/// The contract every <see cref="ICredentialStore"/> must satisfy. Run it once against the fake and once against the
/// store the operating system offers, so a unit test can trust what the fake reports.
/// </summary>
/// <remarks>
/// A derived class supplies the store under test. The suite writes only origins of its own and deletes them when the
/// test ends, so the adapter run leaves nothing behind in the developer's or the runner's credential store. Keys are
/// ASCII, which is what an Arbiter key is: macOS <c>security</c> prints a password containing non-ASCII characters as
/// hex, so no store is contracted to round-trip one.
/// </remarks>
public abstract class CredentialStoreContractTests : IDisposable
{
    private readonly List<ICredentialStore> _stores = [];
    private readonly List<string> _origins = [];

    /// <summary>Creates the store under test.</summary>
    /// <returns>The credential store seam.</returns>
    protected abstract ICredentialStore CreateStore();

    /// <summary>Creates a second store that reads the same machine's credentials as <paramref name="first"/>.</summary>
    /// <param name="first">The store already used by the test.</param>
    /// <returns>The second store.</returns>
    protected virtual ICredentialStore CreateSecondStoreOverTheSameMachine(ICredentialStore first) => CreateStore();

    /// <summary>Creates a store under test and remembers it, so the suite can delete what the test wrote.</summary>
    /// <returns>The store.</returns>
    protected ICredentialStore NewStore()
    {
        ICredentialStore store = CreateStore();
        _stores.Add(store);
        return store;
    }

    /// <summary>An endpoint origin no other test uses, remembered for cleanup.</summary>
    /// <returns>The origin.</returns>
    protected string NewOrigin()
    {
        string origin = $"https://kyber-weave-contract-{Guid.NewGuid():N}.invalid";
        _origins.Add(origin);
        return origin;
    }

    /// <summary>Deletes every origin the test wrote, so the real store is left as it was found.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes every origin the test wrote, so the real store is left as it was found.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (ICredentialStore store in _stores)
            {
                foreach (string origin in _origins)
                {
                    try
                    {
                        store.Delete(origin);
                    }
                    catch (InvalidOperationException)
                    {
                        // Cleanup runs after the result is recorded, so a refusal here must not mask the test's outcome.
                    }
                    catch (PlatformNotSupportedException)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }

            _stores.Clear();
            _origins.Clear();
        }
    }

    [Fact]
    public void ReadsNoKeyForAnOriginThatWasNeverWritten()
    {
        ICredentialStore store = NewStore();

        Assert.Null(store.Read(NewOrigin()));
    }

    [Fact]
    public void ReadsBackTheKeyThatWasWritten()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();

        store.Write(origin, "sk-contract-0001");

        Assert.Equal("sk-contract-0001", store.Read(origin));
    }

    [Fact]
    public async Task ReadsBackTheKeyThatWasWrittenAsync()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();

        await store.WriteAsync(origin, "sk-contract-0001");

        Assert.Equal("sk-contract-0001", await store.ReadAsync(origin));
    }

    [Fact]
    public async Task DeletingAnOriginRemovesItsKeyAsync()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();
        await store.WriteAsync(origin, "sk-contract-0002");

        await store.DeleteAsync(origin);

        Assert.Null(await store.ReadAsync(origin));
    }

    [Fact]
    public void ReadsBackAnyKeyItAccepts()
    {
        ICredentialStore store = NewStore();
        string[] candidates =
        [
            "sk-plain-0005",
            "sk-live \"a b\" \\c-d_e",
            "sk-ünïcø∂é",
        ];

        foreach (string key in candidates)
        {
            string origin = NewOrigin();
            try
            {
                store.Write(origin, key);
            }
            catch (ArgumentException)
            {
                // A store may refuse a key it cannot carry; it must never accept one it cannot read back.
                continue;
            }

            Assert.Equal(key, store.Read(origin));
        }
    }

    [Fact]
    public void WritingASecondKeyForOneOriginReplacesTheFirst()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();

        store.Write(origin, "sk-contract-first");
        store.Write(origin, "sk-contract-second");

        Assert.Equal("sk-contract-second", store.Read(origin));
    }

    [Fact]
    public void KeepsOneOriginsKeyOutOfAnothers()
    {
        ICredentialStore store = NewStore();
        string first = NewOrigin();
        string second = NewOrigin();

        store.Write(first, "sk-contract-first");
        store.Write(second, "sk-contract-second");

        Assert.Equal("sk-contract-first", store.Read(first));
        Assert.Equal("sk-contract-second", store.Read(second));
    }

    [Fact]
    public void ReadsAKeyWithSpacesQuotesAndBackslashesUnchanged()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();
        const string key = "sk-live \"a b\" \\c-d_e";

        store.Write(origin, key);

        Assert.Equal(key, store.Read(origin));
    }

    [Fact]
    public void DeletingAnOriginRemovesItsKey()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();
        store.Write(origin, "sk-contract-0002");

        store.Delete(origin);

        Assert.Null(store.Read(origin));
    }

    [Fact]
    public void DeletingAnOriginThatWasNeverWrittenSucceeds()
    {
        ICredentialStore store = NewStore();

        store.Delete(NewOrigin());
    }

    [Fact]
    public void ReadsThroughASecondStoreOverTheSameMachine()
    {
        ICredentialStore first = NewStore();
        string origin = NewOrigin();
        first.Write(origin, "sk-contract-0003");

        ICredentialStore second = CreateSecondStoreOverTheSameMachine(first);

        Assert.Equal("sk-contract-0003", second.Read(origin));
    }

    [Fact]
    public void RejectsAnOriginWithALineBreak()
    {
        ICredentialStore store = NewStore();
        const string origin = "https://kyber-weave\ncontract.invalid";
        const string carriageReturnOrigin = "https://kyber-weave\rcontract.invalid";

        _ = Assert.ThrowsAny<ArgumentException>(() => store.Read(origin));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write(origin, "sk-contract-0006"));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Delete(origin));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Read(carriageReturnOrigin));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write(carriageReturnOrigin, "sk-contract-0006"));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Delete(carriageReturnOrigin));
    }

    [Fact]
    public void RejectsAKeyWithALineBreak()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();

        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write(origin, "line-one\nline-two"));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write(origin, "line-one\rline-two"));
    }

    [Fact]
    public void RejectsAnOriginThatIsNotGiven()
    {
        ICredentialStore store = NewStore();

        _ = Assert.ThrowsAny<ArgumentException>(() => store.Read(null!));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Read(string.Empty));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Read("  "));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write(null!, "sk-contract-0004"));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write(string.Empty, "sk-contract-0004"));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write("  ", "sk-contract-0004"));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Delete(null!));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Delete(string.Empty));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Delete("  "));
    }

    [Fact]
    public void RejectsAnEmptyKey()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();

        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write(origin, null!));
        _ = Assert.ThrowsAny<ArgumentException>(() => store.Write(origin, string.Empty));
    }
}

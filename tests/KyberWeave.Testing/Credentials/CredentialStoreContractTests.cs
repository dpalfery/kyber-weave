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
public abstract class CredentialStoreContractTests : IAsyncLifetime
{
    private readonly List<ICredentialStore> _stores = [];
    private readonly List<string> _origins = [];

    /// <summary>Nothing to set up: each test creates the stores it uses through <see cref="NewStore"/>.</summary>
    /// <returns>A completed task.</returns>
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

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
    /// <returns>A task that completes once every origin is deleted.</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (ICredentialStore store in _stores)
        {
            foreach (string origin in _origins)
            {
                try
                {
                    await store.DeleteAsync(origin).ConfigureAwait(false);
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
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ReadsNoKeyForAnOriginThatWasNeverWritten()
    {
        ICredentialStore store = NewStore();

        Assert.Null(await store.ReadAsync(NewOrigin()));
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
    public async Task ReadsBackAnyKeyItAccepts()
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
                await store.WriteAsync(origin, key);
            }
            catch (ArgumentException)
            {
                // A store may refuse a key it cannot carry; it must never accept one it cannot read back.
                continue;
            }

            Assert.Equal(key, await store.ReadAsync(origin));
        }
    }

    [Fact]
    public async Task WritingASecondKeyForOneOriginReplacesTheFirst()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();

        await store.WriteAsync(origin, "sk-contract-first");
        await store.WriteAsync(origin, "sk-contract-second");

        Assert.Equal("sk-contract-second", await store.ReadAsync(origin));
    }

    [Fact]
    public async Task KeepsOneOriginsKeyOutOfAnothers()
    {
        ICredentialStore store = NewStore();
        string first = NewOrigin();
        string second = NewOrigin();

        await store.WriteAsync(first, "sk-contract-first");
        await store.WriteAsync(second, "sk-contract-second");

        Assert.Equal("sk-contract-first", await store.ReadAsync(first));
        Assert.Equal("sk-contract-second", await store.ReadAsync(second));
    }

    [Fact]
    public async Task ReadsAKeyWithSpacesQuotesAndBackslashesUnchanged()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();
        const string key = "sk-live \"a b\" \\c-d_e";

        await store.WriteAsync(origin, key);

        Assert.Equal(key, await store.ReadAsync(origin));
    }

    [Fact]
    public async Task DeletingAnOriginThatWasNeverWrittenSucceeds()
    {
        ICredentialStore store = NewStore();

        await store.DeleteAsync(NewOrigin());
    }

    [Fact]
    public async Task ReadsThroughASecondStoreOverTheSameMachine()
    {
        ICredentialStore first = NewStore();
        string origin = NewOrigin();
        await first.WriteAsync(origin, "sk-contract-0003");

        ICredentialStore second = CreateSecondStoreOverTheSameMachine(first);

        Assert.Equal("sk-contract-0003", await second.ReadAsync(origin));
    }

    [Fact]
    public async Task RejectsAnOriginWithALineBreak()
    {
        ICredentialStore store = NewStore();
        const string origin = "https://kyber-weave\ncontract.invalid";
        const string carriageReturnOrigin = "https://kyber-weave\rcontract.invalid";

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync(origin).AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(origin, "sk-contract-0006").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(origin).AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync(carriageReturnOrigin).AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(carriageReturnOrigin, "sk-contract-0006").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(carriageReturnOrigin).AsTask());
    }

    [Fact]
    public async Task RejectsAKeyWithALineBreak()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(origin, "line-one\nline-two").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(origin, "line-one\rline-two").AsTask());
    }

    [Fact]
    public async Task RejectsAnOriginThatIsNotGiven()
    {
        ICredentialStore store = NewStore();

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync(null!).AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync(string.Empty).AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync("  ").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(null!, "sk-contract-0004").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(string.Empty, "sk-contract-0004").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync("  ", "sk-contract-0004").AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(null!).AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(string.Empty).AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync("  ").AsTask());
    }

    [Fact]
    public async Task RejectsAnEmptyKey()
    {
        ICredentialStore store = NewStore();
        string origin = NewOrigin();

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(origin, null!).AsTask());
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(origin, string.Empty).AsTask());
    }
}

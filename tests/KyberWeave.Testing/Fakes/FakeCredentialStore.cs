using KyberWeave.Core.Arbiter.Credentials;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="ICredentialStore"/>: an in-memory map from an endpoint origin to its key.</summary>
/// <remarks>
/// An operating-system credential store is machine-wide, so two stores over one <see cref="FakeCredentialStoreHost"/>
/// see the same entries. A test that needs isolation creates its own host. The recorded reads, writes and deletes
/// let a test assert what a caller asked of the store, not only what it stored.
/// </remarks>
internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly FakeCredentialStoreHost _host;

    /// <summary>Creates a store over a new, empty host.</summary>
    public FakeCredentialStore()
        : this(new FakeCredentialStoreHost())
    {
    }

    /// <summary>Creates a store over <paramref name="host"/>.</summary>
    /// <param name="host">The entries this store and any store over the same host read and write.</param>
    public FakeCredentialStore(FakeCredentialStoreHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>Gets the origins read, in the order the caller asked for them.</summary>
    public List<string> Reads { get; } = [];

    /// <summary>Gets the writes made, in the order the caller made them.</summary>
    public List<(string Origin, string Key)> Writes { get; } = [];

    /// <summary>Gets the origins deleted, in the order the caller deleted them.</summary>
    public List<string> Deletes { get; } = [];

    /// <summary>Gets the keys the host holds, so a test can assert what was stored.</summary>
    public IReadOnlyDictionary<string, string> Entries => _host.Entries;

    /// <summary>Stores a key without recording a write, for a test arranging what the store already holds.</summary>
    /// <param name="origin">The endpoint origin.</param>
    /// <param name="key">The key.</param>
    public void Seed(string origin, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrEmpty(key);
        _host.Set(origin, key);
    }

    /// <inheritdoc />
    public string? Read(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        Reads.Add(origin);
        return _host.Entries.TryGetValue(origin, out string? key) ? key : null;
    }

    /// <inheritdoc />
    public void Write(string origin, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrEmpty(key);
        Writes.Add((origin, key));
        _host.Set(origin, key);
    }

    /// <inheritdoc />
    public void Delete(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        Deletes.Add(origin);
        _ = _host.Entries.Remove(origin);
    }
}

/// <summary>The machine-wide entries two <see cref="FakeCredentialStore"/> instances share.</summary>
internal sealed class FakeCredentialStoreHost
{
    /// <summary>Creates a host with no stored key.</summary>
    public FakeCredentialStoreHost()
    {
    }

    /// <summary>Gets the keys held, by origin.</summary>
    public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

    internal void Set(string origin, string key) => Entries[origin] = key;
}

using System.Collections.Concurrent;
using KyberWeave.Contracts.Host;

namespace KyberWeave.Tests.Fakes;

/// <summary>One simulated machine: the held-lease table that every <see cref="InProcessLease"/> built from it shares.</summary>
/// <remarks>
/// A unit test builds its own host, so its leases never contend with another test's. Two fakes built without a host
/// each get a machine of their own and can hold the same name at the same time.
/// </remarks>
public sealed class InProcessLeaseHost
{
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    internal bool TryTake(string name) => _held.TryAdd(name, 0);

    internal bool TryRelease(string name) => _held.TryRemove(name, out _);
}

/// <summary>The one fake for <see cref="ILease"/>: a lease table shared by every instance built from one host.</summary>
/// <remarks>
/// <para>
/// The host stands for a machine, as the real adapter's operating-system mutex is machine-wide: two seam instances
/// built from one host contend for the same name in a unit test exactly as two adapters do in the product. Two
/// instances built without a host share nothing, so tests that run in parallel cannot block or refuse each other.
/// </para>
/// <para>
/// A contended request reports <see cref="LeaseTimeoutException"/> at once instead of waiting out its timeout, so a unit
/// test proves the caller's decision without spending the time the real lease would spend waiting.
/// </para>
/// </remarks>
public sealed class InProcessLease : ILease
{
    private readonly InProcessLeaseHost _host;

    /// <summary>Creates a lease on a machine of its own, shared with no other instance.</summary>
    public InProcessLease()
        : this(new InProcessLeaseHost())
    {
    }

    /// <summary>Creates a lease on <paramref name="host"/>, so it shares that machine's lease table with its peers.</summary>
    /// <param name="host">The simulated machine whose leases this seam sees.</param>
    public InProcessLease(InProcessLeaseHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <inheritdoc />
    public IDisposable Acquire(string name, TimeSpan timeout)
    {
        _ = LeaseNames.Validate(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        if (!_host.TryTake(name))
        {
            throw new LeaseTimeoutException(name, timeout);
        }

        return new HeldLease(_host, name);
    }

    /// <summary>Simulates another owner holding <paramref name="name"/> until the returned handle is disposed.</summary>
    /// <param name="name">The lease name.</param>
    /// <returns>The simulated owner; disposing it releases the lease.</returns>
    /// <exception cref="InvalidOperationException">The lease is already held.</exception>
    public IDisposable Hold(string name)
    {
        _ = LeaseNames.Validate(name);
        return _host.TryTake(name)
            ? new HeldLease(_host, name)
            : throw new InvalidOperationException($"The lease '{name}' is already held.");
    }

    private sealed class HeldLease(InProcessLeaseHost host, string name) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = host.TryRelease(name);
        }
    }
}

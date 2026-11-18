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
    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foreign = new(StringComparer.Ordinal);

    internal bool TryTake(string name, TimeSpan timeout, bool foreign = false)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (!_held.Add(name))
            {
                // An owner in another process is simulated, and nothing in a unit test ever releases it on its own:
                // waiting out a real timeout would only stall the test, so the request is refused at once.
                if (_foreign.Contains(name))
                {
                    return false;
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                _ = Monitor.Wait(_gate, remaining);
            }

            if (foreign)
            {
                _ = _foreign.Add(name);
            }

            return true;
        }
    }

    internal bool TryRelease(string name)
    {
        lock (_gate)
        {
            bool removed = _held.Remove(name);
            _ = _foreign.Remove(name);
            Monitor.PulseAll(_gate);
            return removed;
        }
    }
}

/// <summary>The one fake for <see cref="ILease"/>: a lease table shared by every instance built from one host.</summary>
/// <remarks>
/// <para>
/// The host stands for a machine, as the real adapter's operating-system mutex is machine-wide: two seam instances
/// built from one host contend for the same name in a unit test exactly as two adapters do in the product. Two
/// instances built without a host share nothing, so tests that run in parallel cannot block or refuse each other.
/// </para>
/// <para>
/// A request from an instance whose peer on the same host holds the name waits up to its timeout for the peer to release
/// it, as the adapter does, and is refused when the timeout passes; a zero timeout is refused at once. A name taken
/// with <see cref="InProcessLease.Hold"/> stands for an owner in another process, and is refused at once.
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

        if (!_host.TryTake(name, timeout))
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
        return _host.TryTake(name, TimeSpan.Zero, foreign: true)
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

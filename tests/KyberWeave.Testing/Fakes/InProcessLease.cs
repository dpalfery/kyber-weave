using System.Collections.Concurrent;
using KyberWeave.Contracts.Host;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="ILease"/>: an in-memory lease table shared by every caller of this instance.</summary>
/// <remarks>
/// A contended request reports <see cref="LeaseTimeoutException"/> at once instead of waiting out its timeout, so a unit
/// test proves the caller's decision without spending the time the real lease would spend waiting.
/// </remarks>
public sealed class InProcessLease : ILease
{
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IDisposable Acquire(string name, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        if (!_held.TryAdd(name, 0))
        {
            throw new LeaseTimeoutException(name, timeout);
        }

        return new HeldLease(this, name);
    }

    /// <summary>Simulates another process holding <paramref name="name"/> until the returned handle is disposed.</summary>
    /// <param name="name">The lease name.</param>
    /// <returns>The simulated owner; disposing it releases the lease.</returns>
    /// <exception cref="InvalidOperationException">This instance already holds <paramref name="name"/>.</exception>
    public IDisposable Hold(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _held.TryAdd(name, 0)
            ? new HeldLease(this, name)
            : throw new InvalidOperationException($"The lease '{name}' is already held.");
    }

    private sealed class HeldLease(InProcessLease owner, string name) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = owner._held.TryRemove(name, out _);
        }
    }
}

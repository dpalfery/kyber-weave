using System.Collections.Concurrent;
using KyberWeave.Contracts.Host;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="ILease"/>: one lease table shared by every instance in the process.</summary>
/// <remarks>
/// <para>
/// The table is static, as the real adapter's operating-system mutex is machine-wide: two seam instances created by two
/// transient services contend for the same name in a unit test exactly as two adapters do in the product.
/// </para>
/// <para>
/// A contended request reports <see cref="LeaseTimeoutException"/> at once instead of waiting out its timeout, so a unit
/// test proves the caller's decision without spending the time the real lease would spend waiting.
/// </para>
/// </remarks>
public sealed class InProcessLease : ILease
{
    private static readonly ConcurrentDictionary<string, byte> Held = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IDisposable Acquire(string name, TimeSpan timeout)
    {
        _ = LeaseNames.Validate(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        if (!Held.TryAdd(name, 0))
        {
            throw new LeaseTimeoutException(name, timeout);
        }

        return new HeldLease(name);
    }

    /// <summary>Simulates another owner holding <paramref name="name"/> until the returned handle is disposed.</summary>
    /// <param name="name">The lease name.</param>
    /// <returns>The simulated owner; disposing it releases the lease.</returns>
    /// <exception cref="InvalidOperationException">The lease is already held.</exception>
    public IDisposable Hold(string name)
    {
        _ = LeaseNames.Validate(name);
        return Held.TryAdd(name, 0)
            ? new HeldLease(name)
            : throw new InvalidOperationException($"The lease '{name}' is already held.");
    }

    private sealed class HeldLease(string name) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = Held.TryRemove(name, out _);
        }
    }
}

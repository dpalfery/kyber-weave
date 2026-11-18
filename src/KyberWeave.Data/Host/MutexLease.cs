using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using KyberWeave.Contracts.Host;

namespace KyberWeave.Data.Host;

/// <summary>A named operating-system mutex, taken as a lease and released by disposing it.</summary>
/// <remarks>
/// <para>
/// It mirrors how the Squad deployment takes its lease today: a named mutex, an abandoned mutex treated as taken, and a
/// second request from this process refused at once rather than waiting for the thread's own recursive mutex.
/// </para>
/// <para>
/// The platform names the mutex, so two processes that pass the same name contend for the same lease.
/// </para>
/// </remarks>
public sealed class MutexLease : ILease
{
    private static readonly ConcurrentDictionary<string, byte> HeldInProcess = new(StringComparer.Ordinal);

    /// <inheritdoc />
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "A returned HeldLease owns the mutex and disposes it; every other path disposes it in the catch block.")]
    public IDisposable Acquire(string name, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        if (!HeldInProcess.TryAdd(name, 0))
        {
            throw new LeaseTimeoutException(name, timeout);
        }

        Mutex? mutex = null;
        bool acquired = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing it; the operating system hands it to us.
                acquired = true;
            }

            if (!acquired)
            {
                throw new LeaseTimeoutException(name, timeout);
            }

            HeldLease lease = new(name, mutex);
            mutex = null;
            return lease;
        }
        catch (Exception exception)
        {
            if (mutex is not null)
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }

                mutex.Dispose();
            }

            _ = HeldInProcess.TryRemove(name, out _);
            if (exception is IOException or UnauthorizedAccessException)
            {
                throw new LeaseTimeoutException(name, timeout, exception);
            }

            throw;
        }
    }

    private sealed class HeldLease(string name, Mutex mutex) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
                _ = HeldInProcess.TryRemove(name, out _);
            }
        }
    }
}

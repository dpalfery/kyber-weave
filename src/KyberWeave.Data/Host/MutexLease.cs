using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using KyberWeave.Contracts.Host;

namespace KyberWeave.Data.Host;

/// <summary>A named operating-system mutex, taken as a lease and released by disposing it.</summary>
/// <remarks>
/// <para>
/// It mirrors how the Squad deployment takes its lease today: a named mutex, an abandoned mutex treated as taken, and a
/// second request from this process queued behind the first, up to its timeout, rather than slipping through the
/// thread's own recursive mutex.
/// </para>
/// <para>
/// A mutex belongs to the thread that took it, so this adapter takes it on a thread of its own and releases it there.
/// The lease it returns can therefore be disposed from any thread, which is what an asynchronous caller needs.
/// </para>
/// <para>
/// The platform names the mutex, so two processes that pass the same name contend for the same lease.
/// </para>
/// </remarks>
public sealed class MutexLease : ILease
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> HeldInProcess = new(StringComparer.Ordinal);

    // A mutex is recursive on its own thread, so the process keeps its own table: a second request from this process
    // waits here, up to its timeout, for the first to release, and is then refused like any other contender.
    private static bool TryTakeInProcess(string name, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        lock (Gate)
        {
            while (!HeldInProcess.Add(name))
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                _ = Monitor.Wait(Gate, remaining);
            }

            return true;
        }
    }

    private static void ReleaseInProcess(string name)
    {
        lock (Gate)
        {
            _ = HeldInProcess.Remove(name);
            Monitor.PulseAll(Gate);
        }
    }

    /// <inheritdoc />
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "A returned HeldLease owns the mutex and disposes it; every other path disposes it in the catch block.")]
    public IDisposable Acquire(string name, TimeSpan timeout)
    {
        _ = LeaseNames.Validate(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        // One timeout bounds the whole request: the wait behind a holder in this process counts against the wait for
        // the system mutex that follows it.
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        if (!TryTakeInProcess(name, timeout))
        {
            throw new LeaseTimeoutException(name, timeout);
        }

        OwnerThread? owner = null;
        try
        {
            TimeSpan remaining = timeout - clock.Elapsed;
            owner = new OwnerThread(name, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            if (!owner.Take())
            {
                throw new LeaseTimeoutException(name, timeout);
            }

            HeldLease lease = new(name, owner);
            owner = null;
            return lease;
        }
        catch (Exception exception)
        {
            owner?.Dispose();
            ReleaseInProcess(name);
            if (exception is IOException or UnauthorizedAccessException)
            {
                throw new LeaseTimeoutException(name, timeout, exception);
            }

            throw;
        }
    }

    private sealed class HeldLease(string name, OwnerThread owner) : IDisposable
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
                owner.Dispose();
            }
            finally
            {
                ReleaseInProcess(name);
            }
        }
    }

    /// <summary>The thread that owns the mutex for as long as the lease is held.</summary>
    private sealed class OwnerThread : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly Thread _thread;
        private readonly TimeSpan _timeout;
        private readonly ManualResetEventSlim _taken = new(initialState: false);
        private readonly ManualResetEventSlim _released = new(initialState: false);
        private bool _held;
        private Exception? _failure;

        public OwnerThread(string name, TimeSpan timeout)
        {
            _timeout = timeout;
            _mutex = new Mutex(initiallyOwned: false, name);
            _thread = new Thread(Hold)
            {
                IsBackground = true,
                Name = "kyber-weave lease owner"
            };
        }

        /// <summary>Starts the owner thread and reports whether it took the mutex.</summary>
        /// <returns>True when the mutex is held; false when the wait timed out.</returns>
        public bool Take()
        {
            _thread.Start();
            _taken.Wait();
            if (_failure is not null)
            {
                ExceptionDispatchInfo.Capture(_failure).Throw();
            }

            return _held;
        }

        public void Dispose()
        {
            _released.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(30)))
            {
                // The owner thread is still inside the mutex. Disposing the mutex or the events under it would throw
                // there, on a background thread that cannot report the failure, so leave them to the process instead.
                return;
            }

            _mutex.Dispose();
            _taken.Dispose();
            _released.Dispose();
        }

        private void Hold()
        {
            try
            {
                bool held;
                try
                {
                    held = _mutex.WaitOne(_timeout);
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner died without releasing it; the operating system hands it to us.
                    held = true;
                }

                _held = held;
                _taken.Set();
                if (!held)
                {
                    return;
                }

                _released.Wait();
                _mutex.ReleaseMutex();
            }
            // The owner-thread entry point is a boundary: it reports any failure through _failure rather than
            // crashing the process on a background thread.
            catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
            {
                _failure = exception;
                _taken.Set();
            }
        }
    }
}

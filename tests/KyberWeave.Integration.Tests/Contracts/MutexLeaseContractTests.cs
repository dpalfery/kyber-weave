using KyberWeave.Contracts.Host;
using KyberWeave.Data.Host;
using KyberWeave.Tests.Lease;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the lease contract against the real adapter, which takes a named operating-system mutex.</summary>
public sealed class MutexLeaseContractTests : LeaseContractTests
{
    /// <inheritdoc />
    protected override ILease CreateLease() => new MutexLease();

    /// <inheritdoc />
    protected override IDisposable HoldElsewhere(string name)
    {
        using ManualResetEventSlim acquired = new(initialState: false);
        ManualResetEventSlim release = new(initialState: false);
        Mutex mutex = new(initiallyOwned: false, name);

        // A mutex belongs to the thread that took it, so the second owner needs its own thread and releases it there.
        Thread thread = new(() =>
        {
            bool held = mutex.WaitOne(TimeSpan.FromSeconds(30));
            acquired.Set();
            _ = release.Wait(TimeSpan.FromSeconds(30));
            if (held)
            {
                mutex.ReleaseMutex();
            }

            mutex.Dispose();
        })
        {
            IsBackground = true
        };

        thread.Start();
        if (!acquired.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException($"The lease '{name}' was not taken by the second owner within 30 seconds.");
        }

        return new ThreadOwner(thread, release);
    }

    private sealed class ThreadOwner(Thread thread, ManualResetEventSlim release) : IDisposable
    {
        public void Dispose()
        {
            release.Set();
            _ = thread.Join(TimeSpan.FromSeconds(30));
            release.Dispose();
        }
    }
}

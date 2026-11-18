using KyberWeave.Contracts.Host;
using KyberWeave.Data.Host;
using KyberWeave.Tests.Lease;
using Xunit;

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
            bool held;
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died holding the name; the operating system hands it to us, as the adapter does.
                held = true;
            }

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

    [Fact]
    public void TakesALeaseAbandonedByItsOwner()
    {
        string name = $"kyber-weave-contract-lease-{Guid.NewGuid():N}";
        using (Mutex previouslyHeld = new(initiallyOwned: false, name))
        {
            Thread owner = new(() =>
            {
                // Take the mutex and exit without releasing it; the operating system then abandons it.
                _ = previouslyHeld.WaitOne(TimeSpan.FromSeconds(30));
            })
            {
                IsBackground = true
            };

            owner.Start();
            Assert.True(owner.Join(TimeSpan.FromSeconds(30)), $"The owner of '{name}' did not exit within 30 seconds.");
        }

        ILease lease = CreateLease();

        using IDisposable held = lease.Acquire(name, ShortTimeout);
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

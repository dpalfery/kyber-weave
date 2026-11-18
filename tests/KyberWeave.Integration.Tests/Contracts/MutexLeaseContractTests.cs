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
    public void WaitsNoLongerThanOneTimeoutInTotalForASameProcessHolderAndThenAnotherProcess()
    {
        MutexLease lease = new();
        string name = $"kyber-weave-contract-lease-{Guid.NewGuid():N}";
        TimeSpan timeout = TimeSpan.FromMilliseconds(800);
        IDisposable first = lease.Acquire(name, GenerousTimeout);

        // The second request queues behind the first inside the process, then meets a mutex that another owner holds:
        // together the two waits must stay within the one timeout it was given.
        Thread release = new(() =>
        {
            Thread.Sleep(400);
            first.Dispose();
        });
        release.Start();
        IDisposable foreign = HoldElsewhereAfterRelease(name, release);
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        _ = Assert.Throws<LeaseTimeoutException>(() => lease.Acquire(name, timeout));

        clock.Stop();
        foreign.Dispose();
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1100), $"The request waited {clock.Elapsed}.");
    }

    private IDisposable HoldElsewhereAfterRelease(string name, Thread release)
    {
        // The other owner takes the system mutex the moment the in-process holder lets go, so the queued request has
        // to wait on the mutex as well.
        ManualResetEventSlim ready = new(initialState: false);
        IDisposable owner = new NoOwner();
        Thread thread = new(() =>
        {
            release.Join();
            owner = HoldElsewhere(name);
            ready.Set();
        })
        {
            IsBackground = true
        };
        thread.Start();
        return new Waiter(thread, ready, () => owner);
    }

    private sealed class NoOwner : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class Waiter(Thread thread, ManualResetEventSlim ready, Func<IDisposable> owner) : IDisposable
    {
        public void Dispose()
        {
            _ = thread.Join(TimeSpan.FromSeconds(30));
            _ = ready.Wait(TimeSpan.FromSeconds(30));
            owner().Dispose();
        }
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

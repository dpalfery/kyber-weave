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

        // Another owner is already queued on the system mutex, so when the first holder lets go it is that owner, not
        // the request below, that takes it.
        using ManualResetEventSlim foreignHolds = new(initialState: false);
        using ManualResetEventSlim foreignRelease = new(initialState: false);
        Thread foreign = new(() =>
        {
            using Mutex mutex = new(initiallyOwned: false, name);
            bool held = mutex.WaitOne(TimeSpan.FromSeconds(30));
            foreignHolds.Set();
            _ = foreignRelease.Wait(TimeSpan.FromSeconds(30));
            if (held)
            {
                mutex.ReleaseMutex();
            }
        })
        {
            IsBackground = true
        };
        foreign.Start();

        // A thread parked in a wait is the observable sign that it is queued on the mutex.
        SpinWait.SpinUntil(
            () => foreign.ThreadState.HasFlag(ThreadState.WaitSleepJoin),
            TimeSpan.FromSeconds(30));
        Thread release = new(() =>
        {
            Thread.Sleep(600);
            first.Dispose();
        });
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        release.Start();

        // The request queues behind the first holder inside the process, then meets a mutex the other owner holds:
        // the two waits together must stay within the one timeout it was given.
        _ = Assert.Throws<LeaseTimeoutException>(() => lease.Acquire(name, timeout));

        clock.Stop();
        foreignRelease.Set();
        release.Join();
        foreign.Join();
        Assert.True(foreignHolds.IsSet, "The other owner never took the mutex.");
        // One timeout is about 800 ms in total; the old double wait was about 1400 ms.
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1200), $"The request waited {clock.Elapsed}.");
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

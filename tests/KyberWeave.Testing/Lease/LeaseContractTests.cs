using KyberWeave.Contracts.Host;
using Xunit;

namespace KyberWeave.Tests.Lease;

/// <summary>
/// The contract every <see cref="ILease"/> must satisfy. Run it once against the fake and once against the real
/// adapter, so a unit test can trust what the fake reports.
/// </summary>
/// <remarks>
/// A derived class supplies the lease under test and a way to have another owner hold a name. The fake simulates that
/// owner in memory; the adapter takes the same named mutex on a second thread.
/// </remarks>
public abstract class LeaseContractTests
{
    /// <summary>A wait long enough that a free lease is always taken.</summary>
    protected static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A wait short enough that a held lease fails quickly.</summary>
    protected static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(150);

    /// <summary>Creates the lease seam under test.</summary>
    /// <returns>The lease seam. The suite may create more than one instance.</returns>
    protected abstract ILease CreateLease();

    /// <summary>Has another owner take <paramref name="name"/> until the returned handle is disposed.</summary>
    /// <param name="name">The lease name.</param>
    /// <returns>The other owner; disposing it releases the lease.</returns>
    protected abstract IDisposable HoldElsewhere(string name);

    /// <summary>A lease name no other test uses.</summary>
    /// <returns>The name.</returns>
    protected static string NewName() => $"kyber-weave-contract-lease-{Guid.NewGuid():N}";

    [Fact]
    public void TakesAFreeLeaseAndReleasesItWhenDisposed()
    {
        ILease lease = CreateLease();
        string name = NewName();

        using (lease.Acquire(name, GenerousTimeout))
        {
        }

        using (lease.Acquire(name, GenerousTimeout))
        {
        }
    }

    [Fact]
    public void TakesADifferentNameWhileAnotherIsHeld()
    {
        ILease lease = CreateLease();

        using IDisposable first = lease.Acquire(NewName(), GenerousTimeout);
        using IDisposable second = lease.Acquire(NewName(), GenerousTimeout);
    }

    [Fact]
    public void RefusesALeaseThatAnotherOwnerHolds()
    {
        ILease lease = CreateLease();
        string name = NewName();

        using (HoldElsewhere(name))
        {
            _ = Assert.Throws<LeaseTimeoutException>(() => lease.Acquire(name, ShortTimeout));
        }

        using (lease.Acquire(name, GenerousTimeout))
        {
        }
    }

    [Fact]
    public void RefusesALeaseThatASecondSeamInstanceHolds()
    {
        ILease holder = CreateLease();
        ILease contender = CreateLease();
        string name = NewName();

        using (holder.Acquire(name, GenerousTimeout))
        {
            _ = Assert.Throws<LeaseTimeoutException>(() => contender.Acquire(name, ShortTimeout));
        }

        using (contender.Acquire(name, GenerousTimeout))
        {
        }
    }

    [Fact]
    public void RefusesTheSameLeaseTwiceInOneProcess()
    {
        ILease lease = CreateLease();
        string name = NewName();

        using IDisposable held = lease.Acquire(name, GenerousTimeout);

        LeaseTimeoutException exception =
            Assert.Throws<LeaseTimeoutException>(() => lease.Acquire(name, ShortTimeout));

        Assert.Equal(name, exception.Name);
        Assert.Equal(ShortTimeout, exception.Timeout);
    }

    [Fact]
    public void ReleasingALeaseTwiceIsHarmless()
    {
        ILease lease = CreateLease();
        string name = NewName();
        IDisposable held = lease.Acquire(name, GenerousTimeout);

        held.Dispose();
        held.Dispose();

        using (lease.Acquire(name, GenerousTimeout))
        {
        }
    }

    [Fact]
    public void ReleasesTheLeaseFromAThreadOtherThanTheOneThatTookIt()
    {
        ILease lease = CreateLease();
        string name = NewName();
        IDisposable held = lease.Acquire(name, GenerousTimeout);

        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                held.Dispose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.Start();
        _ = thread.Join(TimeSpan.FromSeconds(30));

        Assert.Null(failure);
        using (lease.Acquire(name, ShortTimeout))
        {
        }
    }

    [Fact]
    public void TakesAFreeLeaseWithNoWait()
    {
        ILease lease = CreateLease();

        using IDisposable held = lease.Acquire(NewName(), TimeSpan.Zero);
    }

    [Fact]
    public void RejectsALeaseNameThatIsNotGiven()
    {
        ILease lease = CreateLease();

        _ = Assert.ThrowsAny<ArgumentException>(() => lease.Acquire(null!, GenerousTimeout));
        _ = Assert.ThrowsAny<ArgumentException>(() => lease.Acquire(string.Empty, GenerousTimeout));
        _ = Assert.ThrowsAny<ArgumentException>(() => lease.Acquire("  ", GenerousTimeout));
    }

    [Fact]
    public void RejectsANegativeTimeout()
    {
        ILease lease = CreateLease();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => lease.Acquire(NewName(), TimeSpan.FromSeconds(-1)));
    }
}

namespace KyberWeave.Contracts.Host;

/// <summary>A named cross-process lease: only one owner holds a given name at a time, on this machine.</summary>
/// <remarks>
/// Two Squad commands that change the same deployment must not interleave. Code asks this seam instead of creating a
/// <see cref="Mutex"/> itself, so a unit test controls contention in memory.
/// </remarks>
public interface ILease
{
    /// <summary>Takes the lease, waiting up to <paramref name="timeout"/> for its current owner to release it.</summary>
    /// <param name="name">The lease name. Two owners share a lease only when they name it the same way.</param>
    /// <param name="timeout">The longest to wait for the lease; <see cref="TimeSpan.Zero"/> fails at once when it is held.</param>
    /// <returns>The held lease; disposing it releases the lease.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
    /// <exception cref="LeaseTimeoutException">The lease stayed held for the whole timeout, including a second request from the same process.</exception>
    IDisposable Acquire(string name, TimeSpan timeout);
}

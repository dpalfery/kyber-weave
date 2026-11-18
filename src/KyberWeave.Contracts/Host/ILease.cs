namespace KyberWeave.Contracts.Host;

/// <summary>A named cross-process lease: only one owner holds a given name at a time, on this machine.</summary>
/// <remarks>
/// Two Squad commands that change the same deployment must not interleave. Code asks this seam instead of creating a
/// <see cref="Mutex"/> itself, so a unit test controls contention in memory.
/// </remarks>
public interface ILease
{
    /// <summary>Takes the lease, waiting up to <paramref name="timeout"/> for an owner in another process to release it. A request from the process that already holds it is refused at once.</summary>
    /// <param name="name">The lease name: a plain name, at most <see cref="LeaseNames.MaximumLength"/> characters, with no namespace prefix or path separator. Two owners share a lease only when they name it the same way.</param>
    /// <param name="timeout">The longest to wait for the lease; <see cref="TimeSpan.Zero"/> fails at once when it is held.</param>
    /// <returns>The held lease; disposing it releases the lease, from any thread.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or whitespace, longer than <see cref="LeaseNames.MaximumLength"/>, or contains a namespace prefix or path separator.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
    /// <exception cref="LeaseTimeoutException">The lease was still held when the timeout ended. A second request from the holding process is refused at once instead of waiting.</exception>
    IDisposable Acquire(string name, TimeSpan timeout);
}

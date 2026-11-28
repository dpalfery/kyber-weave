namespace KyberWeave.Contracts.Repositories;

/// <summary>The disk the CodeGraph index preparation works on: the lock that serialises runs and the writability proof.</summary>
public interface ICodeGraphIndexWorkspace
{
    /// <summary>
    /// Takes the cross-process index lock by opening the file with no sharing, retrying until the deadline. The file is
    /// never removed: removing a lock another run holds would let a third run in beside it.
    /// </summary>
    /// <param name="path">The lock file.</param>
    /// <param name="wait">How long to wait for a lock another run holds. Zero tries once.</param>
    /// <param name="failure">
    /// Null when another run holds the lock, and the reason when the lock path itself cannot be used.
    /// </param>
    /// <returns>The held lock, released when disposed; or null when it was not taken.</returns>
    IDisposable? TryAcquireLock(string path, TimeSpan wait, out string? failure);

    /// <summary>
    /// Takes the cross-process index lock without blocking a thread, retrying until the deadline. The file is never
    /// removed: removing a lock another run holds would let a third run in beside it.
    /// </summary>
    /// <param name="path">The lock file.</param>
    /// <param name="wait">How long to wait for a lock another run holds. Zero tries once.</param>
    /// <param name="cancellationToken">Cancels the wait between attempts.</param>
    /// <returns>
    /// The held lock, released when disposed; or the reason the lock path itself cannot be used. A lock another run
    /// holds is reported with neither.
    /// </returns>
    Task<CodeGraphIndexLockAttempt> TryAcquireLockAsync(
        string path,
        TimeSpan wait,
        CancellationToken cancellationToken = default);

    /// <summary>Proves a directory is writable by creating and removing a probe file.</summary>
    /// <param name="directory">The directory.</param>
    /// <returns>True when a file could be created and removed there.</returns>
    bool IsWritable(string directory);

    /// <summary>
    /// Whether CodeGraph's own lock file in <paramref name="indexDirectory"/> names a process that is still running,
    /// which is the rule CodeGraph itself uses to tell a held lock from a stale one.
    /// </summary>
    /// <param name="indexDirectory">The <c>.codegraph/</c> directory.</param>
    /// <returns>True when another live process holds the lock; false when there is no lock file or it is stale.</returns>
    bool IsCodeGraphLockHeld(string indexDirectory);
}

/// <summary>The outcome of trying to take the CodeGraph index lock.</summary>
/// <param name="Lock">The held lock, released when disposed; null when it was not taken.</param>
/// <param name="Failure">
/// Null when another run holds the lock, and the reason when the lock path itself cannot be used.
/// </param>
public sealed record CodeGraphIndexLockAttempt(IDisposable? Lock, string? Failure);

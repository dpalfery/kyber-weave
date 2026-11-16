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

    /// <summary>Proves a directory is writable by creating and removing a probe file.</summary>
    /// <param name="directory">The directory.</param>
    /// <returns>True when a file could be created and removed there.</returns>
    bool IsWritable(string directory);
}

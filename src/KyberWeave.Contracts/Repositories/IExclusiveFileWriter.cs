namespace KyberWeave.Contracts.Repositories;

/// <summary>Creates a file that must not exist yet, in one step, so a file that appeared since the last look is never overwritten.</summary>
public interface IExclusiveFileWriter
{
    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/>. False when the path is already taken (a file or a
    /// folder is there) or this process may not create it; either way nothing is overwritten. A failure while writing
    /// removes the partial file this call created, best effort, and propagates.
    /// </summary>
    /// <param name="path">The file to create.</param>
    /// <param name="text">What to write.</param>
    bool TryWriteNew(string path, string text);

    /// <summary><see cref="TryWriteNew(string, string)"/> with the removal of a partial file supplied, so a failing removal can be tested.</summary>
    /// <param name="path">The file to create.</param>
    /// <param name="text">What to write.</param>
    /// <param name="deletePartial">Removes the file this call created when writing it failed.</param>
    bool TryWriteNew(string path, string text, Action<string> deletePartial);
}

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads what the file system knows about a file without reading its content: its length and its last write time.</summary>
/// <remarks>
/// Both members follow symbolic links to the final target and answer for that file, never for the link. A broken
/// link reads as a missing file, and a link chain that loops throws <see cref="IOException"/>.
/// </remarks>
public interface IFileStatReader
{
    /// <summary>Gets the length of a file in bytes.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The length in bytes.</returns>
    /// <exception cref="FileNotFoundException">The file, or the final target of a link, does not exist.</exception>
    /// <exception cref="IOException">A symbolic link chain loops or is too deep.</exception>
    long GetLength(string path);

    /// <summary>Gets a file's last write time in UTC.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The time; the file-time epoch when the file does not exist. An unreadable file throws.</returns>
    /// <exception cref="IOException">The file cannot be read, or a symbolic link chain loops or is too deep.</exception>
    DateTime GetLastWriteTimeUtc(string path);
}

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads what the file system knows about a file without reading its content: its length and its last write time.</summary>
public interface IFileStatReader
{
    /// <summary>Gets the length of a file in bytes.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The length in bytes.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    long GetLength(string path);

    /// <summary>Gets a file's last write time in UTC.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The time; the file-time epoch when the file does not exist. An unreadable file throws.</returns>
    DateTime GetLastWriteTimeUtc(string path);
}

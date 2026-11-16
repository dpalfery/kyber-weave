namespace KyberWeave.Contracts.Repositories;

/// <summary>The file system writes and probes a command needs beyond the reads in <see cref="IFileSystem"/>.</summary>
public interface IWritableFileSystem : IFileSystem
{
    /// <summary>Gets whether a file or a directory exists at a path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True when something exists there.</returns>
    bool PathExists(string path);

    /// <summary>Creates a directory and its missing parents.</summary>
    /// <param name="path">The directory.</param>
    void CreateDirectory(string path);

    /// <summary>Writes a whole text file, replacing any earlier one.</summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The text.</param>
    void WriteAllText(string path, string contents);

    /// <summary>Creates an empty file that must not exist yet, so only one caller can win a race for the path.</summary>
    /// <param name="path">The file.</param>
    /// <exception cref="IOException">The file already exists, or it could not be created.</exception>
    void CreateNewFile(string path);

    /// <summary>Deletes a file. A missing file is not an error.</summary>
    /// <param name="path">The file.</param>
    void DeleteFile(string path);

    /// <summary>Gets the target of a symbolic link.</summary>
    /// <param name="path">The file or directory.</param>
    /// <returns>The link target as written, or null when the path is not a link or does not exist.</returns>
    string? GetLinkTarget(string path);
}

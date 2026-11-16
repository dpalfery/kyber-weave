namespace KyberWeave.Contracts.Repositories;

/// <summary>The file system the Kyber Utilities status-line deployment reads and writes.</summary>
public interface IStatusLineFiles
{
    /// <summary>Gets whether a file exists.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>True when the file exists.</returns>
    bool FileExists(string path);

    /// <summary>Gets whether a directory exists.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>True when the directory exists.</returns>
    bool DirectoryExists(string path);

    /// <summary>Reads a whole file.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The file's bytes.</returns>
    byte[] ReadAllBytes(string path);

    /// <summary>Reads a whole text file, or reports it absent.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The file's UTF-8 text, or null when the file does not exist.</returns>
    string? ReadTextOrNull(string path);

    /// <summary>Creates a directory and its missing parents.</summary>
    /// <param name="path">The directory.</param>
    void CreateDirectory(string path);

    /// <summary>Writes a whole file, replacing any earlier one.</summary>
    /// <param name="path">The file.</param>
    /// <param name="content">The bytes.</param>
    void WriteAllBytes(string path, ReadOnlySpan<byte> content);

    /// <summary>Writes a text file so a reader never sees a partial file: through a temporary beside it, then a rename.</summary>
    /// <param name="path">The file.</param>
    /// <param name="text">The text.</param>
    void WriteTextAtomically(string path, string text);

    /// <summary>Deletes a file. A missing file is not an error.</summary>
    /// <param name="path">The file.</param>
    void DeleteFile(string path);

    /// <summary>Gets whether a directory holds no files and no directories.</summary>
    /// <param name="path">The directory.</param>
    /// <returns>True when it is empty.</returns>
    bool IsDirectoryEmpty(string path);

    /// <summary>Deletes an empty directory.</summary>
    /// <param name="path">The directory.</param>
    void DeleteDirectory(string path);

    /// <summary>Gets whether a path is a symbolic link or a reparse point.</summary>
    /// <param name="path">The file or directory.</param>
    /// <returns>True for a link; false for anything else, a path that does not exist included.</returns>
    bool IsLinkOrReparsePoint(string path);
}

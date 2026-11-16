namespace KyberWeave.Contracts.Repositories;

/// <summary>The file reads, existence probes and directory listings a command needs.</summary>
public interface IFileSystem
{
    /// <summary>Gets whether a file exists.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>True when the file exists.</returns>
    bool FileExists(string path);

    /// <summary>Gets whether a directory exists.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>True when the directory exists.</returns>
    bool DirectoryExists(string path);

    /// <summary>Reads a whole text file.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The file's text.</returns>
    string ReadAllText(string path);

    /// <summary>Lists files under a directory, skipping build output and dependency folders.</summary>
    /// <param name="directory">The absolute directory.</param>
    /// <param name="searchPattern">A file name pattern such as <c>*.sln</c>.</param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    /// <returns>Absolute file paths; empty when the directory is missing.</returns>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive);

    /// <summary>Lists the immediate child directories of a directory.</summary>
    /// <param name="directory">The absolute directory.</param>
    /// <returns>Absolute directory paths; empty when the directory is missing.</returns>
    IEnumerable<string> EnumerateDirectories(string directory);
}

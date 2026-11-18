namespace KyberWeave.Contracts.Repositories;

/// <summary>The file reads, writes and directory walks the documentation engine does inside a repository checkout.</summary>
public interface IRepositoryFileStore : IWritableFileSystem
{
    /// <summary>Reads a whole file as bytes.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The file's bytes.</returns>
    new byte[] ReadAllBytes(string path);

    /// <summary>Reads a file line by line.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The lines, read lazily.</returns>
    new IEnumerable<string> ReadLines(string path);

    /// <summary>Reads a text file through the one bounded reader.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The file's text.</returns>
    /// <exception cref="InvalidDataException">The file is larger than the bound.</exception>
    string ReadAllTextBounded(string path);

    /// <summary>Reads a text file that may or may not be there, in one step.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The text, or null when the file or its folder is not there at the moment of the read.</returns>
    /// <exception cref="IOException">The file cannot be read, is a folder, or is larger than the bound.</exception>
    string? ReadTextIfPresent(string path);

    /// <summary>Writes a whole text file so a reader never sees it half written.</summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The text.</param>
    new void WriteAllTextAtomic(string path, string contents);

    /// <summary>Opens a file for reading.</summary>
    /// <param name="path">The file.</param>
    /// <returns>A read stream the caller disposes.</returns>
    new Stream OpenRead(string path);

    /// <summary>Opens a file for appending with no sharing.</summary>
    /// <param name="path">The file.</param>
    /// <returns>A write stream the caller disposes.</returns>
    Stream OpenAppend(string path);

    /// <summary>Lists the files directly in a directory.</summary>
    /// <param name="directory">The directory.</param>
    /// <returns>Absolute paths.</returns>
    string[] GetFiles(string directory);

    /// <summary>Lists the directories directly in a directory.</summary>
    /// <param name="directory">The directory.</param>
    /// <returns>Absolute paths.</returns>
    string[] GetDirectories(string directory);

    /// <summary>Lists the files and directories directly in a directory, lazily and in the platform's order.</summary>
    /// <param name="directory">The directory.</param>
    /// <returns>The entries, each with its name and whether it is a directory or a link.</returns>
    new IEnumerable<RepositoryEntry> EnumerateEntries(string directory);

    /// <summary>Deletes a directory.</summary>
    /// <param name="path">The directory.</param>
    /// <param name="recursive">Whether its contents go with it.</param>
    void DeleteDirectory(string path, bool recursive);

    /// <summary>Gets when a file was last written.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The UTC time.</returns>
    new DateTime GetLastWriteTimeUtc(string path);

    /// <summary>Lists the files under a documentation root, refusing any symbolic link on the way.</summary>
    /// <param name="root">The absolute documentation root.</param>
    /// <param name="searchPattern">A file name pattern such as <c>*.json</c>.</param>
    /// <returns>Absolute paths of files that stay inside the root.</returns>
    IEnumerable<string> EnumerateContainedFiles(string root, string searchPattern);

    /// <summary>Gets whether a repository-relative file is a regular file inside one of the documentation roots.</summary>
    /// <param name="repoRoot">The absolute repository root.</param>
    /// <param name="relativePath">The repository-relative path.</param>
    /// <param name="relativeRoots">The repository-relative documentation roots.</param>
    /// <returns>True when the file exists, no part of its path is a link, and it sits under a root.</returns>
    bool IsContainedFile(string repoRoot, string relativePath, IEnumerable<string> relativeRoots);

    /// <summary>Gets whether a path is a reparse point such as a Windows junction or a symbolic link.</summary>
    /// <param name="path">The file or directory.</param>
    /// <returns>True when it is one.</returns>
    bool IsReparsePoint(string path);
}

/// <summary>One entry of a directory listing.</summary>
/// <param name="Path">The absolute path.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="IsLink">Whether the entry is a symbolic link or other reparse point.</param>
public readonly record struct RepositoryEntry(string Path, bool IsDirectory, bool IsLink);

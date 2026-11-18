namespace KyberWeave.Contracts.Repositories;

/// <summary>Everything the product reads from a file system: content, metadata, listings, links and directory entries.</summary>
/// <remarks>
/// This is the read half of the file-system seam. Writes live on <see cref="IWritableFileSystem"/>, so a class
/// that only reads can be given an implementation that cannot change anything. Paths are absolute; turning a
/// relative path into one is the path seam's job.
/// </remarks>
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
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    string ReadAllText(string path);

    /// <summary>Reads a whole file as bytes.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    byte[] ReadAllBytes(string path);

    /// <summary>Reads a file line by line, without loading it whole.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The lines, read lazily as the caller enumerates them.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist, reported when enumeration starts.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    IEnumerable<string> ReadLines(string path);

    /// <summary>Opens a file for reading.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>A stream the caller disposes.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    Stream OpenRead(string path);

    /// <summary>Gets the length of a file in bytes, without reading its content.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The length in bytes.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file's metadata.</exception>
    long GetLength(string path);

    /// <summary>Gets a path's last write time in UTC.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The UTC time; the file-time epoch when the path does not exist.</returns>
    /// <exception cref="UnauthorizedAccessException">The process may not read the path's metadata.</exception>
    DateTime GetLastWriteTimeUtc(string path);

    /// <summary>Gets a path's attributes, such as whether it is a directory, read-only or a reparse point.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The attributes.</returns>
    /// <exception cref="FileNotFoundException">Nothing exists at the path.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the path's metadata.</exception>
    FileAttributes GetAttributes(string path);

    /// <summary>Lists files under a directory, skipping build output and dependency folders.</summary>
    /// <param name="directory">The absolute directory.</param>
    /// <param name="searchPattern">A file name pattern such as <c>*.sln</c>.</param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    /// <returns>Absolute file paths; empty when the directory is missing.</returns>
    /// <exception cref="UnauthorizedAccessException">A directory on the walk may not be listed.</exception>
    /// <exception cref="IOException">A directory on the walk could not be listed.</exception>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive);

    /// <summary>Lists files under a directory, choosing whether a linked directory is descended into.</summary>
    /// <param name="directory">The absolute directory.</param>
    /// <param name="searchPattern">A file name pattern such as <c>*.sln</c>.</param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    /// <param name="followLinks">
    /// True to descend into a symbolic link that names a directory; false to report the link's own directory as
    /// empty and never read through it, so a walk cannot leave the tree it started in.
    /// </param>
    /// <returns>Absolute file paths; empty when the directory is missing.</returns>
    /// <exception cref="UnauthorizedAccessException">A directory on the walk may not be listed.</exception>
    /// <exception cref="IOException">A directory on the walk could not be listed.</exception>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive, bool followLinks);

    /// <summary>Lists the immediate child directories of a directory.</summary>
    /// <param name="directory">The absolute directory.</param>
    /// <returns>Absolute directory paths; empty when the directory is missing.</returns>
    /// <exception cref="UnauthorizedAccessException">The directory may not be listed.</exception>
    /// <exception cref="IOException">The directory could not be listed.</exception>
    IEnumerable<string> EnumerateDirectories(string directory);

    /// <summary>Lists the immediate children of a directory with their kind, without following a link.</summary>
    /// <param name="directory">The absolute directory.</param>
    /// <returns>One entry per child, in the platform's order; empty when the directory is missing.</returns>
    /// <exception cref="UnauthorizedAccessException">The directory may not be listed.</exception>
    /// <exception cref="IOException">The directory could not be listed.</exception>
    IEnumerable<FileSystemEntry> EnumerateEntries(string directory);

    /// <summary>Gets the target of a symbolic link, exactly as the link records it.</summary>
    /// <param name="path">The file or directory.</param>
    /// <returns>The link target as written, or null when the path is not a link or does not exist.</returns>
    string? GetLinkTarget(string path);

    /// <summary>Resolves a symbolic link through every hop to the final target's full path.</summary>
    /// <param name="path">The file or directory.</param>
    /// <returns>
    /// The final target's full path, or null when the path is not a link, does not exist, or names a link whose
    /// target does not exist.
    /// </returns>
    string? GetFinalLinkTarget(string path);
}

/// <summary>One immediate child of a directory, as a listing sees it.</summary>
/// <param name="Path">The entry's absolute path.</param>
/// <param name="IsDirectory">Whether the entry names a directory rather than a file.</param>
/// <param name="IsLink">Whether the entry is a symbolic link or another reparse point, which the listing does not follow.</param>
public readonly record struct FileSystemEntry(string Path, bool IsDirectory, bool IsLink);

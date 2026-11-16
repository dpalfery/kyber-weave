namespace KyberWeave.Kernel.FileSystem;

/// <summary>The file system reads a doctor probe needs to prove what is installed on the machine.</summary>
/// <remarks>
/// Separate from the hook's file-system contract because a probe also lists one level of a directory while
/// skipping links, and reads the tail of a log a live process holds open. A seam only for reading keeps a
/// probe from being able to change what it inspects.
/// </remarks>
public interface IDoctorFileSystem
{
    /// <summary>Gets whether a file exists.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>True when the file exists.</returns>
    bool FileExists(string path);

    /// <summary>Gets whether a path names a readable file, resolving a link to its final target.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>True when there is a file to read; false for a missing file and for a link whose target does not exist.</returns>
    bool IsPresentFile(string path);

    /// <summary>Reads a whole file as bytes.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The file's bytes.</returns>
    byte[] ReadAllBytes(string path);

    /// <summary>Gets whether a directory exists.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>True when the directory exists.</returns>
    bool DirectoryExists(string path);

    /// <summary>Reads a whole text file.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The file's text.</returns>
    string ReadAllText(string path);

    /// <summary>Lists the immediate entries of a directory without following a link.</summary>
    /// <param name="directory">The absolute directory.</param>
    /// <returns>One entry per immediate child, in no particular order; empty when the directory is missing.</returns>
    IReadOnlyList<DoctorDirectoryEntry> ListEntries(string directory);

    /// <summary>Reads the last part of a file a live process may hold open for writing.</summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="maxBytes">How many bytes from the end to read at most.</param>
    /// <param name="firstLineIsPartial">
    /// True when the file is longer than <paramref name="maxBytes"/> and the byte before the window is not a line
    /// break, so the window starts part-way through a line.
    /// </param>
    /// <returns>The text read from the file's tail.</returns>
    string ReadTail(string path, long maxBytes, out bool firstLineIsPartial);
}

/// <summary>One immediate child of a directory, as a probe sees it.</summary>
/// <param name="Name">The entry's file name, without its directory.</param>
/// <param name="FullPath">The entry's absolute path.</param>
/// <param name="IsDirectory">Whether the entry is a directory rather than a file.</param>
/// <param name="IsLink">Whether the entry is a link, which a probe never follows.</param>
public sealed record DoctorDirectoryEntry(string Name, string FullPath, bool IsDirectory, bool IsLink);

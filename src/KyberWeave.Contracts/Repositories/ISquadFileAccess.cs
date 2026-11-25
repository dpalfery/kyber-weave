using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The file probes the Squad lifecycle needs on a release's extraction tree and the checkout it runs from.</summary>
public interface ISquadFileAccess
{
    /// <summary>Tells whether a file exists.</summary>
    bool FileExists(string path);

    /// <summary>Tells whether a directory exists.</summary>
    bool DirectoryExists(string path);

    /// <summary>Reads a text file.</summary>
    string ReadAllText(string path);

    /// <summary>Reads a file's bytes.</summary>
    byte[] ReadAllBytes(string path);

    /// <summary>
    /// Gets a file's length in bytes without reading its content, so a caller can refuse an oversized file before it
    /// loads the file.
    /// </summary>
    /// <remarks>
    /// A symbolic link at <paramref name="path"/> is followed: the length is the one of the file it names.
    /// </remarks>
    /// <param name="path">The file.</param>
    /// <returns>The file's length in bytes.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    long GetLength(string path);

    /// <summary>Creates a directory and any missing parents.</summary>
    void CreateDirectory(string path);

    /// <summary>Deletes a directory tree.</summary>
    void DeleteDirectory(string path);

    /// <summary>Gets a path for a new scratch directory that does not exist yet.</summary>
    string NewScratchDirectoryPath(string prefix);

    /// <summary>Lists the directories directly under a directory that are not links, hidden ones included.</summary>
    IEnumerable<SquadDirectoryEntry> EnumerateRealDirectories(string directory);

    /// <summary>Finds the checkout's <c>products/kyber-squad</c> directory above the running binary, or null.</summary>
    string? FindCheckoutSquadSourceRoot();

    /// <summary>
    /// Replaces a whole file atomically: a fully written temporary sibling is renamed over it, so a reader never
    /// sees it half written.
    /// </summary>
    /// <remarks>
    /// Missing parent directories are created. An existing file's Unix mode is kept, so a <c>0600</c> file that
    /// holds secrets does not become readable by others. A symbolic link at <paramref name="path"/> is written
    /// through: the file it names receives the bytes and the link itself stays.
    /// </remarks>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="contents">The complete bytes the file ends up holding.</param>
    /// <exception cref="UnauthorizedAccessException">The process may not write the file.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    void WriteAllBytesAtomic(string path, byte[] contents);
}

/// <summary>A directory found under another directory.</summary>
/// <param name="Name">The directory's name.</param>
/// <param name="FullPath">The directory's full path.</param>
public sealed record SquadDirectoryEntry(string Name, string FullPath);

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
}

/// <summary>A directory found under another directory.</summary>
/// <param name="Name">The directory's name.</param>
/// <param name="FullPath">The directory's full path.</param>
public sealed record SquadDirectoryEntry(string Name, string FullPath);

using KyberWeave.Core.Docs.Mcp;

namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// The file access the MCP wiring engines (inspect, repair, unwire, rename) need to read and rewrite harness MCP
/// configuration files. Reads that must tell a denied file from an absent one surface the underlying exception.
/// </summary>
public interface IMcpConfigFiles
{
    /// <summary>Which state one path component is in: an ordinary entry, a symbolic link, or one this process may not inspect.</summary>
    /// <param name="path">The absolute path of the component.</param>
    McpLinkObstruction InspectLink(string path);

    /// <summary>Whether a file exists at <paramref name="path"/>.</summary>
    /// <param name="path">The absolute path.</param>
    bool FileExists(string path);

    /// <summary>Whether a directory exists at <paramref name="path"/>.</summary>
    /// <param name="path">The absolute path.</param>
    bool DirectoryExists(string path);

    /// <summary>Reads a text file.</summary>
    /// <param name="path">The absolute path.</param>
    string ReadAllText(string path);

    /// <summary>Reads a file's bytes.</summary>
    /// <param name="path">The absolute path.</param>
    byte[] ReadAllBytes(string path);

    /// <summary>Writes a file so a reader never sees a partial one, creating it when absent.</summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="contents">The bytes to write.</param>
    void WriteAllBytesAtomic(string path, byte[] contents);

    /// <summary>Deletes a file.</summary>
    /// <param name="path">The absolute path.</param>
    void DeleteFile(string path);
}

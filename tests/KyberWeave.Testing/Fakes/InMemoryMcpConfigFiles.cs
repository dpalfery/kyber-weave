using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Fakes;

/// <summary>The MCP configuration file seam in a unit test: reads and atomic writes answered by an <see cref="InMemoryFileSystem" />.</summary>
/// <remarks>
/// A path that is a link in the in-memory file system answers <see cref="McpLinkObstruction.Link" />; every other
/// path answers <see cref="McpLinkObstruction.None" />, so a test never meets a denied segment unless it adds a fault.
/// </remarks>
public sealed class InMemoryMcpConfigFiles(InMemoryFileSystem files) : IMcpConfigFiles
{
    /// <summary>Gets the file system the files live in.</summary>
    public InMemoryFileSystem Files { get; } = files ?? throw new ArgumentNullException(nameof(files));

    /// <inheritdoc />
    public McpLinkObstruction InspectLink(string path)
    {
        try
        {
            return Files.GetLinkTarget(path) is null ? McpLinkObstruction.None : McpLinkObstruction.Link;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return McpLinkObstruction.Denied;
        }
    }

    /// <inheritdoc />
    public bool FileExists(string path) => Files.FileExists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Files.DirectoryExists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => Files.ReadAllText(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => Files.ReadAllBytes(path);

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] contents) => Files.WriteAllBytesAtomic(path, contents);

    /// <inheritdoc />
    public void DeleteFile(string path) => Files.DeleteFile(path);
}

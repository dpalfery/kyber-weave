using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Docs.Mcp;

/// <summary>The physical file system as the MCP wiring engines see it.</summary>
internal sealed class PhysicalMcpConfigFiles : IMcpConfigFiles
{
    /// <summary>
    /// Which of the three states one path component is in: an ordinary entry, a symbolic link, or one
    /// this process may not inspect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A component that does not exist is an ordinary entry, so a file the repair is about to create
    /// is not refused.
    /// </para>
    /// <para>
    /// <see cref="FileSystemInfo.LinkTarget"/> cannot answer the denial question on its own: it
    /// returns null both for a real file and for one it failed to read, so a guard built only on it
    /// reports a file this process may not touch as "not a link" — and the repair, told the file is
    /// not there, takes the path that creates one over it. <see cref="File.GetAttributes(string)"/> is
    /// the call that raises the denial, so it is the call that answers this.
    /// </para>
    /// </remarks>
    public McpLinkObstruction InspectLink(string path)
    {
        FileSystemInfo entry = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        try
        {
            // LinkTarget answers even for a link whose target is gone, which is the one case a
            // File.Exists pre-check would have hidden.
            if (entry.LinkTarget is not null)
                return McpLinkObstruction.Link;

            FileAttributes attributes = File.GetAttributes(path);

            return attributes.HasFlag(FileAttributes.ReparsePoint)
                ? McpLinkObstruction.Link
                : McpLinkObstruction.None;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return McpLinkObstruction.None;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return McpLinkObstruction.Denied;
        }
    }

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] contents) => AtomicFile.WriteAllBytes(path, contents);

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);
}

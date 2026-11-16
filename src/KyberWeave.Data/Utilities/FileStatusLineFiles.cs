using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Utilities;

/// <summary>Reads and writes the status-line deployment on the real file system.</summary>
public sealed class FileStatusLineFiles : IStatusLineFiles
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    /// <inheritdoc />
    public string? ReadTextOrNull(string path) =>
        File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void WriteAllBytes(string path, ReadOnlySpan<byte> content) => File.WriteAllBytes(path, content);

    /// <inheritdoc />
    public void WriteTextAtomically(string path, string text) => AtomicFile.WriteAllText(path, text);

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public bool IsDirectoryEmpty(string path) => !Directory.EnumerateFileSystemEntries(path).Any();

    /// <inheritdoc />
    public void DeleteDirectory(string path) => Directory.Delete(path);

    /// <inheritdoc />
    public bool IsLinkOrReparsePoint(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        try
        {
            return info.LinkTarget is not null ||
                   (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Component does not exist yet.
            return false;
        }
    }
}

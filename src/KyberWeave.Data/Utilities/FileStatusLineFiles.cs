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

    /// <inheritdoc />
    public bool HasUserExecuteBit(string path)
    {
        if (OperatingSystem.IsWindows()) return true;

        try
        {
            return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A dangling symbolic link has no target to inspect.
            return false;
        }
    }

    /// <inheritdoc />
    public void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        UnixFileMode mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="SearchOption.AllDirectories" /> follows directory symbolic links, so a link inside the tree could
    ///     make the walk read files outside it (C# coding standard, "Safe filesystem enumeration").
    /// </remarks>
    public IEnumerable<string> EnumerateRegularFiles(string directory)
    {
        Stack<string> pending = new();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (FileSystemInfo entry in new DirectoryInfo(current).EnumerateFileSystemInfos(
                         "*",
                         new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
            {
                if (entry.LinkTarget is not null) continue;

                switch (entry)
                {
                    case DirectoryInfo child:
                        pending.Push(child.FullName);
                        break;
                    case FileInfo file:
                        yield return file.FullName;
                        break;
                }
            }
        }
    }
}

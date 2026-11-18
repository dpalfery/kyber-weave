using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Hooks;

/// <summary>Reads and writes the real file system.</summary>
public sealed class PhysicalFileSystem : IWritableFileSystem
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal)
    {
        "bin", "obj", "node_modules", ".git", ".venv", "dist", "out", "coverage", "TestResults", "artifacts", "target"
    };

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool PathExists(string path) => Path.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => _ = Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    /// <inheritdoc />
    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    /// <inheritdoc />
    public void WriteAllTextAtomic(string path, string contents) => AtomicFile.WriteAllText(path, contents);

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] bytes) => AtomicFile.WriteAllBytes(path, bytes);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        File.Move(sourcePath, destinationPath, overwrite);

    /// <inheritdoc />
    public void CreateNewFile(string path)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write);
    }

    /// <inheritdoc />
    public bool TryCreateNewFile(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write);
            return true;
        }
        catch (IOException) when (File.Exists(path) || Directory.Exists(path))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public void DeleteTreeNoFollow(string directoryPath)
    {
        if (!Directory.Exists(directoryPath) && !File.Exists(directoryPath) && !IsLink(directoryPath))
        {
            return;
        }

        if (IsLink(directoryPath))
        {
            DeleteLinkEntry(directoryPath);
            return;
        }

        EnumerationOptions options = new() { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (FileSystemInfo entry in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos("*", options).ToList())
        {
            if (IsLink(entry))
            {
                DeleteLinkEntry(entry.FullName);
            }
            else if (entry is DirectoryInfo)
            {
                DeleteTreeNoFollow(entry.FullName);
            }
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(entry.FullName, FileAttributes.Normal);
                }

                File.Delete(entry.FullName);
            }
        }

        Directory.Delete(directoryPath, false);
    }

    /// <inheritdoc />
    public void CreateFileLink(string linkPath, string targetPath)
    {
        CreateLinkParent(linkPath);
        File.CreateSymbolicLink(linkPath, targetPath);
    }

    /// <inheritdoc />
    public void CreateDirectoryLink(string linkPath, string targetPath)
    {
        CreateLinkParent(linkPath);
        Directory.CreateSymbolicLink(linkPath, targetPath);
    }

    /// <inheritdoc />
    public string CreateTemporaryDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    /// <inheritdoc />
    public string? GetLinkTarget(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        if (!info.Exists)
        {
            info = new DirectoryInfo(path);
        }

        return info.Exists ? info.LinkTarget : null;
    }

    /// <inheritdoc />
    public string? GetFinalLinkTarget(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is null)
        {
            info = new DirectoryInfo(path);
            if (info.LinkTarget is null)
            {
                return null;
            }
        }

        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            // A link whose target is gone has no final path, which is what null already means here.
            return null;
        }
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    /// <inheritdoc />
    public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);

    /// <inheritdoc />
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <inheritdoc />
    public long GetLength(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    /// <inheritdoc />
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [];

    /// <inheritdoc />
    public IEnumerable<FileSystemEntry> EnumerateEntries(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        EnumerationOptions options = new() { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
        {
            bool isLink = IsLink(entry);
            bool isDirectory = entry is DirectoryInfo || entry.Attributes.HasFlag(FileAttributes.Directory);
            yield return new FileSystemEntry(entry.FullName, isDirectory, isLink);
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
        EnumerateFiles(directory, searchPattern, recursive, followLinks: true);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive, bool followLinks)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(directory, searchPattern))
        {
            yield return file;
        }

        if (!recursive)
        {
            yield break;
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(child)))
            {
                continue;
            }

            if (!followLinks && IsLink(child))
            {
                continue;
            }

            foreach (string file in EnumerateFiles(child, searchPattern, recursive: true, followLinks))
            {
                yield return file;
            }
        }
    }

    private static bool IsLink(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        return info.LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;
    }

    /// <summary>A link is removed as a link, so its target survives whether it names a file or a directory.</summary>
    private static bool IsLink(FileSystemInfo? entry) =>
        entry is not null && (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint));

    private static void DeleteLinkEntry(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, false);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void CreateLinkParent(string linkPath)
    {
        string? parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent))
        {
            _ = Directory.CreateDirectory(parent);
        }
    }
}

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Unix and macOS implementation of <see cref="ISquadLinkFileSystem"/> using POSIX symbolic links.
/// </summary>
public sealed class UnixSquadLinkFileSystem : ISquadLinkFileSystem
{
    /// <inheritdoc />
    public void CreateFileLink(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        EnsureDirectoryExists(Path.GetDirectoryName(linkPath));
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new SquadLinkException(
                $"Failed to create symbolic link '{linkPath}' to target '{targetPath}': {ex.Message}",
                linkPath,
                targetPath,
                ex);
        }
    }

    /// <inheritdoc />
    public void CreateFolderLink(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        EnsureDirectoryExists(Path.GetDirectoryName(linkPath));
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new SquadLinkException(
                $"Failed to create folder symbolic link '{linkPath}' to target '{targetPath}': {ex.Message}",
                linkPath,
                targetPath,
                ex);
        }
    }

    /// <inheritdoc />
    public bool IsLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        FileInfo fileInfo = new(path);
        if (fileInfo.LinkTarget is not null)
        {
            return true;
        }

        DirectoryInfo dirInfo = new(path);
        if (dirInfo.LinkTarget is not null)
        {
            return true;
        }

        // ReparsePoint attribute check for symbolic links on Unix filesystems.
        if (fileInfo.Exists && fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        if (dirInfo.Exists && dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsLinkTo(string linkPath, string expectedTarget)
    {
        if (!IsLink(linkPath) || string.IsNullOrWhiteSpace(expectedTarget))
        {
            return false;
        }

        FileInfo fileInfo = new(linkPath);
        string? linkTarget = fileInfo.LinkTarget;
        if (linkTarget is null)
        {
            DirectoryInfo dirInfo = new(linkPath);
            linkTarget = dirInfo.LinkTarget;
        }

        if (linkTarget is null)
        {
            return false;
        }

        if (string.Equals(linkTarget, expectedTarget, StringComparison.Ordinal))
        {
            return true;
        }

        string resolvedTarget = Path.IsPathRooted(linkTarget)
            ? linkTarget
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath) ?? string.Empty, linkTarget));

        string normalizedResolved = Path.GetFullPath(resolvedTarget).TrimEnd('/', '\\');
        string normalizedExpected = Path.GetFullPath(expectedTarget).TrimEnd('/', '\\');

        if (string.Equals(normalizedResolved, normalizedExpected, StringComparison.Ordinal))
        {
            return true;
        }

        // Handle canonical symlinked system directories (such as /var -> /private/var on macOS).
        try
        {
            FileSystemInfo? resolvedFinalInfo = fileInfo.ResolveLinkTarget(true);
            if (resolvedFinalInfo is not null &&
                string.Equals(resolvedFinalInfo.FullName.TrimEnd('/', '\\'), normalizedExpected,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        catch (IOException)
        {
            // Link cannot be resolved (target might be absent); not an error for this check.
        }

        return false;
    }

    /// <inheritdoc />
    public void DeleteTreeNoFollow(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        if (!Directory.Exists(directoryPath) && !File.Exists(directoryPath) && !IsLink(directoryPath))
        {
            return;
        }

        // If the path itself is a link, remove the link entry without following into its target.
        if (IsLink(directoryPath))
        {
            DirectoryInfo rootLinkInfo = new(directoryPath);
            if (rootLinkInfo.Attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(directoryPath, false);
            }
            else
            {
                File.Delete(directoryPath);
            }

            return;
        }

        DirectoryInfo dirInfo = new(directoryPath);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };

        // Materialize entries into a list first so the directory handle is released before deletion.
        List<FileSystemInfo> entries = dirInfo.EnumerateFileSystemInfos("*", options).ToList();

        foreach (FileSystemInfo entry in entries)
        {
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (entry is DirectoryInfo || entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.Delete(entry.FullName, false);
                }
                else
                {
                    File.Delete(entry.FullName);
                }
            }
            else if (entry is DirectoryInfo subDirectory)
            {
                DeleteTreeNoFollow(subDirectory.FullName);
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

    private static void EnsureDirectoryExists(string? directoryPath)
    {
        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }
}

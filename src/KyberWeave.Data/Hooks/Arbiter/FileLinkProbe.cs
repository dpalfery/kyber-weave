using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>Finds symbolic links by asking the file system about each existing entry on a path.</summary>
public sealed class FileLinkProbe : IFileLinkProbe
{
    /// <inheritdoc/>
    public bool HasLinkBelow(string root, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        string current = root.TrimEnd('/');
        foreach (string segment in fullPath[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = $"{current}/{segment}";
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!entry.Exists)
            {
                return false;
            }

            if (entry.LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }
}

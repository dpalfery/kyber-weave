using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Testing.Squad;

/// <summary>Snapshots a tree through the file-system seam, so the same capture reads the fake and the real disk.</summary>
public static class SquadStateTreeCapture
{
    /// <summary>Snapshots every path beneath a directory.</summary>
    /// <param name="files">The file system to read. A fault plan on it counts these reads, so pass the undecorated one.</param>
    /// <param name="root">The directory the keys are relative to.</param>
    /// <returns>The tree, keyed by '/'-separated relative path in ordinal order.</returns>
    public static IReadOnlyDictionary<string, SquadTreeEntry> Capture(IFileSystem files, string root)
    {
        ArgumentNullException.ThrowIfNull(files);
        SortedDictionary<string, SquadTreeEntry> tree = new(StringComparer.Ordinal);
        CaptureDirectory(files, root, root, tree);
        return tree;
    }

    private static void CaptureDirectory(
        IFileSystem files,
        string directory,
        string root,
        SortedDictionary<string, SquadTreeEntry> tree)
    {
        foreach (FileSystemEntry entry in files.EnumerateEntries(directory))
        {
            string key = Path.GetRelativePath(root, entry.Path).Replace(Path.DirectorySeparatorChar, '/');
            if (entry.IsLink)
            {
                tree[key] = new SquadTreeEntry(
                    entry.IsDirectory ? SquadTreeEntryKind.DirectorySymbolicLink : SquadTreeEntryKind.FileSymbolicLink,
                    LinkTarget: files.GetLinkTarget(entry.Path));
            }
            else if (entry.IsDirectory)
            {
                tree[key] = new SquadTreeEntry(SquadTreeEntryKind.Directory);
                CaptureDirectory(files, entry.Path, root, tree);
            }
            else
            {
                tree[key] = new SquadTreeEntry(SquadTreeEntryKind.File, files.ReadAllBytes(entry.Path));
            }
        }
    }
}

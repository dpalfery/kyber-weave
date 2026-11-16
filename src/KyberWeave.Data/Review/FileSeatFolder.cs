using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Review;

/// <summary>Lists a review seat folder on the real file system.</summary>
public sealed class FileSeatFolder : ISeatFolder
{
    /// <inheritdoc />
    public bool Exists(string folder) => Directory.Exists(folder);

    /// <inheritdoc />
    public IReadOnlyList<SeatFolderEntry> ListMarkdown(string folder)
    {
        // IgnoreInaccessible is off: a folder the process cannot list must be refused, not read as an empty folder
        // whose every lens then looks unaccounted for, or whose seats silently vanish.
        FileSystemInfo[] entries = new DirectoryInfo(folder).GetFileSystemInfos(
            "*.md",
            new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false });
        return [.. entries.Select(entry => new SeatFolderEntry(entry.Name, entry.FullName, entry is FileInfo))];
    }
}

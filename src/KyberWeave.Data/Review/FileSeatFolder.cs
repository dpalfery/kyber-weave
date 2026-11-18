using System.IO.Enumeration;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Review;

/// <summary>Lists a review seat folder through the file-system seam.</summary>
public sealed class FileSeatFolder(IFileSystem files, IProcessEnvironment environment) : ISeatFolder
{
    private const string MarkdownPattern = "*.md";

    /// <inheritdoc />
    public bool Exists(string folder) => files.DirectoryExists(folder);

    /// <inheritdoc />
    public IReadOnlyList<SeatFolderEntry> ListMarkdown(string folder)
    {
        // The listing refuses a folder the process cannot read, rather than reading it as an empty folder whose
        // every lens then looks unaccounted for, or whose seats silently vanish. Names match the way the platform's
        // own directory listing matches them: ignoring case on Windows and macOS.
        bool ignoreCase = environment.OperatingSystem is OperatingSystemKind.Windows or OperatingSystemKind.MacOS;
        return [.. files.EnumerateEntries(folder)
            .Where(entry => FileSystemName.MatchesSimpleExpression(MarkdownPattern, Path.GetFileName(entry.Path), ignoreCase))
            .Select(entry => new SeatFolderEntry(Path.GetFileName(entry.Path), entry.Path, !entry.IsDirectory))];
    }
}

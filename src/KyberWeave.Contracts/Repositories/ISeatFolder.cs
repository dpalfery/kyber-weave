namespace KyberWeave.Contracts.Repositories;

/// <summary>One entry of a review seat folder.</summary>
/// <param name="Name">The entry's file name.</param>
/// <param name="FullPath">The entry's full path.</param>
/// <param name="IsFile">Whether the entry is a regular file rather than a directory.</param>
public readonly record struct SeatFolderEntry(string Name, string FullPath, bool IsFile);

/// <summary>Lists the seat output files a review council wrote into one folder.</summary>
public interface ISeatFolder
{
    /// <summary>Whether <paramref name="folder"/> exists as a directory.</summary>
    /// <param name="folder">The seat folder.</param>
    bool Exists(string folder);

    /// <summary>The <c>*.md</c> entries directly inside <paramref name="folder"/>, in no particular order.</summary>
    /// <remarks>An inaccessible folder is not read as an empty one: the listing throws the file-system exception instead.</remarks>
    /// <param name="folder">The seat folder.</param>
    IReadOnlyList<SeatFolderEntry> ListMarkdown(string folder);
}

using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="ISeatFolder"/>: the entries a test declares, and the listing failure a real folder's permissions produce.</summary>
/// <remarks>
/// A folder is present when the test names it, and lists only the entries the test declared for it, so a
/// production read of the wrong folder fails the case instead of answering another folder's entries.
/// <see cref="ListFailure"/> stands in for the file-system exception a folder the process cannot list throws, which
/// the in-memory file system has no permission bits to produce.
/// </remarks>
internal sealed class FakeSeatFolder : ISeatFolder
{
    /// <summary>The folders that exist, by full path.</summary>
    public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the entries declared for each folder, so two folders in one test can differ.</summary>
    private Dictionary<string, List<SeatFolderEntry>> ByFolder { get; } = new(StringComparer.Ordinal);

    /// <summary>Declares the entries one folder lists, and that the folder exists.</summary>
    /// <param name="folder">The seat folder, by full path.</param>
    /// <param name="entries">The entries that folder lists.</param>
    public void AddEntries(string folder, params SeatFolderEntry[] entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(entries);
        Folders.Add(folder);
        ByFolder[folder] = [.. entries];
    }

    /// <summary>The exception the listing throws, or null when it answers <see cref="Entries"/>.</summary>
    public Exception? ListFailure { get; set; }

    /// <inheritdoc />
    public bool Exists(string folder) => Folders.Contains(folder);

    /// <inheritdoc />
    public IReadOnlyList<SeatFolderEntry> ListMarkdown(string folder)
    {
        if (ListFailure is not null)
            throw ListFailure;

        return ByFolder.TryGetValue(folder, out List<SeatFolderEntry>? entries) ? [.. entries] : [];
    }
}

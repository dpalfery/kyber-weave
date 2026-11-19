using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="ISeatFolder"/>: the entries a test declares, and the listing failure a real folder's permissions produce.</summary>
/// <remarks>
/// A folder is present when the test names it. <see cref="ListFailure"/> stands in for the file-system exception a
/// folder the process cannot list throws, which the in-memory file system has no permission bits to produce.
/// </remarks>
internal sealed class FakeSeatFolder : ISeatFolder
{
    /// <summary>The folders that exist, by full path.</summary>
    public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);

    /// <summary>The entries every existing folder lists.</summary>
    public List<SeatFolderEntry> Entries { get; } = [];

    /// <summary>The exception the listing throws, or null when it answers <see cref="Entries"/>.</summary>
    public Exception? ListFailure { get; set; }

    /// <inheritdoc />
    public bool Exists(string folder) => Folders.Contains(folder);

    /// <inheritdoc />
    public IReadOnlyList<SeatFolderEntry> ListMarkdown(string folder) =>
        ListFailure is null ? Entries : throw ListFailure;
}

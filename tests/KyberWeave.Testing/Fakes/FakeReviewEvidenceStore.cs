using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IReviewEvidenceStore"/>: keeps each cycle folder's files in memory, so a test needs no disk.</summary>
internal sealed class FakeReviewEvidenceStore : IReviewEvidenceStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _files = new(StringComparer.Ordinal);

    /// <summary>The folders a caller asked to create, in order.</summary>
    public List<string> CreatedFolders { get; } = [];

    /// <summary>The text written to one path, or null when nothing was written there.</summary>
    /// <param name="path">The full path.</param>
    public string? TextAt(string path) =>
        _files.Values.Select(files => files.TryGetValue(path, out string? text) ? text : null).FirstOrDefault(text => text is not null);

    /// <summary>Whether anything was written to one path.</summary>
    /// <param name="path">The full path.</param>
    public bool WasWritten(string path) => TextAt(path) is not null;

    /// <inheritdoc />
    public void EnsureFolder(string folder)
    {
        CreatedFolders.Add(folder);
        _ = _files.TryAdd(folder, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public void WriteAtomically(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        if (!_files.TryGetValue(folder, out Dictionary<string, string>? files))
        {
            files = new Dictionary<string, string>(StringComparer.Ordinal);
            _files[folder] = files;
        }

        files[path] = text;
    }
}

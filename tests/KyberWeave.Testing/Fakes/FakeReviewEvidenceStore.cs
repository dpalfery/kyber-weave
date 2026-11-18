using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IReviewEvidenceStore"/>: keeps every cycle file in memory, so a test needs no disk.</summary>
internal sealed class FakeReviewEvidenceStore : IReviewEvidenceStore
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>The folders a caller asked to create, in order.</summary>
    public List<string> CreatedFolders { get; } = [];

    /// <summary>Every path written, in order.</summary>
    public List<string> Written { get; } = [];

    /// <summary>The file name of every path written, in order.</summary>
    public IReadOnlyList<string> WrittenNames() => [.. Written.Select(path => Path.GetFileName(path))];

    /// <summary>The text written to one path, or null when nothing was written there.</summary>
    /// <param name="path">The full path.</param>
    public string? TextAt(string path) => _files.TryGetValue(path, out string? text) ? text : null;

    /// <summary>Whether anything was written to one path.</summary>
    /// <param name="path">The full path.</param>
    public bool WasWritten(string path) => _files.ContainsKey(path);

    /// <inheritdoc />
    public void EnsureFolder(string folder) => CreatedFolders.Add(folder);

    /// <inheritdoc />
    public void WriteAtomically(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _files[path] = text;
        Written.Add(path);
    }
}

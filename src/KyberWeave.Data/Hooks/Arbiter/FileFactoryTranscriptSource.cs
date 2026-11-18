using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>Reads a Factory session transcript from disk, skipping a file that is missing or larger than the caller allows.</summary>
public sealed class FileFactoryTranscriptSource(IFileSystem files) : IFactoryTranscriptSource
{
    /// <inheritdoc/>
    public IEnumerable<string>? ReadLines(string path, long maxBytes)
    {
        return !files.FileExists(path) || files.GetLength(path) > maxBytes ? null : files.ReadLines(path);
    }
}

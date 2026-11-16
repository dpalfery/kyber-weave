using KyberWeave.Arbiter.Hooks.Adapters;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>Reads a Factory session transcript from disk, skipping a file that is missing or larger than the caller allows.</summary>
public sealed class FileFactoryTranscriptSource : IFactoryTranscriptSource
{
    /// <inheritdoc/>
    public IEnumerable<string>? ReadLines(string path, long maxBytes)
    {
        FileInfo file = new(path);
        return !file.Exists || file.Length > maxBytes ? null : File.ReadLines(path);
    }
}

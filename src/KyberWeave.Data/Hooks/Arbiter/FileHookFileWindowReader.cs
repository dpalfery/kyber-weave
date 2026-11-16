using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>Reads a window of lines from a file on disk.</summary>
public sealed class FileHookFileWindowReader : IHookFileWindowReader
{
    /// <inheritdoc/>
    public IReadOnlyList<string>? ReadWindow(string path, long maxBytes, int skip, int take)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (new FileInfo(path).Length > maxBytes)
            {
                return null;
            }

            return [.. File.ReadLines(path).Skip(skip).Take(take)];
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
    }
}

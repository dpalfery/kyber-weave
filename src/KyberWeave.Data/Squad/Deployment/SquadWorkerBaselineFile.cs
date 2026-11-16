using System.Collections.Concurrent;
using System.Text;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Reads the shared worker baseline block from a Squad source tree, caching it by file stamp.</summary>
public static class SquadWorkerBaselineFile
{
    private const int MaxCacheEntries = 64;

    private sealed record CacheEntry(DateTime LastWriteTimeUtc, long FileLength, string? Block);

    private static readonly ConcurrentDictionary<string, CacheEntry> CachedBlocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Empties the baseline block cache.</summary>
    public static void ClearCache() => CachedBlocks.Clear();

    /// <summary>
    /// Reads the injection block from <paramref name="sourceDirectory"/>, or returns null when the
    /// tree carries no baseline file.
    /// </summary>
    /// <remarks>
    /// A test fixture renders from a minimal source tree and has no baseline; a shipped corpus does,
    /// and the suite fails when a render carries no block rather than letting the render go quiet.
    /// </remarks>
    /// <param name="sourceDirectory">The Squad source root the render loaded from.</param>
    /// <returns>The block, tags included, or null when the tree carries no baseline file.</returns>
    public static string? ReadBlock(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        string path = Path.Combine(normalized, SquadWorkerBaselineInjector.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                if (CachedBlocks.TryGetValue(normalized, out CacheEntry? missing) && missing.LastWriteTimeUtc == DateTime.MinValue)
                    return missing.Block;

                if (CachedBlocks.Count >= MaxCacheEntries)
                    CachedBlocks.Clear();

                CachedBlocks[normalized] = new CacheEntry(DateTime.MinValue, 0, null);
                return null;
            }

            DateTime lastWrite = info.LastWriteTimeUtc;
            long length = info.Length;
            if (CachedBlocks.TryGetValue(normalized, out CacheEntry? entry)
                && entry.LastWriteTimeUtc == lastWrite
                && entry.FileLength == length)
            {
                return entry.Block;
            }

            string? block = ReadBlockUncached(path);
            if (CachedBlocks.Count >= MaxCacheEntries)
                CachedBlocks.Clear();

            CachedBlocks[normalized] = new CacheEntry(lastWrite, length, block);
            return block;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return ReadBlockUncached(path);
        }
    }

    private static string? ReadBlockUncached(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        return SquadWorkerBaselineInjector.ExtractBlock(text, path);
    }
}

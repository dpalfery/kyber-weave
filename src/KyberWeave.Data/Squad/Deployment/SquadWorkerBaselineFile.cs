using System.Collections.Concurrent;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Reads the shared worker baseline block from a Squad source tree, caching it by file stamp.</summary>
/// <remarks>The cache belongs to the instance, so two readers never share a stamp and a test never inherits another's.</remarks>
/// <param name="files">Reads the baseline file and its stamp.</param>
/// <param name="paths">Turns the source directory into a full path.</param>
/// <param name="environment">Supplies the base directory a relative source directory resolves against.</param>
public sealed class SquadWorkerBaselineFile(
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : ISquadWorkerBaselineFile
{
    private const int MaxCacheEntries = 64;

    private sealed record CacheEntry(DateTime LastWriteTimeUtc, long FileLength, string? Block);

    private readonly ConcurrentDictionary<string, CacheEntry> _cachedBlocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    /// A test fixture renders from a minimal source tree and has no baseline; a shipped corpus does,
    /// and the suite fails when a render carries no block rather than letting the render go quiet.
    /// </remarks>
    public string? ReadBlock(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        string normalized = Path.TrimEndingDirectorySeparator(paths.GetFullPath(sourceDirectory, environment.CurrentDirectory));
        string path = Path.Combine(normalized, SquadWorkerBaselineInjector.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            if (!files.FileExists(path))
            {
                if (_cachedBlocks.TryGetValue(normalized, out CacheEntry? missing) && missing.LastWriteTimeUtc == DateTime.MinValue)
                    return missing.Block;

                if (_cachedBlocks.Count >= MaxCacheEntries)
                    _cachedBlocks.Clear();

                _cachedBlocks[normalized] = new CacheEntry(DateTime.MinValue, 0, null);
                return null;
            }

            DateTime lastWrite = files.GetLastWriteTimeUtc(path);
            long length = files.GetLength(path);
            if (_cachedBlocks.TryGetValue(normalized, out CacheEntry? entry)
                && entry.LastWriteTimeUtc == lastWrite
                && entry.FileLength == length)
            {
                return entry.Block;
            }

            string? block = ReadBlockUncached(path);
            if (_cachedBlocks.Count >= MaxCacheEntries)
                _cachedBlocks.Clear();

            _cachedBlocks[normalized] = new CacheEntry(lastWrite, length, block);
            return block;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return ReadBlockUncached(path);
        }
    }

    private string? ReadBlockUncached(string path)
    {
        string text;
        try
        {
            text = files.ReadAllText(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        return SquadWorkerBaselineInjector.ExtractBlock(text, path);
    }
}

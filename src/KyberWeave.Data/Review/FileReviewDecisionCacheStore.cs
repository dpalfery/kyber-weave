using System.Text.Json;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Keeps one review cycle's decision-model answers in a JSON file.</summary>
public sealed class FileReviewDecisionCacheStore(IWritableFileSystem files, IPathResolver paths, IProcessEnvironment environment)
    : IReviewDecisionCacheStore
{
    /// <inheritdoc />
    public IReadOnlyDictionary<string, ReviewCachedAnswer> Load(string path)
    {
        try
        {
            if (files.FileExists(path)
                && JsonSerializer.Deserialize<Dictionary<string, ReviewCachedAnswer>>(files.ReadAllText(path)) is { } read)
            {
                return read;
            }
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException)
        {
            // An unreadable cache only costs the questions again.
        }

        return new Dictionary<string, ReviewCachedAnswer>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool Save(string path, IReadOnlyDictionary<string, ReviewCachedAnswer> answers)
    {
        try
        {
            files.CreateDirectory(Path.GetDirectoryName(paths.GetFullPath(path, environment.CurrentDirectory))!);
            files.WriteAllText(path, JsonSerializer.Serialize(answers));
            return true;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            // Not kept: the next run asks again.
            return false;
        }
    }
}

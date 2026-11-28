using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.ModelTargets;

namespace KyberWeave.Data.Arbiter;

/// <summary>Reads the model targets table from the arbiter directory and fails closed on any damage.</summary>
/// <param name="fileSystem">The file system the table is read from.</param>
public sealed class FileArbiterModelTargetsReader(IFileSystem fileSystem) : IArbiterModelTargetsReader
{
    private static readonly JsonSerializerOptions ReadOptions = new();

    /// <inheritdoc />
    public ArbiterModelTargets? TryRead(string arbiterDirectory, string harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        if (harness.Length == 0 || harness.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        string path = Path.Combine(arbiterDirectory, $"model-targets.{harness}.json");
        try
        {
            if (!fileSystem.FileExists(path))
                return null;

            ArbiterModelTargets? targets = JsonSerializer.Deserialize<ArbiterModelTargets>(
                fileSystem.ReadAllText(path), ReadOptions);
            return targets is not null && targets.IsCompleteFor(harness) ? targets : null;
        }
        catch (Exception exception) when (exception is IOException or
                                              UnauthorizedAccessException or
                                              JsonException or
                                              NotSupportedException)
        {
            // A file that cannot be read or parsed is the same as no file: the caller falls back to its
            // defaults rather than throwing over a damaged arbiter state file.
            return null;
        }
    }
}

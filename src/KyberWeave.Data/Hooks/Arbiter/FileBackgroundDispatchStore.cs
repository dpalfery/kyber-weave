using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>
/// The background dispatch records, one JSON file per agent under <c>&lt;arbiter directory&gt;/background/</c>. The
/// file name is a digest of the session and a digest of the agent id, so one session's records share a prefix and no
/// harness-supplied text reaches the file system. A record is written to a temporary file and moved into place, so a
/// reader never sees half of one. Records older than a week are pruned, at most once an hour.
/// </summary>
public sealed class FileBackgroundDispatchStore : IBackgroundDispatchStore
{
    private const string FolderName = "background";
    private const string PruneMarker = ".last-prune";
    private const int SessionKeyLength = 16;

    private static readonly TimeSpan StaleAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc/>
    public void Save(string arbiterDirectory, BackgroundDispatchRecord record)
    {
        ArgumentNullException.ThrowIfNull(arbiterDirectory);
        ArgumentNullException.ThrowIfNull(record);
        string directory = Path.Combine(arbiterDirectory, FolderName);
        Directory.CreateDirectory(directory);
        PruneStale(directory);
        string path = Path.Combine(directory, FileNameOf(record.Dispatch.SessionId, record.Dispatch.AgentId));
        string temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(record), Utf8);
        File.Move(temp, path, overwrite: true);
    }

    /// <inheritdoc/>
    public BackgroundDispatchRecord? Find(string arbiterDirectory, string sessionId, string agentId)
    {
        ArgumentNullException.ThrowIfNull(arbiterDirectory);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(agentId);
        return Read(Path.Combine(arbiterDirectory, FolderName, FileNameOf(sessionId, agentId)));
    }

    /// <inheritdoc/>
    public IReadOnlyList<BackgroundDispatchRecord> ForSession(string arbiterDirectory, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(arbiterDirectory);
        ArgumentNullException.ThrowIfNull(sessionId);
        string directory = Path.Combine(arbiterDirectory, FolderName);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(directory, SessionKey(sessionId) + "-*.json")
            .Order(StringComparer.Ordinal)
            .Select(Read)
            .OfType<BackgroundDispatchRecord>()
            .Where(record => string.Equals(record.Dispatch.SessionId, sessionId, StringComparison.Ordinal))];
    }

    private static BackgroundDispatchRecord? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<BackgroundDispatchRecord>(File.ReadAllText(path, Utf8));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // A record that is gone or unreadable is an agent the Arbiter is not following.
            return null;
        }
    }

    private static string FileNameOf(string sessionId, string agentId) =>
        $"{SessionKey(sessionId)}-{Digest(agentId)}.json";

    private static string SessionKey(string sessionId) => Digest(sessionId)[..SessionKeyLength];

    private static string Digest(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Pruning is housekeeping: a file that cannot be deleted now is retried on a later save, and it must not stop
    // the dispatch being recorded.
    private static void PruneStale(string directory)
    {
        string marker = Path.Combine(directory, PruneMarker);
        try
        {
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < PruneInterval)
            {
                return;
            }

            File.WriteAllText(marker, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        DateTime cutoff = DateTime.UtcNow - StaleAge;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retried on a later save; a stale record that cannot be pruned is harmless.
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>
/// The background dispatch records, one JSON file per agent under <c>&lt;arbiter directory&gt;/background/</c>. The
/// file name is a digest of the session and a digest of the agent id, so one session's records share a prefix and no
/// harness-supplied text reaches the file system. A record is written to a temporary file and moved into place, so a
/// reader never sees half of one. Records older than a week are pruned, at most once an hour. The reports kept for
/// agents with no followed dispatch live the same way under <c>&lt;arbiter directory&gt;/background-kept/</c>.
/// </summary>
public sealed class FileBackgroundDispatchStore(IWritableFileSystem files, TimeProvider time, IIdGenerator ids) : IBackgroundDispatchStore
{
    private const string FolderName = "background";
    private const string KeptFolderName = "background-kept";
    private const string PruneMarker = ".last-prune";
    private const int SessionKeyLength = 16;

    private static readonly TimeSpan StaleAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    /// <inheritdoc/>
    public void Save(string arbiterDirectory, BackgroundDispatchRecord record)
    {
        ArgumentNullException.ThrowIfNull(arbiterDirectory);
        ArgumentNullException.ThrowIfNull(record);
        string directory = Path.Combine(arbiterDirectory, FolderName);
        files.CreateDirectory(directory);
        PruneStale(directory);
        string path = Path.Combine(directory, FileNameOf(record.Dispatch.SessionId, record.Dispatch.AgentId));
        string temp = Path.Combine(directory, $".{ids.NewGuid():N}.tmp");
        files.WriteAllText(temp, JsonSerializer.Serialize(record));
        files.MoveFile(temp, path, overwrite: true);
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
        if (!files.DirectoryExists(directory))
        {
            return [];
        }

        return [.. files.EnumerateFiles(directory, SessionKey(sessionId) + "-*.json", recursive: false)
            .Order(StringComparer.Ordinal)
            .Select(Read)
            .OfType<BackgroundDispatchRecord>()
            .Where(record => string.Equals(record.Dispatch.SessionId, sessionId, StringComparison.Ordinal))];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A kept report lives in the sibling <c>background-kept/</c> folder, under the same session and agent file name a
    /// record uses, so <see cref="ForSession"/> never reads one as a followed dispatch. It is written and pruned the
    /// way a record is: through a temporary file moved into place, and swept after a week when no post takes it.
    /// </remarks>
    public void Keep(string arbiterDirectory, string sessionId, string agentId, string message)
    {
        ArgumentNullException.ThrowIfNull(arbiterDirectory);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(message);
        string directory = Path.Combine(arbiterDirectory, KeptFolderName);
        files.CreateDirectory(directory);
        PruneStale(directory);
        string path = Path.Combine(directory, FileNameOf(sessionId, agentId));
        string temp = Path.Combine(directory, $".{ids.NewGuid():N}.tmp");
        files.WriteAllText(temp, JsonSerializer.Serialize(message));
        files.MoveFile(temp, path, overwrite: true);
    }

    /// <inheritdoc/>
    public string? Take(string arbiterDirectory, string sessionId, string agentId)
    {
        ArgumentNullException.ThrowIfNull(arbiterDirectory);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(agentId);
        string path = Path.Combine(arbiterDirectory, KeptFolderName, FileNameOf(sessionId, agentId));
        string? message;
        try
        {
            message = JsonSerializer.Deserialize<string>(files.ReadAllText(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException)
        {
            // An unreadable kept report is no report; it is removed below so it is not read again.
            message = null;
        }

        // Taking removes the report, so a second completed event for the same agent finds none and records nothing.
        try
        {
            files.DeleteFile(path);
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            // Removing an unreadable report is best-effort; permission-denied deletions must not crash the hook.
        }

        return message;
    }

    private BackgroundDispatchRecord? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<BackgroundDispatchRecord>(files.ReadAllText(path));
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException)
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
    private void PruneStale(string directory)
    {
        string marker = Path.Combine(directory, PruneMarker);
        try
        {
            if (files.FileExists(marker) && time.GetUtcNow().UtcDateTime - files.GetLastWriteTimeUtc(marker) < PruneInterval)
            {
                return;
            }

            files.WriteAllText(marker, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        DateTime cutoff = time.GetUtcNow().UtcDateTime - StaleAge;
        foreach (string file in files.EnumerateFiles(directory, "*", recursive: false))
        {
            try
            {
                if (files.GetLastWriteTimeUtc(file) < cutoff)
                {
                    files.DeleteFile(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retried on a later save; a stale record that cannot be pruned is harmless.
            }
        }
    }
}

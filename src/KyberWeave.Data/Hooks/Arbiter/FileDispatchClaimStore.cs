using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>
/// The dispatch claim files. A claim is <c>&lt;project&gt;/artifacts/arbiter/claims/&lt;id&gt;.&lt;pre|post&gt;</c>, where the id is
/// the harness's <c>tool_use_id</c> when it is a safe file name, and its SHA-256 otherwise. The file is written to a
/// temporary file and moved into place without overwriting, so exactly one creator wins a race and a reader never sees a
/// claim before its owner line is in it. Claims older than a day are pruned, at most once an hour, so the folder does not
/// grow with every dispatch ever made and no dispatch pays for a scan of it.
/// </summary>
public sealed class FileDispatchClaimStore(IWritableFileSystem files, TimeProvider time, IIdGenerator ids) : IDispatchClaimStore
{
    /// <summary>How long a user-level hook waits for a project hook to claim a dispatch it can see.</summary>
    internal static readonly TimeSpan WaitWindow = TimeSpan.FromMilliseconds(1500);

    private const int MaxSafeIdLength = 128;
    private const string PruneMarker = ".last-prune";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private static readonly Regex SafeIdPattern = new(
        "^[A-Za-z0-9_-]{1,128}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <inheritdoc/>
    public bool TryClaim(string projectRoot, DispatchClaimKey key, string owner)
    {
        string path = ClaimPath(projectRoot, key);
        string directory = Path.GetDirectoryName(path)!;
        files.CreateDirectory(directory);
        PruneStale(directory);
        string temp = Path.Combine(directory, $".{ids.NewGuid():N}.tmp");
        try
        {
            // A failure to write the temporary file propagates: the caller evaluates without a claim.
            files.WriteAllText(temp, owner + "\n");
            try
            {
                files.MoveFile(temp, path, overwrite: false);
                return true;
            }
            catch (IOException) when (ReadOwner(projectRoot, key) is not null)
            {
                // Another hook's claim is in place with its owner written, so that owner decides what
                // this hook does. Any other move failure propagates for the same reason.
                return false;
            }
        }
        finally
        {
            try
            {
                files.DeleteFile(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file is pruned with the stale claims.
            }
        }
    }

    /// <inheritdoc/>
    public string? ReadOwner(string projectRoot, DispatchClaimKey key)
    {
        try
        {
            string? owner = files.ReadLines(ClaimPath(projectRoot, key)).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(owner) ? null : owner;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public bool WaitForClaim(string projectRoot, DispatchClaimKey key)
    {
        string path = ClaimPath(projectRoot, key);
        long started = time.GetTimestamp();
        while (!files.FileExists(path))
        {
            if (time.GetElapsedTime(started) >= WaitWindow)
            {
                return false;
            }

            Task.Delay(PollInterval, time).GetAwaiter().GetResult();
        }

        return true;
    }

    internal static string ClaimPath(string projectRoot, DispatchClaimKey key)
    {
        string id = key.ToolUseId.Length <= MaxSafeIdLength && SafeIdPattern.IsMatch(key.ToolUseId)
            ? key.ToolUseId
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToolUseId)));
        return Path.Combine(ArbiterStateDirectory.ClaimsDirectory(ArbiterStateDirectory.ForProject(projectRoot)), $"{id}.{key.Phase}");
    }

    // Pruning is housekeeping: a file that cannot be deleted now is retried on the next claim, and it
    // must not stop the dispatch that is being claimed.
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
                // Retried on the next claim; a claim that cannot be pruned is harmless.
            }
        }
    }
}

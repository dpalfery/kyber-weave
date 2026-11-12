using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The one-evaluation-per-dispatch claim. The project-scope hook and the user-level hook can both see
/// the same dispatch; whichever creates the claim file first evaluates it, and the other allows or
/// defers without writing a second decision.
/// </summary>
/// <remarks>
/// A claim is <c>&lt;project&gt;/artifacts/arbiter/claims/&lt;id&gt;.&lt;pre|post&gt;</c>, where the id is the
/// harness's <c>tool_use_id</c> when it is a safe file name, and its SHA-256 otherwise. The file is created
/// written to a temporary file and moved into place without overwriting, so exactly one creator wins a race
/// and a reader never sees a claim before its owner line is in it. The line names the owner: <c>project</c>
/// or <c>global</c>. Claims older than a day are pruned, at most once an hour, so the folder does not grow
/// with every dispatch ever made and no dispatch pays for a scan of it.
/// </remarks>
internal static class DispatchClaim
{
    /// <summary>The owner line of a claim taken by the project-scope hook.</summary>
    internal const string ProjectOwner = "project";

    /// <summary>The owner line of a claim taken by the user-level hook.</summary>
    internal const string GlobalOwner = "global";

    /// <summary>How long a user-level hook waits for a project hook to claim a dispatch it can see.</summary>
    internal static readonly TimeSpan WaitWindow = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// The longest <c>tool_use_id</c> that can be claimed. A longer value is not an id the harness sends, so
    /// a dispatch carrying one is evaluated without a claim rather than hashed.
    /// </summary>
    internal const int MaxClaimableIdLength = 256;

    private const int MaxSafeIdLength = 128;
    private const string PruneMarker = ".last-prune";

    private static readonly Regex SafeIdPattern = new(
        "^[A-Za-z0-9_-]{1,128}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Takes the claim for <paramref name="key"/>. Returns false when a claim already exists.</summary>
    /// <param name="projectRoot">The repository root whose <c>artifacts/arbiter/claims</c> folder holds the claim.</param>
    /// <param name="key">The dispatch event being claimed.</param>
    /// <param name="owner"><see cref="ProjectOwner"/> or <see cref="GlobalOwner"/>, written as the claim's one line.</param>
    internal static bool TryClaim(string projectRoot, DispatchClaimKey key, string owner)
    {
        string path = ClaimPath(projectRoot, key);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        PruneStale(directory);

        string temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            // A failure to write the temporary file propagates: the caller evaluates without a claim.
            File.WriteAllText(temp, owner + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                File.Move(temp, path, overwrite: false);
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
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file is pruned with the stale claims.
            }
        }
    }

    /// <summary>The owner line of the existing claim for <paramref name="key"/>, or null when there is none.</summary>
    internal static string? ReadOwner(string projectRoot, DispatchClaimKey key)
    {
        try
        {
            string? owner = File.ReadLines(ClaimPath(projectRoot, key)).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(owner) ? null : owner;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits up to <see cref="WaitWindow"/>, polling every 50 ms, for a claim to appear for
    /// <paramref name="key"/>. Returns true as soon as one exists, false when the window closes first.
    /// </summary>
    internal static bool WaitForClaim(string projectRoot, DispatchClaimKey key)
    {
        string path = ClaimPath(projectRoot, key);
        Stopwatch clock = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (clock.Elapsed >= WaitWindow)
            {
                return false;
            }

            Thread.Sleep(PollInterval);
        }

        return true;
    }

    internal static string ClaimPath(string projectRoot, DispatchClaimKey key)
    {
        string id = key.ToolUseId.Length <= MaxSafeIdLength && SafeIdPattern.IsMatch(key.ToolUseId)
            ? key.ToolUseId
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToolUseId)));
        return Path.Combine(projectRoot, "artifacts", "arbiter", "claims", $"{id}.{key.Phase}");
    }

    // Pruning is housekeeping: a file that cannot be deleted now is retried on the next claim, and it
    // must not stop the dispatch that is being claimed.
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
                // Retried on the next claim; a claim that cannot be pruned is harmless.
            }
        }
    }
}

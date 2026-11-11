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
/// with <see cref="FileMode.CreateNew"/>, so exactly one creator wins a race, and it holds one line naming
/// the owner: <c>project</c> or <c>global</c>. Claims older than a day are pruned on each new claim, so the
/// folder does not grow with every dispatch ever made.
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

        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(owner);
            writer.Write('\n');
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another hook created the claim first. Its owner decides what this hook does.
            return false;
        }
    }

    /// <summary>The owner line of the existing claim for <paramref name="key"/>, or null when there is none.</summary>
    internal static string? ReadOwner(string projectRoot, DispatchClaimKey key)
    {
        string path = ClaimPath(projectRoot, key);
        return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
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
        string id = SafeIdPattern.IsMatch(key.ToolUseId)
            ? key.ToolUseId
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToolUseId)));
        return Path.Combine(projectRoot, "artifacts", "arbiter", "claims", $"{id}.{key.Phase}");
    }

    // Pruning is housekeeping: a file that cannot be deleted now is retried on the next claim, and it
    // must not stop the dispatch that is being claimed.
    private static void PruneStale(string directory)
    {
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

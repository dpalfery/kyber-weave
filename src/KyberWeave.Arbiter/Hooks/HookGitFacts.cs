using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The hook's git facts: the fact bag already carries every fact the trigger
/// declares (absent as null), so this enriches only what a hook process can state
/// truthfully from a live working tree.
/// </summary>
/// <remarks>
/// <para>
/// Outside a repository or without <c>git</c> on <c>PATH</c>, <see cref="GitFacts.GetHead"/>
/// returns <c>null</c> and every git-derived fact stays absent (<c>null</c>) so rules that
/// require git evidence fail closed (<c>undecidable</c>) rather than mistaking an
/// unobservable repository for an empty diff.
/// </para>
/// <para>
/// On a pre-dispatch trigger whose return trigger declares
/// <c>git.changed-paths.since-dispatch</c>, the hook captures a <see cref="GitSnapshot"/>
/// before the subagent runs and records it under the arbiter directory. When the matching
/// post-dispatch return arrives, <see cref="GitFacts.GetChangedPathsSinceSnapshot"/> diffs
/// the working tree and <c>HEAD</c> against that pre-dispatch snapshot, isolating the dispatch's own
/// changes from earlier branch commits and <c>artifacts/**</c> output. When no pre-dispatch
/// snapshot was recorded, the fact stays absent (<c>null</c>) so the rule answers
/// <c>undecidable</c> instead of mistaking an unobservable dispatch for an empty diff.
/// </para>
/// <para>
/// On review triggers declaring <c>review.changed-paths</c>, the hook measures the
/// committed diff plus the dirty set against the run's recorded base (falling back to
/// <see cref="ReviewBase"/> when no run base is recorded) and records that basis in
/// <c>review.base-ref</c>.
/// </para>
/// </remarks>
internal sealed class HookGitFacts(
    string workingDirectory,
    string? homeDirectory = null,
    string? arbiterDirectory = null) : IArbiterGitFacts
{
    /// <summary>
    /// The ref the review's changed set is measured from: <c>origin/HEAD</c>, the
    /// default <c>review gates</c> diffs against, so the lenses judge the same change
    /// the council does. The hook event carries no base of its own.
    /// </summary>
    private const string ReviewBase = "origin/HEAD";

    private const string SnapshotFileName = "snapshots.json";
    private const string SnapshotLockFileName = ".snapshots.lock";
    private const int MaxStoredSnapshots = 64;
    private const int MaxHashedPromptBytes = 16_384;
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromHours(4);

    /// <inheritdoc/>
    public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(ev);

        bool isPreWithReturn = ev.IsDispatch
            && string.Equals(ev.Phase, "pre", StringComparison.OrdinalIgnoreCase)
            && IsDispatchTriggerWithReturn(classification.Trigger);
        bool needsSinceDispatch = facts.Contains("git.changed-paths.since-dispatch");
        bool needsReviewChangedPaths = facts.Contains("review.changed-paths");

        if (!isPreWithReturn && !needsSinceDispatch && !needsReviewChangedPaths)
        {
            return facts;
        }

        string cwd = !string.IsNullOrWhiteSpace(ev.Cwd) ? ev.Cwd : workingDirectory;
        if (isPreWithReturn)
        {
            GitSnapshot? preSnapshot = GitFacts.CaptureSnapshot(cwd);
            if (preSnapshot is not null)
            {
                RecordPreSnapshot(ResolveArbiterDirectory(cwd), classification.Target, ev, preSnapshot);
            }
        }

        if (!needsSinceDispatch && !needsReviewChangedPaths)
        {
            return facts;
        }

        string? head = GitFacts.GetHead(cwd);
        if (head is null)
        {
            return facts;
        }

        if (needsSinceDispatch)
        {
            GitSnapshot? baseline = FindPreSnapshot(ResolveArbiterDirectory(cwd), classification.Target, ev);
            if (baseline is not null)
            {
                IReadOnlyList<string> sinceDispatch = [.. GitFacts.GetChangedPathsSinceSnapshot(cwd, baseline)];
                facts = facts.With("git.changed-paths.since-dispatch", sinceDispatch, ArbiterFactLabel.Derived);
            }
        }

        if (needsReviewChangedPaths)
        {
            string baseRef = ResolveBase(cwd);
            IReadOnlyList<string> changed = [.. GitFacts.GetChangedPathsSinceBase(cwd, baseRef)];
            facts = facts.With("review.changed-paths", changed, ArbiterFactLabel.Derived);
            facts = facts.With("review.base-ref", baseRef, ArbiterFactLabel.Derived);
        }

        return facts;
    }

    private static bool IsDispatchTriggerWithReturn(string? trigger) =>
        string.Equals(trigger, "delegate", StringComparison.Ordinal)
        || string.Equals(trigger, "delegate.planner", StringComparison.Ordinal)
        || string.Equals(trigger, "investigate", StringComparison.Ordinal);

    private string ResolveArbiterDirectory(string cwd) =>
        !string.IsNullOrWhiteSpace(arbiterDirectory)
            ? arbiterDirectory
            : Path.Combine(cwd, "artifacts", "arbiter");

    private static void RecordPreSnapshot(
        string directory,
        string? target,
        ArbiterEvent ev,
        GitSnapshot snapshot)
    {
        // Without a tool-call id or a prompt there is no pairing key: no post can ever
        // match this entry, so storing it only evicts baselines that could still pair.
        if (string.IsNullOrWhiteSpace(ev.ToolCallId) && string.IsNullOrWhiteSpace(ev.Prompt))
        {
            return;
        }

        WithSnapshotStore(directory, entries =>
        {
            entries.RemoveAll(entry => DateTimeOffset.UtcNow - entry.RecordedAt > SnapshotRetention);
            entries.Add(new StoredSnapshot(
                ev.ToolCallId,
                target,
                HashPrompt(ev.Prompt),
                snapshot.Head,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>(snapshot.BlobIds, StringComparer.Ordinal),
                new Dictionary<string, string>(snapshot.Statuses, StringComparer.Ordinal)));
            while (entries.Count > MaxStoredSnapshots)
            {
                entries.RemoveAt(0);
            }

            return true;
        });
    }

    private static GitSnapshot? FindPreSnapshot(
        string directory,
        string? target,
        ArbiterEvent ev)
    {
        // Peek, never consume: a retry carrying the same tool-call id must see the same
        // baseline instead of falling back to a diff the dispatch's commits never appear in.
        // Lock-free: atomic renames mean readers only ever see whole documents, so pairing
        // a recorded baseline never waits on — or blocks — a concurrent record.
        List<StoredSnapshot> entries = ReadSnapshots(Path.Combine(directory, SnapshotFileName));
        int index = FindMatchingIndex(entries, target, ev);
        if (index < 0)
        {
            return null;
        }

        StoredSnapshot entry = entries[index];
        return new GitSnapshot(
            entry.Head,
            entry.BlobIds ?? new Dictionary<string, string>(StringComparer.Ordinal),
            entry.Statuses ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static int FindMatchingIndex(
        List<StoredSnapshot> entries,
        string? target,
        ArbiterEvent ev)
    {
        if (!string.IsNullOrWhiteSpace(ev.ToolCallId))
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(entries[i].CallId, ev.ToolCallId, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(ev.Prompt))
        {
            string promptHash = HashPrompt(ev.Prompt);
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(entries[i].Target, target, StringComparison.Ordinal)
                    && string.Equals(entries[i].PromptHash, promptHash, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        // No target-only fallback: without a tool-call id or a prompt there is no pairing
        // key, and handing a dispatch another dispatch's baseline is a guess. The fact
        // stays absent so the rule stops and asks instead of judging the wrong change.
        return -1;
    }

    // Bounded input: only the first bytes enter the hash, mixed with the total length,
    // so two prompts sharing a prefix but not a length still key differently.
    private static string HashPrompt(string prompt)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(prompt);
        if (bytes.Length <= MaxHashedPromptBytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        byte[] suffix = Encoding.UTF8.GetBytes(":" + bytes.Length.ToString(CultureInfo.InvariantCulture));
        byte[] head = new byte[MaxHashedPromptBytes + suffix.Length];
        Buffer.BlockCopy(bytes, 0, head, 0, MaxHashedPromptBytes);
        Buffer.BlockCopy(suffix, 0, head, MaxHashedPromptBytes, suffix.Length);
        return Convert.ToHexString(SHA256.HashData(head));
    }

    private static void WithSnapshotStore(
        string directory,
        Func<List<StoredSnapshot>, bool> mutate)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string lockPath = Path.Combine(directory, SnapshotLockFileName);
            Stopwatch elapsed = Stopwatch.StartNew();
            TimeSpan backoff = TimeSpan.FromMilliseconds(5);
            while (true)
            {
                FileStream? lockFile = null;
                try
                {
                    lockFile = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (elapsed.Elapsed < TimeSpan.FromMilliseconds(500))
                {
                    Thread.Sleep(backoff);
                    backoff = TimeSpan.FromMilliseconds(Math.Min(50, backoff.TotalMilliseconds * 2));
                    continue;
                }

                using (lockFile)
                {
                    string filePath = Path.Combine(directory, SnapshotFileName);
                    SweepTempFiles(directory);
                    List<StoredSnapshot> entries = ReadSnapshots(filePath);
                    if (mutate(entries))
                    {
                        // Write aside and move into place: a crash mid-write leaves the previous
                        // whole document behind, never a truncated one that reads back as [].
                        string json = JsonSerializer.Serialize(entries);
                        string tempPath = Path.Combine(directory, ".snapshots." + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            File.WriteAllText(tempPath, json);
                            File.Move(tempPath, filePath, overwrite: true);
                        }
                        finally
                        {
                            try
                            {
                                File.Delete(tempPath);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                            }
                        }
                    }

                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Snapshot persistence is best-effort; on failure the fact stays absent so the
            // rule stops and asks instead of judging a diff the dispatch never appeared in.
        }
    }

    private static List<StoredSnapshot> ReadSnapshots(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<StoredSnapshot>>(json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The base the review is measured from: the base the run recorded for the checkout's
    /// branch when that base still resolves, or the default review base otherwise. A stale
    /// or bogus record falls back rather than measuring against a ref that is gone.
    /// </summary>
    private string ResolveBase(string workingDirectory)
    {
        string home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? branch = GitFacts.GetCurrentBranch(workingDirectory);
        if (branch is not null &&
            RunBaseState.TryReadBaseRef(home, branch, out string? recorded) &&
            !string.IsNullOrWhiteSpace(recorded) &&
            GitFacts.RefExists(workingDirectory, recorded))
        {
            return recorded;
        }

        return ReviewBase;
    }

    // Crash debris from an interrupted write-aside: swept on the next locked record,
    // before the fresh document moves into place.
    private static void SweepTempFiles(string directory)
    {
        foreach (string temp in Directory.GetFiles(directory, ".snapshots.*.tmp"))
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// One pre-dispatch snapshot. <see cref="PromptHash"/> carries the SHA-256 hex of the
    /// host prompt, never the prompt itself, so the store holds no host payload. Entries
    /// written before hashing carry a null hash and simply never match; entries older
    /// than the retention window are pruned on the next record.
    /// </summary>
    private sealed record StoredSnapshot(
        string? CallId,
        string? Target,
        string? PromptHash,
        string Head,
        DateTimeOffset RecordedAt,
        Dictionary<string, string>? BlobIds,
        Dictionary<string, string>? Statuses);
}

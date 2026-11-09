using System.Diagnostics;
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
/// the working tree and <c>HEAD</c> against that pre-dispatch snapshot (or against current
/// <c>HEAD</c> when no pre-dispatch snapshot was recorded), isolating the dispatch's own
/// changes from earlier branch commits and <c>artifacts/**</c> output.
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

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.Ordinal);

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
            GitSnapshot baseline = TakePreSnapshot(ResolveArbiterDirectory(cwd), classification.Target, ev)
                ?? new GitSnapshot(head, EmptyMap, EmptyMap);
            IReadOnlyList<string> sinceDispatch = [.. GitFacts.GetChangedPathsSinceSnapshot(cwd, baseline)];
            facts = facts.With("git.changed-paths.since-dispatch", sinceDispatch, ArbiterFactLabel.Derived);
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
        WithSnapshotStore(directory, entries =>
        {
            entries.Add(new StoredSnapshot(
                ev.ToolCallId,
                target,
                ev.Prompt,
                snapshot.Head,
                new Dictionary<string, string>(snapshot.BlobIds, StringComparer.Ordinal),
                new Dictionary<string, string>(snapshot.Statuses, StringComparer.Ordinal)));
            while (entries.Count > MaxStoredSnapshots)
            {
                entries.RemoveAt(0);
            }

            return true;
        });
    }

    private static GitSnapshot? TakePreSnapshot(
        string directory,
        string? target,
        ArbiterEvent ev)
    {
        GitSnapshot? matched = null;
        WithSnapshotStore(directory, entries =>
        {
            int index = FindMatchingIndex(entries, target, ev);
            if (index < 0)
            {
                return false;
            }

            StoredSnapshot entry = entries[index];
            entries.RemoveAt(index);
            matched = new GitSnapshot(
                entry.Head,
                entry.BlobIds ?? EmptyMap,
                entry.Statuses ?? EmptyMap);
            return true;
        });
        return matched;
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
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(entries[i].Target, target, StringComparison.Ordinal)
                    && string.Equals(entries[i].Prompt, ev.Prompt, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (string.Equals(entries[i].Target, target, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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
                    List<StoredSnapshot> entries = ReadSnapshots(filePath);
                    if (mutate(entries))
                    {
                        string json = JsonSerializer.Serialize(entries);
                        File.WriteAllText(filePath, json);
                    }

                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Snapshot persistence is best-effort; on failure the post hook falls back
            // to diffing against current HEAD.
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

    private sealed record StoredSnapshot(
        string? CallId,
        string? Target,
        string? Prompt,
        string Head,
        Dictionary<string, string>? BlobIds,
        Dictionary<string, string>? Statuses);
}

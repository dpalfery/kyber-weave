using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The append mechanics both files share: one complete JSON line per append,
/// written under an exclusive lock on <c>artifacts/arbiter/.lock</c>.
/// </summary>
/// <remarks>
/// The lock is taken with <see cref="FileShare.None"/>, which .NET enforces at
/// the OS level between processes (and between handles), so mutual exclusion
/// holds across hook processes, not just threads. A lock that cannot be taken
/// within the 500 ms budget is an internal error: the caller fails closed
/// (<c>KW-ARB-HOOK-001</c>) rather than writing an unserialized line. Readers,
/// by contrast, take no lock and tolerate a torn final line, because a writer
/// may be mid-line when they read.
/// </remarks>
internal static class ArbiterLogFiles
{
    private const string LockFileName = ".lock";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Appends one line to <paramref name="fileName"/> under the exclusive lock.</summary>
    /// <exception cref="IOException">Thrown when the lock is not acquired within 500 ms of backoff.</exception>
    internal static Task AppendLineAsync(
        string directory,
        string fileName,
        string line,
        CancellationToken cancellationToken) =>
        AppendLineAsync(directory, fileName, (Func<string>)(() => line), cancellationToken);

    /// <summary>
    /// Appends the line <paramref name="lineFactory"/> produces, computed while the
    /// exclusive lock is held.
    /// </summary>
    /// <remarks>
    /// The factory runs under the lock, so a line derived from what is already on disk
    /// cannot be derived twice from the same state. A caller that resolved something
    /// before taking the lock would race every other appender doing the same.
    /// </remarks>
    /// <exception cref="IOException">Thrown when the lock is not acquired within 500 ms of backoff.</exception>
    internal static async Task AppendLineAsync(
        string directory,
        string fileName,
        Func<string> lineFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineFactory);
        Directory.CreateDirectory(directory);
        string lockPath = Path.Combine(directory, LockFileName);
        Stopwatch elapsed = Stopwatch.StartNew();
        TimeSpan backoff = TimeSpan.FromMilliseconds(5);

        while (true)
        {
            FileStream lockFile;
            try
            {
                lockFile = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromMilliseconds(500))
            {
                // Exponential backoff; the retry window stays inside the hook
                // budget, and a lock that is still held when it expires throws
                // rather than writes.
                await Task.Delay(backoff, cancellationToken);
                backoff = TimeSpan.FromMilliseconds(Math.Min(50, backoff.TotalMilliseconds * 2));
                continue;
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Could not acquire the exclusive Arbiter append lock '{lockPath}' within 500 ms: the append fails closed (KW-ARB-HOOK-001).",
                    ex);
            }

            try
            {
                // Resolved under the lock, not before it: a post's pairing decision depends
                // on which pre events are still unpaired, so deciding it outside the lock
                // lets racing posts all claim the same pre and double-book a dispatch.
                string line = lineFactory();
                await using FileStream target = File.Open(
                    Path.Combine(directory, fileName), FileMode.Append, FileAccess.Write, FileShare.Read);
                await using StreamWriter writer = new(target, Utf8NoBom);
                await writer.WriteAsync(line.AsMemory(), cancellationToken);
                await writer.WriteAsync("\n".AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
            finally
            {
                await lockFile.DisposeAsync();
            }

            return;
        }
    }

    /// <summary>
    /// Reads the complete lines of <paramref name="path"/> without taking any
    /// lock, ignoring a final line that has no newline (a writer mid-line).
    /// </summary>
    internal static IReadOnlyList<string> ReadCompleteLines(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        string text = File.ReadAllText(path);
        if (text.Length == 0)
        {
            return [];
        }

        string[] segments = text.Split('\n');
        // A file that ends with '\n' yields a trailing empty segment; a torn
        // final write yields a trailing partial line. Either way the last
        // segment is not a complete record, so readers drop it.
        return [.. segments.Take(segments.Length - 1)];
    }
}

/// <summary>
/// The ledger (<c>artifacts/arbiter/ledger.jsonl</c>, schema
/// <c>kyber-arbiter.ledger/v1</c>): append-only pre, post and unmarked
/// dispatch events, plus the queries the pre- and post-dispatch rules read.
/// </summary>
/// <remarks>
/// The arbiter directory is passed in because the hook resolves it: the
/// repository root comes from <c>git rev-parse --show-toplevel</c> run in the
/// payload's <c>cwd</c>, and the files sit in <c>artifacts/arbiter/</c> under it.
/// </remarks>
public sealed class InFlightLedger
{
    /// <summary>The ledger file name, relative to the arbiter directory.</summary>
    public const string FileName = "ledger.jsonl";

    private readonly string _directory;

    /// <summary>Creates a ledger over <paramref name="arbiterDirectory"/>.</summary>
    public InFlightLedger(string arbiterDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arbiterDirectory);
        _directory = arbiterDirectory;
    }

    /// <summary>The directory holding the ledger, the decision log and the lock.</summary>
    public string DirectoryPath => _directory;

    /// <summary>The ledger file path.</summary>
    public string FilePath => Path.Combine(_directory, FileName);

    /// <summary>The exclusive append lock path.</summary>
    public string LockFilePath => Path.Combine(_directory, ".lock");

    /// <summary>
    /// Appends one event as one complete JSON line under the exclusive lock.
    /// A post event without an explicit <c>pre-id</c> is paired first (by
    /// <c>call-id</c>, else by <c>pair-digest</c>, oldest unpaired pre event
    /// first) and stored with the match.
    /// </summary>
    /// <remarks>
    /// Pairing happens inside the append lock. It reads the ledger, so resolving it
    /// before taking the lock would let two concurrent posts both match the same
    /// unpaired pre and both claim it.
    /// </remarks>
    /// <exception cref="IOException">Thrown when the lock is not acquired within 500 ms (fail closed).</exception>
    public Task AppendAsync(ArbiterLedgerEvent record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        bool needsMatch = record.Phase == ArbiterLedgerPhases.Post && record.PreId is null;
        return ArbiterLogFiles.AppendLineAsync(
            _directory,
            FileName,
            needsMatch
                ? () => ArbiterJson.Serialize(record with { PreId = MatchPre(record) })
                : () => ArbiterJson.Serialize(record),
            cancellationToken);
    }

    /// <summary>Reads every complete event, in append order. Takes no lock.</summary>
    public IReadOnlyList<ArbiterLedgerEvent> ReadAll() => ReadAllWithDiagnostics().Records;

    /// <summary>
    /// Reads every complete event and reports how many lines were malformed. The hook
    /// uses <see cref="ReadAll"/> and skips them; <c>audit</c> uses this overload to
    /// report the corruption instead of silently hiding it.
    /// </summary>
    public (IReadOnlyList<ArbiterLedgerEvent> Records, int MalformedLines) ReadAllWithDiagnostics()
    {
        List<ArbiterLedgerEvent> records = [];
        int malformed = 0;
        foreach (string line in ArbiterLogFiles.ReadCompleteLines(FilePath))
        {
            ArbiterLedgerEvent? record = TryDeserialize(line);
            if (record is null)
            {
                malformed++;
                continue;
            }

            records.Add(record);
        }

        return (records, malformed);
    }

    /// <summary>
    /// A malformed line is skipped rather than thrown: one corrupt append must not
    /// fail pairing and every in-flight query on every call.
    /// </summary>
    private static ArbiterLedgerEvent? TryDeserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ArbiterLedgerEvent>(line, ArbiterJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the pre event a post event pairs with: by <c>call-id</c> where
    /// the payload provides one, otherwise by <c>pair-digest</c>, oldest
    /// unpaired pre event first. A post event that already carries a
    /// <c>pre-id</c> keeps it.
    /// </summary>
    public string? MatchPre(ArbiterLedgerEvent post)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (post.PreId is not null)
        {
            return post.PreId;
        }

        IReadOnlyList<ArbiterLedgerEvent> events = ReadAll();

        // A post's own stored claim must not count against itself, or a
        // re-match of the same event would return null instead of the pre it
        // already paired with. Claims by other posts still reserve the pre.
        HashSet<string> paired = events
            .Where(e => e.Phase == ArbiterLedgerPhases.Post && e.PreId is not null && e.Id != post.Id)
            .Select(e => e.PreId!)
            .ToHashSet(StringComparer.Ordinal);

        if (post.CallId is not null)
        {
            string? byCallId = events
                .Where(e => e.Phase == ArbiterLedgerPhases.Pre
                    && !paired.Contains(e.Id)
                    && string.Equals(e.CallId, post.CallId, StringComparison.Ordinal))
                .Select(e => e.Id)
                .FirstOrDefault();
            if (byCallId is not null)
            {
                return byCallId;
            }
        }

        return post.PairDigest is null
            ? null
            : events
                .Where(e => e.Phase == ArbiterLedgerPhases.Pre
                    && !paired.Contains(e.Id)
                    && string.Equals(e.PairDigest, post.PairDigest, StringComparison.Ordinal))
                .Select(e => e.Id)
                .FirstOrDefault();
    }

    /// <summary>
    /// The task files of the other unpaired <c>delegate</c> pre events — the
    /// in-flight paths a scope rule checks the dispatch against.
    /// </summary>
    /// <param name="excludeEventId">The pre event being gated now, if it is already recorded.</param>
    /// <param name="session">When set, only dispatches from this session count.</param>
    /// <param name="notDispatched">Pre events that were refused, so never ran.</param>
    public IReadOnlyList<string> InFlightPaths(
        string? excludeEventId = null,
        string? session = null,
        IReadOnlySet<string>? notDispatched = null)
    {
        IReadOnlyList<ArbiterLedgerEvent> events = ReadAll();
        HashSet<string> paired = events
            .Where(e => e.Phase == ArbiterLedgerPhases.Post && e.PreId is not null)
            .Select(e => e.PreId!)
            .ToHashSet(StringComparer.Ordinal);
        return [.. events
            .Where(e => e.Phase == ArbiterLedgerPhases.Pre
                && e.Id != excludeEventId
                && !paired.Contains(e.Id)
                && notDispatched?.Contains(e.Id) != true
                && (session is null || string.Equals(e.Session, session, StringComparison.Ordinal))
                && string.Equals(e.Trigger, "delegate", StringComparison.Ordinal))
            .SelectMany(e => e.TaskFiles ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The task files of the dispatches that were in flight at any moment
    /// between <paramref name="preId"/> and <paramref name="postId"/>: every
    /// other dispatch whose interval overlaps this one, including a dispatch
    /// still unpaired (no return yet) that started before this return.
    /// </summary>
    public IReadOnlyList<string> ConcurrentPaths(string preId, string postId)
    {
        ArgumentNullException.ThrowIfNull(preId);
        ArgumentNullException.ThrowIfNull(postId);
        IReadOnlyList<ArbiterLedgerEvent> events = ReadAll();
        ArbiterLedgerEvent pre = events.FirstOrDefault(e => e.Id == preId)
            ?? throw new ArgumentException($"No ledger event '{preId}' exists.", nameof(preId));
        ArbiterLedgerEvent post = events.FirstOrDefault(e => e.Id == postId)
            ?? throw new ArgumentException($"No ledger event '{postId}' exists.", nameof(postId));

        Dictionary<string, ArbiterLedgerEvent> returnsById = events
            .Where(e => e.Phase == ArbiterLedgerPhases.Post && e.PreId is not null)
            .GroupBy(e => e.PreId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return [.. events
            .Where(e => e.Phase == ArbiterLedgerPhases.Pre
                && e.Id != preId
                && e.TaskFiles is { Count: > 0 })
            .Where(e => Overlaps(e, returnsById.GetValueOrDefault(e.Id), pre.At, post.At))
            .SelectMany(e => e.TaskFiles ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The completed tasks: a returned worker output whose
    /// <c>^RESULT:\s+(PASS|FAIL)\b</c> marker read <c>PASS</c>, or an
    /// attestation <c>&lt;task&gt;=complete</c> (D31).
    /// </summary>
    /// <param name="planFile">When set, only returns for this plan count.</param>
    public IReadOnlyList<string> CompletedTasks(string? planFile = null) =>
        [.. ReturnsFor(planFile)
            .SelectMany(item => CompletedTasksOf(item.Post, item.Task))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(task => task, StringComparer.Ordinal)];

    /// <summary>
    /// The tasks with RED evidence: a returned <c>test-dev</c> output whose
    /// <c>RED_EVIDENCE:</c> value states evidence, or an
    /// attestation <c>&lt;task&gt;=red</c> (D31).
    /// </summary>
    /// <param name="planFile">When set, only returns for this plan count.</param>
    public IReadOnlyList<string> RedEvidenceTasks(string? planFile = null) =>
        [.. ReturnsFor(planFile)
            .SelectMany(item => RedEvidenceTasksOf(item.Post, item.Task))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(task => task, StringComparer.Ordinal)];

    private static bool Overlaps(
        ArbiterLedgerEvent otherPre,
        ArbiterLedgerEvent? otherPost,
        DateTimeOffset start,
        DateTimeOffset end) =>
        otherPost is null
            ? otherPre.At <= end
            : otherPre.At <= end && start <= otherPost.At;

    /// <summary>
    /// Each post event with the task it answers. A specialist's headers are stripped
    /// before it runs, so its post event names no task and the paired pre event supplies it.
    /// </summary>
    private List<(ArbiterLedgerEvent Post, string? Task)> ReturnsFor(string? planFile)
    {
        IReadOnlyList<ArbiterLedgerEvent> events = ReadAll();
        Dictionary<string, ArbiterLedgerEvent> pres = events
            .Where(e => e.Phase == ArbiterLedgerPhases.Pre)
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        List<(ArbiterLedgerEvent Post, string? Task)> returns = [];
        foreach (ArbiterLedgerEvent post in events.Where(e => e.Phase == ArbiterLedgerPhases.Post))
        {
            ArbiterLedgerHeaders? headers = post.Headers;
            if (headers is null && post.PreId is not null && pres.TryGetValue(post.PreId, out ArbiterLedgerEvent? pre))
            {
                headers = pre.Headers;
            }

            if (planFile is null || (headers?.PlanFile is { } named && SamePlan(named, planFile)))
            {
                returns.Add((post, headers?.Task));
            }
        }

        return returns;
    }

    private static bool SamePlan(string left, string right) =>
        string.Equals(NormalizePlan(left), NormalizePlan(right), StringComparison.Ordinal);

    private static string NormalizePlan(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static IEnumerable<string> CompletedTasksOf(ArbiterLedgerEvent e, string? task)
    {
        List<string> tasks = [];
        if (task is not null && string.Equals(e.Returns?.TaskReview, "PASS", StringComparison.Ordinal))
        {
            tasks.Add(task);
        }

        tasks.AddRange(AttestedTasks(e, "complete"));
        return tasks;
    }

    private static IEnumerable<string> RedEvidenceTasksOf(ArbiterLedgerEvent e, string? task)
    {
        List<string> tasks = [];
        string? red = e.Returns?.RedEvidence;
        if (task is not null
            && red is not null
            && !NoRedEvidence.IsStatedBy(red))
        {
            tasks.Add(task);
        }

        tasks.AddRange(AttestedTasks(e, "red"));
        return tasks;
    }

    private static IEnumerable<string> AttestedTasks(ArbiterLedgerEvent e, string value)
    {
        foreach (string attested in e.Returns?.Attested ?? [])
        {
            int separator = attested.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0
                && string.Equals(attested[(separator + 1)..].Trim(), value, StringComparison.Ordinal)
                && attested[..separator].Trim() is { Length: > 0 } task)
            {
                yield return task;
            }
        }
    }
}

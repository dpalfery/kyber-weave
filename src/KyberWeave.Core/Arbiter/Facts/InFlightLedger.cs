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
    /// The most bytes one read of an append-only Arbiter log looks at, taken from the end. A log is never swept, so
    /// reading all of it would make every hook call cost more as the log ages; the newest records are the ones the
    /// in-flight and decision queries need.
    /// </summary>
    internal const long MaxReadBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Reads the complete lines of <paramref name="path"/>, looking only at its last <see cref="MaxReadBytes"/> bytes,
    /// without taking any lock, and ignoring a final line that has no newline (a writer mid-line). A log that is
    /// absent or cannot be read reads as empty; <see cref="ReadComplete"/> says which, and whether the window cut.
    /// </summary>
    internal static IReadOnlyList<string> ReadCompleteLines(string path) => ReadComplete(path).Lines;

    /// <summary>
    /// <see cref="ReadCompleteLines"/> with what it left out: whether the window cut older lines off, and why a
    /// log that exists could not be read.
    /// </summary>
    internal static LogTail ReadComplete(string path) =>
        ReadTail(path, MaxReadBytes, keepUnterminatedFinalLine: false);

    /// <summary>
    /// Reads the lines inside the last <paramref name="tailBytes"/> bytes of <paramref name="path"/>. A line the
    /// window cuts the start of is dropped, so a whole line is never read as a fragment. A file that is absent or
    /// cannot be opened or read (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>) reads as
    /// empty: a log that cannot be read has no records to offer, and must not crash the caller.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="tailBytes">The window, at least 1.</param>
    /// <param name="keepUnterminatedFinalLine">Whether a final line with no newline is returned.</param>
    internal static IReadOnlyList<string> ReadTailLines(string path, long tailBytes, bool keepUnterminatedFinalLine) =>
        ReadTail(path, tailBytes, keepUnterminatedFinalLine).Lines;

    /// <summary>
    /// <see cref="ReadTailLines(string, long, bool)"/> with what it left out. An absent file is an empty log with no
    /// problem; any other open or read failure is an empty log with <see cref="LogTail.Unreadable"/> set, so a
    /// caller that can report it does not mistake an unreadable log for an empty one.
    /// </summary>
    internal static LogTail ReadTail(string path, long tailBytes, bool keepUnterminatedFinalLine)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tailBytes, 1);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadTail(stream, tailBytes, keepUnterminatedFinalLine);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new LogTail([], Truncated: false, Unreadable: null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new LogTail([], Truncated: false, Unreadable: $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// The stream form of <see cref="ReadTailLines(string, long, bool)"/>. It catches nothing: an
    /// <see cref="IOException"/> from the stream propagates, and only the path overloads turn it into an empty read.
    /// </summary>
    internal static IReadOnlyList<string> ReadTailLines(Stream stream, long tailBytes, bool keepUnterminatedFinalLine) =>
        ReadTail(stream, tailBytes, keepUnterminatedFinalLine).Lines;

    /// <summary>The stream form of <see cref="ReadTail(string, long, bool)"/>; it catches nothing.</summary>
    /// <remarks>
    /// The window is read in fixed chunks and cut into lines as it goes, so the memory held is one chunk, the line
    /// being assembled and the lines kept, not a buffer, a string and a split of the whole window.
    /// </remarks>
    internal static LogTail ReadTail(Stream stream, long tailBytes, bool keepUnterminatedFinalLine)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(tailBytes, 1);

        // Length is read once: a second read of a file that is being appended to, or truncated, could disagree with
        // the first and yield a negative seek or a window of the wrong size.
        long length = stream.Length;
        long window = Math.Min(length, tailBytes);
        bool truncated = length > tailBytes;
        bool startsMidLine = false;
        if (truncated)
        {
            stream.Seek(length - tailBytes - 1, SeekOrigin.Begin);
            startsMidLine = stream.ReadByte() != '\n';
        }

        List<string> lines = [];
        byte[] chunk = new byte[Math.Min(window, ChunkBytes)];
        using MemoryStream pending = new();
        long remaining = window;
        int segment = 0;
        while (remaining > 0)
        {
            int read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
            if (read == 0)
            {
                break;
            }

            remaining -= read;
            int start = 0;
            while (start < read)
            {
                int newline = Array.IndexOf(chunk, (byte)'\n', start, read - start);
                if (newline < 0)
                {
                    pending.Write(chunk, start, read - start);
                    break;
                }

                pending.Write(chunk, start, newline - start);
                AddSegment(lines, pending, segment++, startsMidLine);
                start = newline + 1;
            }
        }

        // What is left has no newline after it: a writer mid-line or a torn write. The first segment is a fragment
        // when the window opened mid-line (and possibly mid-character), and is never kept.
        if (pending.Length > 0 && keepUnterminatedFinalLine)
        {
            AddSegment(lines, pending, segment, startsMidLine);
        }

        return new LogTail(lines, truncated, Unreadable: null);
    }

    private const int ChunkBytes = 64 * 1024;

    private static void AddSegment(List<string> lines, MemoryStream segmentBytes, int index, bool startsMidLine)
    {
        if (index > 0 || !startsMidLine)
        {
            lines.Add(Encoding.UTF8.GetString(segmentBytes.GetBuffer(), 0, (int)segmentBytes.Length).TrimEnd('\r'));
        }

        segmentBytes.SetLength(0);
    }
}

/// <summary>What one windowed read of an append-only log found, and what it could not show.</summary>
/// <param name="Lines">The whole lines in the window, in file order.</param>
/// <param name="Truncated">Whether the log was longer than the window, so older lines were not read.</param>
/// <param name="Unreadable">Why a log that exists could not be read; null when it was read or is absent.</param>
internal readonly record struct LogTail(IReadOnlyList<string> Lines, bool Truncated, string? Unreadable);

/// <summary>
/// The records of an append-only Arbiter log with what the read could not show: lines that were not a record, a window
/// that cut older records off, and a log that exists but could not be read.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <param name="Records">The records in the window, in append order.</param>
/// <param name="MalformedLines">How many whole lines were not a record.</param>
/// <param name="Truncated">Whether the log was longer than the read window, so older records were not read.</param>
/// <param name="Unreadable">Why a log that exists could not be read; null otherwise.</param>
public sealed record ArbiterLogRead<T>(
    IReadOnlyList<T> Records,
    int MalformedLines,
    bool Truncated,
    string? Unreadable);

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

    /// <summary>Reads every complete event in the newest <see cref="ArbiterLogFiles.MaxReadBytes"/> bytes, in append order. Takes no lock.</summary>
    public IReadOnlyList<ArbiterLedgerEvent> ReadAll() => ReadAllWithDiagnostics().Records;

    /// <summary>
    /// Reads every complete event in the newest <see cref="ArbiterLogFiles.MaxReadBytes"/> bytes and reports what
    /// the read could not show: how many lines were malformed, whether older events fell outside the window, and
    /// why a log that exists could not be read. The hook uses <see cref="ReadAll"/> and stays quiet about all three;
    /// <c>audit</c> and <c>tune</c> use this overload to report them instead of presenting a partial log as whole.
    /// </summary>
    public ArbiterLogRead<ArbiterLedgerEvent> ReadAllWithDiagnostics()
    {
        List<ArbiterLedgerEvent> records = [];
        int malformed = 0;
        LogTail tail = ArbiterLogFiles.ReadComplete(FilePath);
        foreach (string line in tail.Lines)
        {
            ArbiterLedgerEvent? record = TryDeserialize(line);
            if (record is null)
            {
                malformed++;
                continue;
            }

            records.Add(record);
        }

        return new ArbiterLogRead<ArbiterLedgerEvent>(records, malformed, tail.Truncated, tail.Unreadable);
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
    /// The writer dispatches (<c>delegate</c> and <c>delegate.planner</c>) that were in flight at any moment
    /// between <paramref name="preId"/> and <paramref name="postId"/>, other than that dispatch itself: their
    /// declared task files, and whether any of them declared none, so the paths it wrote cannot be told apart.
    /// </summary>
    /// <remarks>
    /// A dispatch with no return yet overlaps from its start, unless it started more than
    /// <see cref="UnreturnedDispatchHorizon"/> before this return: a harness that crashed never reports it, and it
    /// must not hold a window open forever. Read-only dispatches write nothing, so they are not counted. A return
    /// that paired with no pre event has no known start, so its window is taken as the whole horizon.
    /// </remarks>
    /// <param name="preId">The pre event of the dispatch that returned, or null when the return paired with none.</param>
    /// <param name="postId">Its return.</param>
    /// <param name="notDispatched">Pre events that were refused, so never ran.</param>
    /// <returns>The overlap, or null when either event is not in the ledger.</returns>
    public ConcurrentDispatches? ConcurrentWriters(string? preId, string postId, IReadOnlySet<string>? notDispatched = null)
    {
        ArgumentNullException.ThrowIfNull(postId);
        IReadOnlyList<ArbiterLedgerEvent> events = ReadAll();
        ArbiterLedgerEvent? post = events.FirstOrDefault(e => e.Id == postId);
        ArbiterLedgerEvent? pre = preId is null ? null : events.FirstOrDefault(e => e.Id == preId);
        if (post is null || (preId is not null && pre is null))
        {
            return null;
        }

        DateTimeOffset start = pre?.At ?? post.At - UnreturnedDispatchHorizon;

        Dictionary<string, ArbiterLedgerEvent> returnsById = events
            .Where(e => e.Phase == ArbiterLedgerPhases.Post && e.PreId is not null)
            .GroupBy(e => e.PreId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        List<ArbiterLedgerEvent> overlapping = [.. events
            .Where(e => e.Phase == ArbiterLedgerPhases.Pre
                && e.Id != preId
                && WriterTriggers.Contains(e.Trigger ?? string.Empty)
                && notDispatched?.Contains(e.Id) != true)
            .Where(e => returnsById.GetValueOrDefault(e.Id) is { } otherPost
                ? e.At <= post.At && start <= otherPost.At
                : e.At <= post.At && post.At - e.At <= UnreturnedDispatchHorizon)];

        return new ConcurrentDispatches(
            [.. overlapping
                .SelectMany(e => e.TaskFiles ?? [])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)],
            overlapping.Any(e => e.TaskFiles is not { Count: > 0 }));
    }

    /// <summary>How long a dispatch with no return still counts as in flight for <see cref="ConcurrentWriters"/>.</summary>
    public static TimeSpan UnreturnedDispatchHorizon { get; } = TimeSpan.FromHours(4);

    private static readonly HashSet<string> WriterTriggers = new(StringComparer.Ordinal) { "delegate", "delegate.planner" };

    /// <summary>
    /// The headers of the dispatch a return answers: the return's own, or else its paired pre event's, because a
    /// specialist's headers are stripped before it runs. Null when the event is unknown or names none.
    /// </summary>
    public ArbiterLedgerHeaders? DispatchHeadersFor(string postId)
    {
        ArgumentNullException.ThrowIfNull(postId);
        IReadOnlyList<ArbiterLedgerEvent> events = ReadAll();
        ArbiterLedgerEvent? post = events.FirstOrDefault(e => string.Equals(e.Id, postId, StringComparison.Ordinal));
        if (post is null)
        {
            return null;
        }

        if (post.Headers is not null || post.PreId is null)
        {
            return post.Headers;
        }

        return events.FirstOrDefault(e => string.Equals(e.Id, post.PreId, StringComparison.Ordinal))?.Headers;
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

/// <summary>The writer dispatches that overlapped one dispatch's window.</summary>
/// <param name="Paths">Their declared task files.</param>
/// <param name="AnyWithoutFiles">Whether any of them declared no task files, so what it wrote is unknown.</param>
public sealed record ConcurrentDispatches(IReadOnlyList<string> Paths, bool AnyWithoutFiles);

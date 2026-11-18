using KyberWeave.Contracts.Repositories;
using System.Text.Json;

namespace KyberWeave.Core.Arbiter.Facts;

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
public sealed class InFlightLedger : IArbiterLedger
{
    /// <summary>The ledger file name, relative to the arbiter directory.</summary>
    public const string FileName = "ledger.jsonl";

    private readonly string _directory;
    private readonly IArbiterLogStore _log;

    /// <summary>Creates a ledger over <paramref name="arbiterDirectory"/>.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory the ledger file sits in.</param>
    /// <param name="log">The append and windowed-read mechanics the ledger shares with the decision log.</param>
    public InFlightLedger(string arbiterDirectory, IArbiterLogStore log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arbiterDirectory);
        ArgumentNullException.ThrowIfNull(log);
        _directory = arbiterDirectory;
        _log = log;
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
        return _log.AppendLineAsync(
            _directory,
            FileName,
            needsMatch
                ? () => ArbiterJson.Serialize(record with { PreId = MatchPre(record) })
                : () => ArbiterJson.Serialize(record),
            cancellationToken);
    }

    /// <summary>Reads every complete event in the newest bytes, in append order. Takes no lock.</summary>
    public IReadOnlyList<ArbiterLedgerEvent> ReadAll() => ReadAllWithDiagnostics().Records;

    /// <summary>
    /// Reads every complete event in the newest bytes and reports what
    /// the read could not show: how many lines were malformed, whether older events fell outside the window, and
    /// why a log that exists could not be read. The hook uses <see cref="ReadAll"/> and stays quiet about all three;
    /// <c>audit</c> and <c>tune</c> use this overload to report them instead of presenting a partial log as whole.
    /// </summary>
    public ArbiterLogRead<ArbiterLedgerEvent> ReadAllWithDiagnostics()
    {
        List<ArbiterLedgerEvent> records = [];
        int malformed = 0;
        LogTail tail = _log.ReadComplete(FilePath);
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
        return post.PreId ?? ArbiterLedgerQueries.MatchPre(ReadAll(), post);
    }

    /// <summary>
    /// The task files of the other unpaired <c>delegate</c> pre events: the
    /// in-flight paths a scope rule checks the dispatch against.
    /// </summary>
    /// <param name="excludeEventId">The pre event being gated now, if it is already recorded.</param>
    /// <param name="session">When set, only dispatches from this session count.</param>
    /// <param name="notDispatched">Pre events that were refused, so never ran.</param>
    /// <param name="run">When set, only dispatches of this run count.</param>
    public IReadOnlyList<string> InFlightPaths(
        string? excludeEventId = null,
        string? session = null,
        IReadOnlySet<string>? notDispatched = null,
        ArbiterRunScope? run = null) =>
        ArbiterLedgerQueries.InFlightPaths(ReadAll(), excludeEventId, session, notDispatched, run);

    /// <summary>
    /// The writer dispatches that were in flight at any moment between <paramref name="preId"/> and
    /// <paramref name="postId"/>, other than that dispatch itself. See <see cref="ArbiterLedgerQueries.ConcurrentWriters"/>.
    /// </summary>
    /// <param name="preId">The pre event of the dispatch that returned, or null when the return paired with none.</param>
    /// <param name="postId">Its return.</param>
    /// <param name="notDispatched">Pre events that were refused, so never ran.</param>
    /// <returns>The overlap, or null when either event is not in the ledger.</returns>
    public ConcurrentDispatches? ConcurrentWriters(string? preId, string postId, IReadOnlySet<string>? notDispatched = null)
    {
        ArgumentNullException.ThrowIfNull(postId);
        return ArbiterLedgerQueries.ConcurrentWriters(ReadAll(), preId, postId, notDispatched);
    }

    /// <summary>How long a dispatch with no return still counts as in flight for <see cref="ConcurrentWriters"/>.</summary>
    public static TimeSpan UnreturnedDispatchHorizon => ArbiterLedgerQueries.UnreturnedDispatchHorizon;

    /// <summary>
    /// The headers of the dispatch a return answers: the return's own, or else its paired pre event's, because a
    /// specialist's headers are stripped before it runs. Null when the event is unknown or names none.
    /// </summary>
    public ArbiterLedgerHeaders? DispatchHeadersFor(string postId)
    {
        ArgumentNullException.ThrowIfNull(postId);
        return ArbiterLedgerQueries.DispatchHeadersFor(ReadAll(), postId);
    }

    /// <summary>
    /// The completed tasks: a returned worker output whose <c>^RESULT:\s+(PASS|FAIL)\b</c> marker read <c>PASS</c>,
    /// or an attestation <c>&lt;task&gt;=complete</c> (D31).
    /// </summary>
    /// <param name="planFile">When set, only returns for this plan count.</param>
    /// <param name="run">When set, only returns of this run's dispatches count.</param>
    public IReadOnlyList<string> CompletedTasks(string? planFile = null, ArbiterRunScope? run = null) =>
        ArbiterLedgerQueries.CompletedTasks(ReadAll(), planFile, run);
}

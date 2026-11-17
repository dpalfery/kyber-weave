using System.Security.Cryptography;
using System.Text;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The questions the rules put to the ledger, answered over the events a read returned: which pre event a return
/// pairs with, which dispatches are in flight, which overlapped a return's window, and which tasks completed.
/// </summary>
public static class ArbiterLedgerQueries
{
    /// <summary>
    /// Resolves the pre event a post event pairs with: by <c>call-id</c> where
    /// the payload provides one, otherwise by <c>pair-digest</c>, oldest
    /// unpaired pre event first. A post event that already carries a
    /// <c>pre-id</c> keeps it.
    /// </summary>
    public static string? MatchPre(IReadOnlyList<ArbiterLedgerEvent> events, ArbiterLedgerEvent post)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (post.PreId is not null)
        {
            return post.PreId;
        }

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
    /// <remarks>
    /// A dispatch whose return never arrives (a harness that crashed, or a background agent whose turn-end event
    /// was not delivered) stops holding its files <see cref="UnreturnedDispatchHorizon"/> after it started,
    /// measured against the dispatch being gated, or the newest event when that one is not recorded.
    /// <para>
    /// An earlier attempt at the same plan task is not in flight for that task's next attempt. A pre event is
    /// written for every evaluation, and a conductor can be allowed a dispatch and then spend the turn elsewhere
    /// without running the worker; its own task's files then intersected its retry and the retry answered
    /// <c>overlaps-in-flight</c> forever, which is how a three-task run lost one task to three refusals.
    /// </para>
    /// </remarks>
    /// <param name="events">The events a ledger read returned, in append order.</param>
    /// <param name="excludeEventId">The pre event being gated now, if it is already recorded.</param>
    /// <param name="session">When set, only dispatches from this session count.</param>
    /// <param name="notDispatched">Pre events that were refused, so never ran.</param>
    /// <param name="run">When set, only dispatches of this run count.</param>
    public static IReadOnlyList<string> InFlightPaths(
        IReadOnlyList<ArbiterLedgerEvent> events,
        string? excludeEventId = null,
        string? session = null,
        IReadOnlySet<string>? notDispatched = null,
        ArbiterRunScope? run = null)
    {
        HashSet<string> paired = events
            .Where(e => e.Phase == ArbiterLedgerPhases.Post && e.PreId is not null)
            .Select(e => e.PreId!)
            .ToHashSet(StringComparer.Ordinal);
        ArbiterLedgerEvent? gated = excludeEventId is null
            ? null
            : events.FirstOrDefault(e => e.Id == excludeEventId);
        string? gatedTask = gated?.Headers?.Task;
        string? gatedPlan = gated?.Headers?.PlanFile;
        DateTimeOffset? now = (gated ?? (events.Count > 0 ? events[^1] : null))?.At;
        return [.. events
            .Where(e => e.Phase == ArbiterLedgerPhases.Pre
                && e.Id != excludeEventId
                && !paired.Contains(e.Id)
                && !SameTask(e, gatedTask, gatedPlan)
                && (now is null || now.Value - e.At <= UnreturnedDispatchHorizon)
                && notDispatched?.Contains(e.Id) != true
                && (session is null || string.Equals(e.Session, session, StringComparison.Ordinal))
                && run?.Includes(e) != false
                && string.Equals(e.Trigger, "delegate", StringComparison.Ordinal))
            .SelectMany(e => e.TaskFiles ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether <paramref name="e"/> is another attempt at the same plan task the gated dispatch names: same task id,
    /// and the same plan file (or both naming none).
    /// </summary>
    private static bool SameTask(ArbiterLedgerEvent e, string? task, string? planFile)
    {
        if (string.IsNullOrWhiteSpace(task)
            || !string.Equals(e.Headers?.Task, task, StringComparison.Ordinal))
        {
            return false;
        }

        string? otherPlan = e.Headers?.PlanFile;
        return string.IsNullOrWhiteSpace(planFile) && string.IsNullOrWhiteSpace(otherPlan)
            || string.Equals(otherPlan, planFile, StringComparison.OrdinalIgnoreCase);
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
    /// <param name="events">The events a ledger read returned, in append order.</param>
    /// <param name="preId">The pre event of the dispatch that returned, or null when the return paired with none.</param>
    /// <param name="postId">Its return.</param>
    /// <param name="notDispatched">Pre events that were refused, so never ran.</param>
    /// <returns>The overlap, or null when either event is not in the ledger.</returns>
    public static ConcurrentDispatches? ConcurrentWriters(IReadOnlyList<ArbiterLedgerEvent> events, string? preId, string postId, IReadOnlySet<string>? notDispatched = null)
    {
        ArgumentNullException.ThrowIfNull(postId);
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
    public static ArbiterLedgerHeaders? DispatchHeadersFor(IReadOnlyList<ArbiterLedgerEvent> events, string postId)
    {
        ArgumentNullException.ThrowIfNull(postId);
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
    /// The technology a read-only agent's dispatch names, read from its unreturned pre events in one session.
    /// </summary>
    /// <remarks>
    /// A tool call names neither the task nor the technology it belongs to, so the dispatch it answers is the one
    /// pre event for <paramref name="target"/> in <paramref name="session"/> that no return has claimed. Parallel
    /// dispatches of the same agent cannot be told apart from the call, and a dispatch that names no technology
    /// names none for this purpose: both answer null, which the caller reads as "apply every technology's rules"
    /// so a refusal is never lost.
    /// </remarks>
    /// <param name="events">The events a ledger read returned, in append order.</param>
    /// <param name="target">The calling agent's name, as the event's dispatch target.</param>
    /// <param name="session">The harness session the call belongs to; null names no session and answers null.</param>
    public static string? DispatchTechnology(IReadOnlyList<ArbiterLedgerEvent> events, string target, string? session)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (string.IsNullOrWhiteSpace(session))
        {
            return null;
        }

        HashSet<string> returned = events
            .Where(e => e.Phase == ArbiterLedgerPhases.Post && e.PreId is not null)
            .Select(e => e.PreId!)
            .ToHashSet(StringComparer.Ordinal);
        List<ArbiterLedgerEvent> candidates = [.. events
            .Where(e => e.Phase == ArbiterLedgerPhases.Pre
                && !returned.Contains(e.Id)
                && string.Equals(e.Target, target, StringComparison.Ordinal)
                && string.Equals(e.Session, session, StringComparison.Ordinal))];
        if (candidates.Count == 0)
        {
            return null;
        }

        string[] technologies = [.. candidates
            .Select(e => e.Headers?.Technology?.Trim())
            .OfType<string>()
            .Where(technology => technology.Length > 0)
            .Distinct(StringComparer.Ordinal)];
        return technologies.Length == 1 && candidates.All(e => !string.IsNullOrWhiteSpace(e.Headers?.Technology))
            ? technologies[0]
            : null;
    }

    /// <summary>
    /// The completed tasks: a returned worker output whose
    /// <c>^RESULT:\s+(PASS|FAIL)\b</c> marker read <c>PASS</c>, a worker digest whose
    /// <c>TASK_GATE:</c> line read <c>PASS</c> under a finished <c>STATUS:</c>, or an
    /// attestation <c>&lt;task&gt;=complete</c> (D31).
    /// </summary>
    /// <param name="events">The events a ledger read returned, in append order.</param>
    /// <param name="planFile">When set, only returns for this plan count.</param>
    /// <param name="run">When set, only returns of this run's dispatches count.</param>
    public static IReadOnlyList<string> CompletedTasks(
        IReadOnlyList<ArbiterLedgerEvent> events, string? planFile = null, ArbiterRunScope? run = null) =>
        [.. ReturnsFor(events, planFile, run)
            .SelectMany(item => CompletedTasksOf(item.Post, item.Task))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(task => task, StringComparer.Ordinal)];

    /// <summary>
    /// Each post event with the task it answers. A specialist's headers are stripped
    /// before it runs, so its post event names no task and the paired pre event supplies it.
    /// </summary>
    private static List<(ArbiterLedgerEvent Post, string? Task)> ReturnsFor(
        IReadOnlyList<ArbiterLedgerEvent> events, string? planFile, ArbiterRunScope? run)
    {
        Dictionary<string, ArbiterLedgerEvent> pres = events
            .Where(e => e.Phase == ArbiterLedgerPhases.Pre)
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        List<(ArbiterLedgerEvent Post, string? Task)> returns = [];
        foreach (ArbiterLedgerEvent post in events.Where(e => e.Phase == ArbiterLedgerPhases.Post))
        {
            ArbiterLedgerHeaders? headers = post.Headers;
            ArbiterLedgerEvent? pre = post.PreId is not null && pres.TryGetValue(post.PreId, out ArbiterLedgerEvent? found) ? found : null;
            if (headers is null && pre is not null)
            {
                headers = pre.Headers;
            }

            // A return belongs to the run of the dispatch it answers; one that paired with none is placed by itself.
            if (run?.Includes(pre ?? post) == false)
            {
                continue;
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
        if (task is not null
            && (string.Equals(e.Returns?.TaskReview, "PASS", StringComparison.Ordinal) || IsPassingDigest(e.Returns)))
        {
            tasks.Add(task);
        }

        tasks.AddRange(AttestedTasks(e, "complete"));
        return tasks;
    }

    private static readonly HashSet<string> FinishedStatuses = new(StringComparer.Ordinal) { "PASS", "READY_FOR_REVIEW" };

    /// <summary>
    /// Whether a worker's completion digest reports a finished task: its own task gate passed and its status is
    /// one a finished worker writes. A <c>RESULT: FAIL</c> beside it still fails the report.
    /// </summary>
    private static bool IsPassingDigest(ArbiterReturnMarkers? returns) =>
        returns is not null
        && string.Equals(returns.TaskGate, "PASS", StringComparison.Ordinal)
        && !string.Equals(returns.TaskReview, "FAIL", StringComparison.Ordinal)
        && FinishedStatuses.Contains(returns.PlannerStatus ?? string.Empty);

    /// <summary>
    /// The digest that pairs a return with its dispatch when the harness has no call id: the target and the prompt
    /// as finally executed, which is the prompt after its headers are stripped. Null when either is missing.
    /// </summary>
    /// <param name="target">The dispatch target.</param>
    /// <param name="prompt">The dispatch prompt, with or without its header block.</param>
    public static string? PairDigest(string? target, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        string executed = HeaderBlock.Strip(prompt).Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target + "\n" + executed)));
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

using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using System.Text.Json;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;

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
/// require git evidence answer <c>undecidable</c>, which allows and is logged, rather than
/// mistaking an unobservable repository for an empty diff.
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
/// <c>review.base-ref</c>. <c>review.diff-summary</c> is measured from the same base.
/// </para>
/// <para>
/// On a refutation, <c>file.surroundings</c> is read from the working tree around the line
/// the finding names. The path comes from the dispatch prompt, so it must resolve inside
/// the repository, and a finding that cannot be located leaves the fact absent.
/// </para>
/// </remarks>
internal sealed class HookGitFacts(
    IGitRepositoryFacts git,
    IPlanFileResolver planPaths,
    IHookSnapshotStore snapshots,
    IHookFileWindowReader windows,
    IRunCheckout checkouts,
    IRunStateStore runStates,
    IProcessEnvironment process,
    IPathResolver paths,
    TimeProvider clock,
    string workingDirectory,
    string? homeDirectory = null,
    string? arbiterDirectory = null) : IArbiterGitFacts, IArbiterRunScopeSource
{
    /// <inheritdoc/>
    public ArbiterRunScope? RunScopeOf(string? directory)
    {
        string cwd = !string.IsNullOrWhiteSpace(directory) ? directory : workingDirectory;
        string home = homeDirectory ?? process.HomeDirectory;
        if (string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        // The newest record wins: a run that reuses a directory replaces the run that worked there before.
        string wanted = KyberWeave.Core.RunStart.RunBaseStateFormat.NormalizeDirectory(cwd, paths, process);
        KyberWeave.Core.RunStart.RunRecord? record = runStates.ReadRecords(home)?
            .Where(candidate => string.Equals(
                KyberWeave.Core.RunStart.RunBaseStateFormat.NormalizeDirectory(candidate.Worktree, paths, process),
                wanted,
                KyberWeave.Core.RunStart.RunBaseStateFormat.PathComparison(process)))
            .OrderBy(candidate => candidate.StartedAt ?? DateTimeOffset.MinValue)
            .LastOrDefault();
        return record is null ? null : new ArbiterRunScope(cwd, record.StartedAt);
    }

    /// <summary>
    /// The ref the review's changed set is measured from: <c>origin/HEAD</c>, the
    /// default <c>review gates</c> diffs against, so the lenses judge the same change
    /// the council does. The hook event carries no base of its own.
    /// </summary>
    private const string ReviewBase = "origin/HEAD";

    private const string ChangedHunksFact = "review.changed-hunks";
    private const string RenameOnlyFact = "review.rename-only-paths";
    private const string CommentOnlyFact = "review.comment-only-paths";
    private const string NoCodeChangeFact = "review.no-code-change-paths";
    private const string FindingChangedLinesFact = "finding.changed-lines";
    private const string RunStartedFact = "run.started";

    private const string StrayPathsFact = "checkout.stray-paths";
    private const string SnapshotFileName = "snapshots.json";
    private const string CanonicalSnapshotFileName = "canonical-snapshots.json";
    private const int SurroundingLines = 20;
    private const long MaxSurroundingsFileBytes = 1L << 24;
    private static readonly TimeSpan PeekRetryBudget = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PeekPause = TimeSpan.FromMilliseconds(20);

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
        bool needsChangeSet = NeedsChangeSet(facts);
        string cwd = !string.IsNullOrWhiteSpace(ev.Cwd) ? ev.Cwd : workingDirectory;
        facts = WithSurroundings(facts, cwd);
        facts = WithRunStarted(facts, ev, cwd);

        if (!isPreWithReturn && !needsSinceDispatch && !needsReviewChangedPaths && !needsChangeSet)
        {
            return facts;
        }

        if (isPreWithReturn)
        {
            GitSnapshot? preSnapshot = git.CaptureSnapshot(cwd);
            if (preSnapshot is not null)
            {
                RecordPreSnapshot(ResolveArbiterDirectory(cwd), SnapshotFileName, classification.Target, ev, preSnapshot);
            }

            RecordCanonicalSnapshot(facts, classification, ev, cwd);
        }

        facts = WithStrayPaths(facts, classification, ev, cwd);

        if (!needsSinceDispatch && !needsReviewChangedPaths && !needsChangeSet)
        {
            return facts;
        }

        string? head = git.GetHead(cwd);
        if (head is null)
        {
            return facts;
        }

        if (needsSinceDispatch)
        {
            GitSnapshot? baseline = FindPreSnapshot(ResolveArbiterDirectory(cwd), SnapshotFileName, classification.Target, ev);
            if (baseline is not null)
            {
                IReadOnlyList<string> sinceDispatch = [.. git.GetChangedPathsSinceSnapshot(cwd, baseline)];
                facts = facts.With("git.changed-paths.since-dispatch", sinceDispatch, ArbiterFactLabel.Derived);
            }
        }

        // Resolved once and reused: a fetch or branch switch between two resolutions would measure the
        // changed paths and the hunks from different bases.
        string? baseRef = needsReviewChangedPaths || needsChangeSet ? ResolveBase(cwd) : null;

        if (needsReviewChangedPaths && baseRef is not null)
        {
            IReadOnlyList<string> changed = [.. git.GetChangedPathsSinceBase(cwd, baseRef)];
            facts = facts.With("review.changed-paths", changed, ArbiterFactLabel.Derived);
            facts = facts.With("review.base-ref", baseRef, ArbiterFactLabel.Derived);
            if (facts.Contains("review.diff-summary")
                && git.GetDiffSummary(cwd, baseRef, changed) is { } summary)
            {
                facts = facts.With("review.diff-summary", summary, ArbiterFactLabel.Derived);
            }
        }

        if (needsChangeSet && baseRef is not null)
        {
            facts = WithChangeFacts(facts, git.GetChangeSet(cwd, baseRef));
        }

        return facts;
    }

    /// <summary>
    /// Fills <c>run.started</c> for a dispatch to a specialist that writes project files: whether a run
    /// recorded by <c>run start</c> is live for this repository. Left absent (undecidable, so the dispatch
    /// proceeds) when git cannot name the repository or the run records cannot be read.
    /// </summary>
    private ArbiterFactSet WithRunStarted(ArbiterFactSet facts, ArbiterEvent ev, string cwd)
    {
        if (!facts.Contains(RunStartedFact)
            || !string.Equals(ev.Phase, "pre", StringComparison.OrdinalIgnoreCase)
            || !facts.TryGet("delegation.target-profile", out ArbiterFact? profile)
            || profile.Value is not string name
            || !RunWriterProfiles.IsWriter(name))
        {
            return facts;
        }

        bool? started = RunStarted(cwd, homeDirectory ?? process.HomeDirectory);
        return started is null ? facts : facts.With(RunStartedFact, started.Value, ArbiterFactLabel.Derived);
    }

    /// <summary>Whether a run is live for the repository <paramref name="workingDirectory" /> belongs to; null when git cannot name the repository or the records cannot be read.</summary>
    private bool? RunStarted(string workingDirectory, string home)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        string? repository = git.GetCommonDirectory(workingDirectory);
        return repository is null ? null : runStates.HasLiveRun(home, repository);
    }

    /// <summary>
    /// Before a dispatch to a specialist that writes files, while a live run of this repository works in a
    /// worktree of its own, records a snapshot of the repository's canonical checkout, so the return can say
    /// which paths the dispatch left there (<c>KW-ARB-CHECKOUT-001</c>).
    /// </summary>
    private void RecordCanonicalSnapshot(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev, string cwd)
    {
        if (!IsRunBranchWriter(facts) || CanonicalCheckoutWithSeparateRun(cwd) is not { } canonical)
        {
            return;
        }

        GitSnapshot? snapshot = git.CaptureSnapshot(canonical);
        if (snapshot is not null)
        {
            RecordPreSnapshot(ResolveArbiterDirectory(cwd), CanonicalSnapshotFileName, classification.Target, ev, snapshot);
        }
    }

    /// <summary>
    /// On a writer's return, fills <c>checkout.stray-paths</c>: the canonical checkout's paths that changed since
    /// the dispatch's snapshot of it. Absent when no snapshot was recorded (no live run in a separate worktree at
    /// dispatch, or git could not be read), which allows.
    /// </summary>
    private ArbiterFactSet WithStrayPaths(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev, string cwd)
    {
        if (!facts.Contains(StrayPathsFact)
            || !string.Equals(ev.Phase, "post", StringComparison.OrdinalIgnoreCase)
            || !IsRunBranchWriter(facts)
            || git.GetCommonDirectory(cwd) is not { } repository
            || checkouts.CanonicalCheckoutOf(repository) is not { } canonical)
        {
            return facts;
        }

        GitSnapshot? baseline = FindPreSnapshot(ResolveArbiterDirectory(cwd), CanonicalSnapshotFileName, classification.Target, ev);
        if (baseline is null)
        {
            return facts;
        }

        // Only what the dispatch left behind counts: a path it changed and then restored, or a stray file a
        // clean-up dispatch put back, is no longer dirty. A commit on the canonical branch keeps every path it
        // moved. The agent scratchpad is working state, not a change to the checkout.
        IReadOnlySet<string> changed = git.GetChangedPathsSinceSnapshot(canonical, baseline);
        bool headMoved = !string.Equals(git.GetHead(canonical), baseline.Head, StringComparison.Ordinal);
        IReadOnlyDictionary<string, string> dirty = git.GetDirtyStatuses(canonical);
        List<string> stray = [.. changed
            .Where(path => (headMoved || dirty.ContainsKey(path)) && !IsScratchpad(path))
            .Order(StringComparer.Ordinal)];
        return facts.With(StrayPathsFact, stray, ArbiterFactLabel.Derived);
    }

    private static bool IsScratchpad(string path) =>
        path.StartsWith(".agent_scratchpad/", StringComparison.Ordinal);

    private static bool IsRunBranchWriter(ArbiterFactSet facts) =>
        facts.TryGet("delegation.target-profile", out ArbiterFact? profile)
        && profile.Value is string name
        && RunWriterProfiles.IsWriter(name);

    /// <summary>The canonical checkout of <paramref name="cwd" />'s repository when a live run of it works in another worktree.</summary>
    private string? CanonicalCheckoutWithSeparateRun(string cwd)
    {
        string home = homeDirectory ?? process.HomeDirectory;
        return git.GetCommonDirectory(cwd) is { } repository
               && checkouts.CanonicalCheckoutOf(repository) is { } canonical
               && checkouts.LiveSeparateRuns(home, canonical) is { Count: > 0 }
            ? canonical
            : null;
    }

    private static bool NeedsChangeSet(ArbiterFactSet facts) =>
        facts.Contains(ChangedHunksFact)
        || facts.Contains(RenameOnlyFact)
        || facts.Contains(CommentOnlyFact)
        || facts.Contains(NoCodeChangeFact)
        || facts.Contains(FindingChangedLinesFact);

    /// <summary>
    /// Fills the change facts from one measured change set. A null change set leaves every fact absent,
    /// so a rule reads an unmeasured change as undecidable and never as an empty one.
    /// </summary>
    /// <remarks>
    /// The no-code-change set is the union of comment-only, rename-only and changed markdown: a lens
    /// may skip those paths, so a code change must never land in it. A file is comment-only only when
    /// every one of its hunks is, because each hunk is judged alone.
    /// </remarks>
    private static ArbiterFactSet WithChangeFacts(ArbiterFactSet facts, ChangeSet? change)
    {
        if (change is null)
        {
            return facts;
        }

        List<string> changedPaths = change.Hunks.Select(measured => measured.Hunk.Path).Distinct(StringComparer.Ordinal).ToList();
        List<string> commentOnly = changedPaths
            .Where(path => change.Hunks
                .Where(measured => string.Equals(measured.Hunk.Path, path, StringComparison.Ordinal))
                .All(measured => CommentSyntax.IsCommentOnly(path, measured.ChangedLines)))
            .Order(StringComparer.Ordinal)
            .ToList();
        List<string> renameOnly = change.RenameOnlyPaths.Order(StringComparer.Ordinal).ToList();
        List<string> noCodeChange = commentOnly
            .Concat(renameOnly)
            .Concat(changedPaths.Where(path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (facts.Contains(ChangedHunksFact))
        {
            facts = facts.With(ChangedHunksFact, change.Hunks.Select(measured => measured.Hunk).ToList(), ArbiterFactLabel.Derived);
        }

        if (facts.Contains(RenameOnlyFact))
        {
            facts = facts.With(RenameOnlyFact, renameOnly, ArbiterFactLabel.Derived);
        }

        if (facts.Contains(CommentOnlyFact))
        {
            facts = facts.With(CommentOnlyFact, commentOnly, ArbiterFactLabel.Derived);
        }

        if (facts.Contains(NoCodeChangeFact))
        {
            facts = facts.With(NoCodeChangeFact, noCodeChange, ArbiterFactLabel.Derived);
        }

        return facts.Contains(FindingChangedLinesFact)
            ? WithFindingChangedLines(facts, change)
            : facts;
    }

    /// <summary>
    /// The most new-side lines the fact lists. A finding names one line, so a change past this bound is
    /// left absent rather than listed in full or cut short.
    /// </summary>
    private const int MaxFindingChangedLines = 100_000;

    /// <summary>
    /// The new-side lines of the finding's file that fall inside a changed hunk: the lines a finding can
    /// honestly claim the change touched. Absent without a finding file, because there is nothing to
    /// intersect with the hunks.
    /// </summary>
    /// <remarks>
    /// Line 0 is never listed: a pure deletion reports a zero count there and names no line. A hunk that
    /// starts before line 1 with lines in it cannot come from git, so the fact stays absent. The total is
    /// counted before any line is listed, so a huge hunk is refused without being expanded.
    /// </remarks>
    internal static ArbiterFactSet WithFindingChangedLines(ArbiterFactSet facts, ChangeSet change)
    {
        if (!facts.TryGet("finding.file", out ArbiterFact? file) || file.Value is not string path)
        {
            return facts;
        }

        List<ChangedHunk> hunks = change.Hunks
            .Select(measured => measured.Hunk)
            .Where(hunk => string.Equals(hunk.Path, path, StringComparison.Ordinal) && hunk.Count > 0)
            .ToList();
        if (hunks.Any(hunk => hunk.Start < 1 || (long)hunk.Start + hunk.Count - 1 > int.MaxValue))
        {
            return facts;
        }

        long total = hunks.Sum(hunk => (long)hunk.Count);
        if (total > MaxFindingChangedLines)
        {
            return facts;
        }

        List<int> lines = hunks
            .SelectMany(hunk => Enumerable.Range(hunk.Start, hunk.Count))
            .Distinct()
            .Order()
            .ToList();
        return facts.With(FindingChangedLinesFact, lines, ArbiterFactLabel.Derived);
    }

    private ArbiterFactSet WithSurroundings(ArbiterFactSet facts, string cwd)
    {
        if (!facts.Contains("file.surroundings")
            || !facts.TryGet("finding.file", out ArbiterFact? file)
            || file.Value is not string path
            || !facts.TryGet("finding.line", out ArbiterFact? line)
            || line.Value is not int number
            || number < 1)
        {
            return facts;
        }

        string? resolved = planPaths.Resolve(path, cwd);
        if (resolved is null)
        {
            return facts;
        }

        int first = Math.Max(1, number - SurroundingLines);
        int linesBeforeFinding = number - first;
        IReadOnlyList<string>? window = windows.ReadWindow(
            resolved, MaxSurroundingsFileBytes, first - 1, linesBeforeFinding + 1 + SurroundingLines);
        return window is not null && window.Count > linesBeforeFinding
            ? facts.With("file.surroundings", string.Join('\n', window), ArbiterFactLabel.Derived)
            : facts;
    }

    private static bool IsDispatchTriggerWithReturn(string? trigger) =>
        string.Equals(trigger, "delegate", StringComparison.Ordinal)
        || string.Equals(trigger, "delegate.planner", StringComparison.Ordinal)
        || string.Equals(trigger, "investigate", StringComparison.Ordinal);

    private string ResolveArbiterDirectory(string cwd) =>
        !string.IsNullOrWhiteSpace(arbiterDirectory)
            ? arbiterDirectory
            : Path.Combine(cwd, "artifacts", "arbiter");

    private void RecordPreSnapshot(
        string directory,
        string fileName,
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

        snapshots.Update(directory, fileName, json =>
        {
            List<StoredSnapshot> entries = HookSnapshotDocument.Parse(json);
            DateTimeOffset now = clock.GetUtcNow();
            entries.RemoveAll(entry => now - entry.RecordedAt > HookSnapshotDocument.Retention);
            entries.Add(new StoredSnapshot(
                ev.ToolCallId,
                target,
                HookSnapshotDocument.HashPrompt(ev.Prompt),
                snapshot.Head,
                clock.GetUtcNow(),
                new Dictionary<string, string>(snapshot.BlobIds, StringComparer.Ordinal),
                new Dictionary<string, string>(snapshot.Statuses, StringComparer.Ordinal)));
            while (entries.Count > HookSnapshotDocument.MaxStoredSnapshots)
            {
                entries.RemoveAt(0);
            }

            return HookSnapshotDocument.Serialize(entries);
        });
    }

    private GitSnapshot? FindPreSnapshot(
        string directory,
        string fileName,
        string? target,
        ArbiterEvent ev)
    {
        // Peek, never consume: a retry carrying the same tool-call id must see the same
        // baseline instead of falling back to a diff the dispatch's commits never appear in.
        // Lock-free: atomic renames mean readers only ever see whole documents, so pairing
        // a recorded baseline never waits on — or blocks — a concurrent record.
        // A rename landing mid-read serves the previous whole document, which may lack a
        // just-recorded entry. Re-read briefly before calling the dispatch unobservable:
        // a transient miss then still pairs, while genuine absence resolves absent.
        // Without a pairing key no record can match: certain absence, no disk reads.
        if (string.IsNullOrWhiteSpace(ev.ToolCallId) && string.IsNullOrWhiteSpace(ev.Prompt))
        {
            return null;
        }

        if (!snapshots.Exists(directory, fileName))
        {
            return null;
        }

        StoredSnapshot? entry = PeekUntilSettled(
            clock,
            () => snapshots.ReadText(directory, fileName),
            text =>
            {
                List<StoredSnapshot> entries = HookSnapshotDocument.Parse(text);
                int index = HookSnapshotDocument.FindMatchingIndex(entries, target, ev.ToolCallId, ev.Prompt);
                return index >= 0 ? entries[index] : null;
            },
            PeekRetryBudget);
        return entry is null
            ? null
            : new GitSnapshot(
                entry.Head,
                entry.BlobIds ?? new Dictionary<string, string>(StringComparer.Ordinal),
                entry.Statuses ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    internal static T? PeekUntilSettled<T>(TimeProvider clock, Func<string> read, Func<string, T?> find, TimeSpan budget)
        where T : class
    {
        // Contents, not timestamps: a save inside one timestamp tick of a coarse
        // filesystem still counts as a change, so it still pairs.
        long started = clock.GetTimestamp();
        string seen = read();
        while (true)
        {
            T? match = find(seen);
            if (match is not null || clock.GetElapsedTime(started) >= budget)
            {
                return match;
            }

            string next = read();
            if (string.Equals(next, seen, StringComparison.Ordinal))
            {
                return null;
            }

            seen = next;
            Task.Delay(PeekPause, clock).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// The base the review is measured from: the base the run recorded for the checkout's
    /// branch when that base still resolves, or the default review base otherwise. A stale
    /// or bogus record falls back rather than measuring against a ref that is gone.
    /// </summary>
    private string ResolveBase(string workingDirectory)
    {
        string home = homeDirectory ?? process.HomeDirectory;
        string? branch = git.GetCurrentBranch(workingDirectory);
        if (branch is not null &&
            runStates.TryReadBaseRef(home, branch, out string? recorded) &&
            !string.IsNullOrWhiteSpace(recorded) &&
            git.RefExists(workingDirectory, recorded))
        {
            return recorded;
        }

        return ReviewBase;
    }
}

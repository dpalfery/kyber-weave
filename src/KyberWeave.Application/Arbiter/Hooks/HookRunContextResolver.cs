using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Resolves one hook invocation's run context over the product's seams. The order is fixed: the agent's worktree, the
/// worktree that holds the dispatch's plan, the payload's <c>cwd</c> when it is inside a worktree other than the
/// canonical checkout, the absolute target of the command's leading <c>cd</c> when the cwd is absent or is the
/// canonical checkout, the single live run, and else <see cref="HookRunContextSource.Ambiguous" /> when several runs
/// are live. Every candidate must belong to this repository or one of its own git worktrees.
/// </summary>
/// <remarks>
/// <para>
/// A chosen worktree maps to its run in two ways: it is, or is inside, a live run record's worktree (the longest one
/// wins), or it is a task worktree whose checked-out branch has exactly one live run's branch as an ancestor, the rule
/// <c>run integrate</c> uses. Run start only records run worktrees; task worktrees are made beside them by the harness
/// or the conductor, so containment alone cannot find a task worktree's run. A <c>git -C</c> names the directory of
/// one git step, never where the session works, so it is no signal. With several live runs, a worktree that maps to
/// none of them, or to more than one, is ambiguous: no run is active and paths are read from the canonical checkout,
/// so a guard never judges a call by another run's plan and files.
/// </para>
/// <para>
/// No member throws on a missing or unreadable input. A candidate that cannot be validated, and a plan or task file
/// that cannot be read, adds a note naming the reason and the path tried, so a guard can escalate with the precise
/// cause instead of denying on an absence.
/// </para>
/// </remarks>
/// <param name="git">Names the repository's common directory and its worktrees.</param>
/// <param name="planPaths">Resolves the dispatch's plan inside a candidate worktree.</param>
/// <param name="planRoots">Chooses the worktree that holds the plan among the validated candidates.</param>
/// <param name="files">Checks whether a candidate directory, plan or task file exists.</param>
/// <param name="paths">Compares path spellings and keeps a candidate inside the repository.</param>
/// <param name="environment">The process directory a payload with no directory falls back to.</param>
/// <param name="runStates">Reads the records <c>run start</c> wrote.</param>
/// <param name="branches">
/// Reads the branch a recorded worktree has checked out, which is what makes its run live, and whether a run's branch
/// is an ancestor of a task worktree's branch.
/// </param>
public sealed class HookRunContextResolver(
    IGitRepositoryFacts git,
    IPlanFileResolver planPaths,
    IPlanRootResolver planRoots,
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment,
    IRunStateStore runStates,
    ICheckedOutBranchReader branches) : IHookRunContextResolver
{
    /// <inheritdoc />
    public HookRunContext Resolve(HookRunContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> notes = [];

        (string root, string? common) = Anchor(request, notes);
        IReadOnlyList<string> worktrees = Worktrees(root, common, notes);
        IReadOnlyList<RunRecord> liveRuns = LiveRuns(request.HomeDirectory, common, worktrees, notes);

        (string worktree, HookRunContextSource source, RunRecord? activeRun) =
            Candidate(request, root, common, worktrees, liveRuns, notes);
        return Hydrate(root, common, activeRun, worktree, worktrees, source, PlanFileOf(request.Prompt), notes);
    }

    /// <inheritdoc />
    public HookRunContext SelectPlan(HookRunContext context, string? planFile, string? text)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<string> notes = [.. context.Notes];
        string? plan = planFile;
        if (plan is null)
        {
            plan = PlanFileOf(text);
        }

        string worktree = planRoots.ResolvePlanRoot(
            context.RepositoryRoot, plan, text, _ => context.Worktrees);
        if (!IsValidated(worktree, context.RepositoryRoot, context.Worktrees))
        {
            notes.Add($"worktree '{worktree}' is not a worktree of this repository; reading the plan from '{context.Worktree}'.");
            worktree = context.Worktree;
        }

        return Hydrate(
            context.RepositoryRoot,
            context.CommonDirectory,
            context.ActiveRun,
            worktree,
            context.Worktrees,
            context.Source,
            plan,
            notes);
    }

    /// <summary>
    /// The repository anchor: the first candidate git names a common directory for. When git names none, the first
    /// candidate still stands — the payload's own directory — because configuration and the ledger are read there and a
    /// repository git cannot see must not move them to the hook process's directory.
    /// </summary>
    private (string Root, string? Common) Anchor(HookRunContextRequest request, List<string> notes)
    {
        string? first = null;
        foreach ((string? candidate, string name) in new[]
                 {
                     (request.WorkspaceRoot, "the payload's workspace root"),
                     (request.EventCwd, "the payload's cwd"),
                     (request.ProcessDirectory, "the process directory"),
                     (environment.CurrentDirectory, "the process directory"),
                 })
        {
            if (candidate is not { } present || string.IsNullOrWhiteSpace(present))
            {
                continue;
            }

            if (!TryFullPath(present, BaseFor(present), out string full, notes, name))
            {
                continue;
            }

            first ??= full;
            if (!DirectoryExists(full))
            {
                notes.Add($"{name} '{full}' is not a directory on disk.");
                continue;
            }

            // The event's own directory is the anchor even when git names no repository there: configuration and the
            // ledger belong to the workspace the event was sent from, never to whichever later candidate happens to
            // sit inside some other repository.
            string? common = CommonDirectoryOf(full);
            if (common is null)
            {
                notes.Add($"{name} '{full}' names no repository; paths are read from it and the rules decide on the absence.");
            }

            return (full, common);
        }

        string fallback = first ?? FullPathOrRaw(environment.CurrentDirectory);
        notes.Add($"no candidate is a directory on disk; reading paths from '{fallback}' and letting the rules decide on the absence.");
        return (fallback, null);
    }

    /// <summary>This repository's worktrees, each validated as belonging to it; the anchor always stands.</summary>
    private IReadOnlyList<string> Worktrees(string root, string? common, List<string> notes)
    {
        List<string> worktrees = [root];
        if (common is null)
        {
            return worktrees;
        }

        IReadOnlyList<string> listed;
        try
        {
            listed = git.ListWorktrees(root);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            notes.Add($"the repository's worktrees could not be listed ({exception.Message}); trusting only '{root}'.");
            return worktrees;
        }

        foreach (string candidate in listed)
        {
            if (!TryFullPath(candidate, root, out string full, notes, "a listed worktree"))
            {
                continue;
            }

            if (worktrees.Any(existing => IsSame(existing, full)))
            {
                continue;
            }

            string? candidateCommon = CommonDirectoryOf(full);
            if (candidateCommon is null || !IsSame(candidateCommon, common))
            {
                notes.Add($"worktree '{full}' belongs to another repository; it was not trusted.");
                continue;
            }

            if (!DirectoryExists(full))
            {
                notes.Add($"worktree '{full}' is no longer on disk; it was not trusted.");
                continue;
            }

            worktrees.Add(full);
        }

        return worktrees;
    }

    /// <summary>Every live run record for this repository whose worktree still exists, newest first.</summary>
    private IReadOnlyList<RunRecord> LiveRuns(
        string home,
        string? common,
        IReadOnlyList<string> worktrees,
        List<string> notes)
    {
        if (common is null || string.IsNullOrWhiteSpace(home))
        {
            return [];
        }

        IReadOnlyList<RunRecord>? records;
        try
        {
            records = runStates.ReadRecords(home);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            notes.Add($"the run records could not be read ({exception.Message}).");
            return [];
        }

        if (records is null)
        {
            notes.Add($"the run records under '{home}' could not be read.");
            return [];
        }

        List<RunRecord> live = [];
        foreach (RunRecord record in records)
        {
            if (!IsSame(record.Repository, common))
            {
                continue;
            }

            if (!IsValidated(record.Worktree, worktrees[0], worktrees))
            {
                notes.Add($"run record for branch '{record.Branch}' names worktree '{record.Worktree}', which is not a worktree of this repository; it was not used.");
                continue;
            }

            // A record says where a run started, not that its worktree still works there. The run is live only while
            // the worktree has the run's branch checked out; a reused worktree, a finished run, or a branch git
            // cannot read is not this run, and the next candidate is tried.
            if (!DirectoryExists(record.Worktree))
            {
                notes.Add($"run record for branch '{record.Branch}' names worktree '{record.Worktree}', which is no longer on disk; it was not used.");
                continue;
            }

            string? checkedOut = BranchOf(record.Worktree);
            if (!string.Equals(checkedOut, record.Branch, StringComparison.Ordinal))
            {
                notes.Add($"run record for branch '{record.Branch}' names worktree '{record.Worktree}', where '{checkedOut ?? "<detached or unreadable>"}' is checked out; it is not this run and was not used.");
                continue;
            }

            live.Add(record);
        }

        return [.. live.OrderByDescending(record => record.StartedAt ?? DateTimeOffset.MinValue)];
    }

    /// <summary>The worktree and run the signals choose, in the order the class summary gives.</summary>
    private (string Worktree, HookRunContextSource Source, RunRecord? Run) Candidate(
        HookRunContextRequest request,
        string root,
        string? common,
        IReadOnlyList<string> worktrees,
        IReadOnlyList<RunRecord> liveRuns,
        List<string> notes)
    {
        string? plan = PlanFileOf(request.Prompt);
        string? canonical = CanonicalOf(common, worktrees);

        if (TextOrNull(request.AgentWorktree) is { } agentCandidate)
        {
            if (TryCandidate(agentCandidate, "the agent's working directory", root, common, worktrees, notes, out string agentWorktree))
            {
                return (agentWorktree, HookRunContextSource.EventWorktree, EventRun(agentWorktree, liveRuns, notes));
            }
        }

        // The plan's own holding worktree is the strongest event-named signal: the dispatch prompt or packet says
        // which plan the work belongs to, and only the worktree that holds it can read that plan.
        if (plan is not null || NamesAWorktree(request.Prompt))
        {
            string chosen = planRoots.ResolvePlanRoot(root, plan, request.Prompt, _ => worktrees);
            if (!IsSame(chosen, root) && IsValidated(chosen, root, worktrees))
            {
                notes.Add($"the event names the plan's worktree '{chosen}'.");
                return (chosen, HookRunContextSource.EventWorktree, EventRun(chosen, liveRuns, notes));
            }
        }

        string? cwd = null;
        if (TextOrNull(request.EventCwd) is { } eventCwd
            && TryCandidate(eventCwd, "the payload's cwd", root, common, worktrees, notes, out string cwdWorktree))
        {
            cwd = cwdWorktree;
        }

        bool cwdIsCanonical = cwd is not null && canonical is not null && IsSame(cwd, canonical);
        if (cwd is not null && !cwdIsCanonical)
        {
            if (!string.IsNullOrWhiteSpace(request.LeadingCdTarget))
            {
                notes.Add($"the command's leading cd to '{request.LeadingCdTarget}' was ignored: the payload's cwd '{cwd}' already places the call.");
            }

            if (liveRuns.Count == 0)
            {
                return (cwd, HookRunContextSource.PayloadCwd, null);
            }

            if (RunOf(cwd, liveRuns, notes) is { } cwdRun)
            {
                notes.Add($"the payload's cwd '{cwd}' belongs to the live run for branch '{cwdRun.Branch}'.");
                return (cwd, HookRunContextSource.PayloadCwd, cwdRun);
            }

            return Ambiguous(canonical ?? root, liveRuns, $"the payload's cwd '{cwd}' belongs to no single live run", notes);
        }

        // A leading cd counts only where the cwd says nothing about the run: absent, or the canonical checkout.
        if (LeadingCdCandidate(request, root, common, worktrees, liveRuns, canonical, notes) is { } fromLeadingCd)
        {
            return fromLeadingCd;
        }

        if (liveRuns.Count == 1)
        {
            RunRecord single = liveRuns[0];
            notes.Add($"the live run for branch '{single.Branch}' works in '{single.Worktree}'.");
            return (single.Worktree, HookRunContextSource.ActiveRun, single);
        }

        if (liveRuns.Count > 1)
        {
            return Ambiguous(canonical ?? root, liveRuns, "no signal chooses one of them", notes);
        }

        if (cwd is not null)
        {
            return (cwd, HookRunContextSource.PayloadCwd, null);
        }

        notes.Add($"the payload names no worktree of this repository; reading paths from '{root}'.");
        return (root, worktrees.Count > 0 && IsSame(root, worktrees[0])
            ? HookRunContextSource.PayloadCwd
            : HookRunContextSource.None, null);
    }

    /// <summary>
    /// The candidate the command's leading <c>cd</c> names when the cwd says nothing about the run. Null when the cd
    /// names no validated worktree of this repository, or names the canonical checkout the cwd already is.
    /// </summary>
    private (string Worktree, HookRunContextSource Source, RunRecord? Run)? LeadingCdCandidate(
        HookRunContextRequest request,
        string root,
        string? common,
        IReadOnlyList<string> worktrees,
        IReadOnlyList<RunRecord> liveRuns,
        string? canonical,
        List<string> notes)
    {
        if (TextOrNull(request.LeadingCdTarget) is not { } leadingCdTarget
            || !TryCandidate(leadingCdTarget, "the command's leading cd", root, common, worktrees, notes, out string cdWorktree)
            || (canonical is not null && IsSame(cdWorktree, canonical)))
        {
            return null;
        }

        if (RunOf(cdWorktree, liveRuns, notes) is { } cdRun)
        {
            notes.Add($"the command's leading cd enters '{cdWorktree}', where the live run for branch '{cdRun.Branch}' works.");
            return (cdWorktree, HookRunContextSource.EventWorktree, cdRun);
        }

        notes.Add($"the command's leading cd enters '{cdWorktree}', which belongs to no single live run; it was not used.");
        return null;
    }

    /// <summary>
    /// The ambiguous outcome: several runs are live and the signals chose none of them. Paths are read from the
    /// canonical checkout, no run is active, and the note names every live run so the operator can see why.
    /// </summary>
    private static (string Worktree, HookRunContextSource Source, RunRecord? Run) Ambiguous(
        string canonical,
        IReadOnlyList<RunRecord> liveRuns,
        string reason,
        List<string> notes)
    {
        string named = string.Join(", ", liveRuns.Select(run => $"'{run.Branch}' at '{run.Worktree}'"));
        notes.Add($"the run is ambiguous: {liveRuns.Count} runs are live ({named}) and {reason}; no run is active and paths are read from '{canonical}'.");
        return (canonical, HookRunContextSource.Ambiguous, null);
    }

    /// <summary>
    /// The run of a worktree the event itself named. The worktree stands whatever its run; the run is the one it
    /// maps to, else the single live run as before, else none when several are live and it maps to none of them.
    /// </summary>
    private RunRecord? EventRun(string worktree, IReadOnlyList<RunRecord> liveRuns, List<string> notes)
    {
        if (liveRuns.Count == 0)
        {
            return null;
        }

        if (RunOf(worktree, liveRuns, notes) is { } mapped)
        {
            return mapped;
        }

        if (liveRuns.Count == 1)
        {
            return liveRuns[0];
        }

        notes.Add($"worktree '{worktree}' belongs to no single one of the {liveRuns.Count} live runs; no run is active.");
        return null;
    }

    /// <summary>
    /// The live run a worktree belongs to: the longest run worktree that equals or contains it, else the one run whose
    /// branch is an ancestor of the worktree's checked-out branch. Null when neither names exactly one run, or when git
    /// cannot answer for one of them.
    /// </summary>
    private RunRecord? RunOf(string worktree, IReadOnlyList<RunRecord> liveRuns, List<string> notes)
    {
        RunRecord? containing = liveRuns
            .Where(run => IsSame(run.Worktree, worktree) || IsWithin(run.Worktree, worktree))
            .OrderByDescending(run => run.Worktree.Length)
            .FirstOrDefault();
        if (containing is not null)
        {
            return containing;
        }

        string? branch = BranchOf(worktree);
        if (branch is null)
        {
            notes.Add($"worktree '{worktree}' has no branch git can read, so no run's branch can be its ancestor.");
            return null;
        }

        List<RunRecord> ancestors = [];
        foreach (RunRecord run in liveRuns)
        {
            bool? ancestor = AncestorOf(worktree, run.Branch, branch);
            if (ancestor is null)
            {
                notes.Add($"git could not tell whether '{run.Branch}' is an ancestor of '{branch}' in '{worktree}'.");
                return null;
            }

            if (ancestor.Value)
            {
                ancestors.Add(run);
            }
        }

        return ancestors.Count == 1 ? ancestors[0] : null;
    }

    /// <summary>The canonical checkout: the listed worktree whose own <c>.git</c> is the repository's common directory.</summary>
    private string? CanonicalOf(string? common, IReadOnlyList<string> worktrees) =>
        common is null
            ? null
            : worktrees.FirstOrDefault(worktree => IsSame(Path.Combine(worktree, ".git"), common));

    /// <summary>Accepts a candidate only when it is this repository or one of its worktrees.</summary>
    private bool TryCandidate(
        string candidate,
        string name,
        string root,
        string? common,
        IReadOnlyList<string> worktrees,
        List<string> notes,
        out string worktree)
    {
        worktree = root;
        if (!TryFullPath(candidate, BaseFor(candidate), out string full, notes, name))
        {
            return false;
        }

        // The worktrees git listed come first: the anchor heads the list as the payload spelled it, which may be a
        // directory inside a worktree, and a candidate is placed at the worktree's own root, not at that directory.
        string? containing = Containing(worktrees.Skip(1), full) ?? Containing(worktrees.Take(1), full);
        if (containing is not null)
        {
            string? candidateCommon = CommonDirectoryOf(full);
            if (common is not null && candidateCommon is null)
            {
                // git named no repository for the candidate: a path this repository's worktrees list happens to hold
                // is not proof, so it is refused exactly as Worktrees() refuses one it cannot attest.
                notes.Add($"{name} '{full}': git could not name its repository, so it was refused rather than trusted.");
                return false;
            }

            if (common is null || (candidateCommon is not null && IsSame(candidateCommon, common)))
            {
                worktree = containing;
                return true;
            }

            notes.Add($"{name} '{full}' belongs to another repository; it was refused.");
            return false;
        }

        notes.Add($"{name} '{full}' is outside this repository and its worktrees; it was refused.");
        return false;
    }

    /// <summary>The longest of <paramref name="worktrees" /> that equals or contains <paramref name="full" />.</summary>
    private string? Containing(IEnumerable<string> worktrees, string full) =>
        worktrees
            .OrderByDescending(worktree => worktree.Length)
            .FirstOrDefault(worktree => IsSame(worktree, full) || IsWithin(worktree, full));

    /// <summary>Reads the plan and its task file inside the chosen worktree, recording what was tried.</summary>
    private HookRunContext Hydrate(
        string root,
        string? common,
        RunRecord? activeRun,
        string worktree,
        IReadOnlyList<string> worktrees,
        HookRunContextSource source,
        string? planFile,
        List<string> notes)
    {
        string? planPath = null;
        string? taskFile = null;
        bool taskFileExists = false;

        if (planFile is not null)
        {
            try
            {
                planPath = planPaths.Resolve(planFile, worktree);
            }
            catch (Exception exception) when (FileFailure.Is(exception) || exception is ArgumentException)
            {
                notes.Add($"the plan '{planFile}' could not be resolved inside '{worktree}' ({exception.Message}).");
            }

            if (planPath is null)
            {
                notes.Add($"the plan '{planFile}' names nothing inside worktree '{worktree}'.");
            }
            else if (!FileExists(planPath))
            {
                notes.Add($"the plan '{planFile}' was not found; tried '{planPath}'.");
                planPath = null;
            }
            else
            {
                string relative = RelativeTo(worktree, planPath);
                string taskRelative = PlanTaskFileRules.TaskFilePathFor(
                    relative, PlanTaskFileRules.IsSpecTaskListPath(relative));
                taskFile = Combine(worktree, taskRelative);
                taskFileExists = FileExists(taskFile);
                if (!taskFileExists)
                {
                    notes.Add($"the task file for '{planFile}' was not found; tried '{taskFile}'.");
                }
            }
        }

        return new HookRunContext(
            root,
            common,
            activeRun,
            worktree,
            worktrees,
            planFile,
            planPath,
            taskFile,
            taskFileExists,
            source,
            notes);
    }

    private static string? PlanFileOf(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        HeaderBlock block = HeaderBlock.Parse(prompt);
        if (block.Headers.TryGetValue(DispatchHeaders.PlanFile, out string? valid))
        {
            return valid;
        }

        return block.RawValues.TryGetValue(DispatchHeaders.PlanFile, out string? raw) ? raw : null;
    }

    private static bool NamesAWorktree(string? prompt) =>
        prompt is not null && prompt.Contains("worktree", StringComparison.OrdinalIgnoreCase);

    private string? CommonDirectoryOf(string directory)
    {
        try
        {
            return git.GetCommonDirectory(directory);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }
    }

    /// <summary>The branch a worktree has checked out; null when git cannot name one, which no record matches.</summary>
    private string? BranchOf(string worktree)
    {
        try
        {
            return branches.Read(worktree);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }
    }

    /// <summary>Whether one branch is an ancestor of another; null when git cannot answer.</summary>
    private bool? AncestorOf(string worktree, string ancestor, string descendant)
    {
        try
        {
            return branches.IsAncestor(worktree, ancestor, descendant);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }
    }

    private bool IsValidated(string candidate, string root, IReadOnlyList<string> worktrees)
    {
        string? full = FullPathOrNull(candidate, BaseFor(candidate));
        return full is not null
            && worktrees.Any(worktree => IsSame(worktree, full) || IsWithin(worktree, full));
    }

    /// <summary>
    /// The base a harness-provided path is resolved against: itself when it is already fully qualified, else the
    /// directory the hook process started in — a harness sends a relative path from the session's own directory, and
    /// the path seam refuses a base that is not fully qualified.
    /// </summary>
    private string BaseFor(string candidate) =>
        Path.IsPathFullyQualified(candidate) ? candidate : environment.CurrentDirectory;

    private bool TryFullPath(string candidate, string baseDirectory, out string full, List<string> notes, string name)
    {
        full = candidate;
        try
        {
            full = paths.GetFullPath(candidate, baseDirectory);
            return true;
        }
        catch (ArgumentException exception)
        {
            notes.Add($"{name} '{candidate}' is not a usable path ({exception.Message}).");
            return false;
        }
    }

    private string? FullPathOrNull(string path, string baseDirectory)
    {
        try
        {
            return paths.GetFullPath(path, baseDirectory);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string FullPathOrRaw(string path)
    {
        try
        {
            return paths.GetFullPath(path, environment.CurrentDirectory);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private bool IsSame(string first, string second)
    {
        try
        {
            return paths.AreSame(first, second);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool IsWithin(string root, string candidate)
    {
        try
        {
            return paths.IsWithin(root, candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool DirectoryExists(string path)
    {
        try
        {
            return files.DirectoryExists(path);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return false;
        }
    }

    private bool FileExists(string path)
    {
        try
        {
            return files.FileExists(path);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return false;
        }
    }

    private static string RelativeTo(string root, string full)
    {
        string prefix = root.TrimEnd('/', '\\') + "/";
        string normalized = full.Replace('\\', '/');
        string normalizedRoot = prefix.Replace('\\', '/');
        return normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            ? normalized[normalizedRoot.Length..]
            : normalized.TrimStart('/');
    }

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The value when it carries text, or null when it is null, empty or whitespace.</summary>
    private static string? TextOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <inheritdoc />
    public async Task<HookRunContext> ResolveAsync(
        HookRunContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> notes = [];

        (string root, string? common) = await AnchorAsync(request, notes, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> worktrees = await WorktreesAsync(root, common, notes, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RunRecord> liveRuns = await LiveRunsAsync(
            request.HomeDirectory, common, worktrees, notes, cancellationToken).ConfigureAwait(false);

        (string worktree, HookRunContextSource source, RunRecord? activeRun) = await CandidateAsync(
            request, root, common, worktrees, liveRuns, notes, cancellationToken).ConfigureAwait(false);
        return Hydrate(root, common, activeRun, worktree, worktrees, source, PlanFileOf(request.Prompt), notes);
    }

    /// <inheritdoc />
    public async Task<HookRunContext> SelectPlanAsync(
        HookRunContext context,
        string? planFile,
        string? text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        List<string> notes = [.. context.Notes];
        string? plan = planFile;
        if (plan is null)
        {
            plan = PlanFileOf(text);
        }

        string worktree = await planRoots.ResolvePlanRootAsync(
            context.RepositoryRoot, plan, text, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!IsValidated(worktree, context.RepositoryRoot, context.Worktrees))
        {
            notes.Add($"worktree '{worktree}' is not a worktree of this repository; reading the plan from '{context.Worktree}'.");
            worktree = context.Worktree;
        }

        return Hydrate(
            context.RepositoryRoot,
            context.CommonDirectory,
            context.ActiveRun,
            worktree,
            context.Worktrees,
            context.Source,
            plan,
            notes);
    }

    private async Task<(string Root, string? Common)> AnchorAsync(
        HookRunContextRequest request,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        string? first = null;
        foreach ((string? candidate, string name) in new[]
                 {
                     (request.WorkspaceRoot, "the payload's workspace root"),
                     (request.EventCwd, "the payload's cwd"),
                     (request.ProcessDirectory, "the process directory"),
                     (environment.CurrentDirectory, "the process directory"),
                 })
        {
            if (candidate is not { } present || string.IsNullOrWhiteSpace(present))
            {
                continue;
            }

            if (!TryFullPath(present, BaseFor(present), out string full, notes, name))
            {
                continue;
            }

            first ??= full;
            if (!DirectoryExists(full))
            {
                notes.Add($"{name} '{full}' is not a directory on disk.");
                continue;
            }

            string? common = await CommonDirectoryOfAsync(full, cancellationToken).ConfigureAwait(false);
            if (common is null)
            {
                notes.Add($"{name} '{full}' names no repository; paths are read from it and the rules decide on the absence.");
            }

            return (full, common);
        }

        string fallback = first ?? FullPathOrRaw(environment.CurrentDirectory);
        notes.Add($"no candidate is a directory on disk; reading paths from '{fallback}' and letting the rules decide on the absence.");
        return (fallback, null);
    }

    private async Task<IReadOnlyList<string>> WorktreesAsync(
        string root,
        string? common,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        List<string> worktrees = [root];
        if (common is null)
        {
            return worktrees;
        }

        IReadOnlyList<string> listed;
        try
        {
            listed = await git.ListWorktreesAsync(root, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            notes.Add($"the repository's worktrees could not be listed ({exception.Message}); trusting only '{root}'.");
            return worktrees;
        }

        foreach (string candidate in listed)
        {
            if (!TryFullPath(candidate, root, out string full, notes, "a listed worktree"))
            {
                continue;
            }

            if (worktrees.Any(existing => IsSame(existing, full)))
            {
                continue;
            }

            string? candidateCommon = await CommonDirectoryOfAsync(full, cancellationToken).ConfigureAwait(false);
            if (candidateCommon is null || !IsSame(candidateCommon, common))
            {
                notes.Add($"worktree '{full}' belongs to another repository; it was not trusted.");
                continue;
            }

            if (!DirectoryExists(full))
            {
                notes.Add($"worktree '{full}' is no longer on disk; it was not trusted.");
                continue;
            }

            worktrees.Add(full);
        }

        return worktrees;
    }

    private async Task<IReadOnlyList<RunRecord>> LiveRunsAsync(
        string home,
        string? common,
        IReadOnlyList<string> worktrees,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (common is null || string.IsNullOrWhiteSpace(home))
        {
            return [];
        }

        IReadOnlyList<RunRecord>? records;
        try
        {
            records = runStates.ReadRecords(home);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            notes.Add($"the run records could not be read ({exception.Message}).");
            return [];
        }

        if (records is null)
        {
            notes.Add($"the run records under '{home}' could not be read.");
            return [];
        }

        List<RunRecord> live = [];
        foreach (RunRecord record in records)
        {
            if (!IsSame(record.Repository, common))
            {
                continue;
            }

            if (!IsValidated(record.Worktree, worktrees[0], worktrees))
            {
                notes.Add($"run record for branch '{record.Branch}' names worktree '{record.Worktree}', which is not a worktree of this repository; it was not used.");
                continue;
            }

            if (!DirectoryExists(record.Worktree))
            {
                notes.Add($"run record for branch '{record.Branch}' names worktree '{record.Worktree}', which is no longer on disk; it was not used.");
                continue;
            }

            string? checkedOut = await BranchOfAsync(record.Worktree, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(checkedOut, record.Branch, StringComparison.Ordinal))
            {
                notes.Add($"run record for branch '{record.Branch}' names worktree '{record.Worktree}', where '{checkedOut ?? "<detached or unreadable>"}' is checked out; it is not this run and was not used.");
                continue;
            }

            live.Add(record);
        }

        return [.. live.OrderByDescending(record => record.StartedAt ?? DateTimeOffset.MinValue)];
    }

    private async Task<(string Worktree, HookRunContextSource Source, RunRecord? Run)> CandidateAsync(
        HookRunContextRequest request,
        string root,
        string? common,
        IReadOnlyList<string> worktrees,
        IReadOnlyList<RunRecord> liveRuns,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        string? plan = PlanFileOf(request.Prompt);
        string? canonical = CanonicalOf(common, worktrees);

        if (TextOrNull(request.AgentWorktree) is { } agentCandidate)
        {
            if (await TryCandidateAsync(
                    agentCandidate, "the agent's working directory", root, common, worktrees, notes, cancellationToken)
                .ConfigureAwait(false) is { } agentWorktree)
            {
                return (agentWorktree, HookRunContextSource.EventWorktree,
                    await EventRunAsync(agentWorktree, liveRuns, notes, cancellationToken).ConfigureAwait(false));
            }
        }

        if (plan is not null || NamesAWorktree(request.Prompt))
        {
            string chosen = await planRoots.ResolvePlanRootAsync(
                root, plan, request.Prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!IsSame(chosen, root) && IsValidated(chosen, root, worktrees))
            {
                notes.Add($"the event names the plan's worktree '{chosen}'.");
                return (chosen, HookRunContextSource.EventWorktree,
                    await EventRunAsync(chosen, liveRuns, notes, cancellationToken).ConfigureAwait(false));
            }
        }

        string? cwd = null;
        if (TextOrNull(request.EventCwd) is { } eventCwd
            && await TryCandidateAsync(
                    eventCwd, "the payload's cwd", root, common, worktrees, notes, cancellationToken)
                .ConfigureAwait(false) is { } cwdWorktree)
        {
            cwd = cwdWorktree;
        }

        bool cwdIsCanonical = cwd is not null && canonical is not null && IsSame(cwd, canonical);
        if (cwd is not null && !cwdIsCanonical)
        {
            if (!string.IsNullOrWhiteSpace(request.LeadingCdTarget))
            {
                notes.Add($"the command's leading cd to '{request.LeadingCdTarget}' was ignored: the payload's cwd '{cwd}' already places the call.");
            }

            if (liveRuns.Count == 0)
            {
                return (cwd, HookRunContextSource.PayloadCwd, null);
            }

            if (await RunOfAsync(cwd, liveRuns, notes, cancellationToken).ConfigureAwait(false) is { } cwdRun)
            {
                notes.Add($"the payload's cwd '{cwd}' belongs to the live run for branch '{cwdRun.Branch}'.");
                return (cwd, HookRunContextSource.PayloadCwd, cwdRun);
            }

            return Ambiguous(canonical ?? root, liveRuns, $"the payload's cwd '{cwd}' belongs to no single live run", notes);
        }

        if (await LeadingCdCandidateAsync(
                request, root, common, worktrees, liveRuns, canonical, notes, cancellationToken)
            .ConfigureAwait(false) is { } fromLeadingCd)
        {
            return fromLeadingCd;
        }

        if (liveRuns.Count == 1)
        {
            RunRecord single = liveRuns[0];
            notes.Add($"the live run for branch '{single.Branch}' works in '{single.Worktree}'.");
            return (single.Worktree, HookRunContextSource.ActiveRun, single);
        }

        if (liveRuns.Count > 1)
        {
            return Ambiguous(canonical ?? root, liveRuns, "no signal chooses one of them", notes);
        }

        if (cwd is not null)
        {
            return (cwd, HookRunContextSource.PayloadCwd, null);
        }

        notes.Add($"the payload names no worktree of this repository; reading paths from '{root}'.");
        return (root, worktrees.Count > 0 && IsSame(root, worktrees[0])
            ? HookRunContextSource.PayloadCwd
            : HookRunContextSource.None, null);
    }

    private async Task<(string Worktree, HookRunContextSource Source, RunRecord? Run)?> LeadingCdCandidateAsync(
        HookRunContextRequest request,
        string root,
        string? common,
        IReadOnlyList<string> worktrees,
        IReadOnlyList<RunRecord> liveRuns,
        string? canonical,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (TextOrNull(request.LeadingCdTarget) is not { } leadingCdTarget
            || await TryCandidateAsync(
                    leadingCdTarget, "the command's leading cd", root, common, worktrees, notes, cancellationToken)
                .ConfigureAwait(false) is not { } cdWorktree
            || (canonical is not null && IsSame(cdWorktree, canonical)))
        {
            return null;
        }

        if (await RunOfAsync(cdWorktree, liveRuns, notes, cancellationToken).ConfigureAwait(false) is { } cdRun)
        {
            notes.Add($"the command's leading cd enters '{cdWorktree}', where the live run for branch '{cdRun.Branch}' works.");
            return (cdWorktree, HookRunContextSource.EventWorktree, cdRun);
        }

        notes.Add($"the command's leading cd enters '{cdWorktree}', which belongs to no single live run; it was not used.");
        return null;
    }

    private async Task<RunRecord?> EventRunAsync(
        string worktree,
        IReadOnlyList<RunRecord> liveRuns,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (liveRuns.Count == 0)
        {
            return null;
        }

        if (await RunOfAsync(worktree, liveRuns, notes, cancellationToken).ConfigureAwait(false) is { } mapped)
        {
            return mapped;
        }

        if (liveRuns.Count == 1)
        {
            return liveRuns[0];
        }

        notes.Add($"worktree '{worktree}' belongs to no single one of the {liveRuns.Count} live runs; no run is active.");
        return null;
    }

    private async Task<RunRecord?> RunOfAsync(
        string worktree,
        IReadOnlyList<RunRecord> liveRuns,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        RunRecord? containing = liveRuns
            .Where(run => IsSame(run.Worktree, worktree) || IsWithin(run.Worktree, worktree))
            .OrderByDescending(run => run.Worktree.Length)
            .FirstOrDefault();
        if (containing is not null)
        {
            return containing;
        }

        string? branch = await BranchOfAsync(worktree, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            notes.Add($"worktree '{worktree}' has no branch git can read, so no run's branch can be its ancestor.");
            return null;
        }

        List<RunRecord> ancestors = [];
        foreach (RunRecord run in liveRuns)
        {
            bool? ancestor = await AncestorOfAsync(worktree, run.Branch, branch, cancellationToken).ConfigureAwait(false);
            if (ancestor is null)
            {
                notes.Add($"git could not tell whether '{run.Branch}' is an ancestor of '{branch}' in '{worktree}'.");
                return null;
            }

            if (ancestor.Value)
            {
                ancestors.Add(run);
            }
        }

        return ancestors.Count == 1 ? ancestors[0] : null;
    }

    private async Task<string?> TryCandidateAsync(
        string candidate,
        string name,
        string root,
        string? common,
        IReadOnlyList<string> worktrees,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (!TryFullPath(candidate, BaseFor(candidate), out string full, notes, name))
        {
            return null;
        }

        string? containing = Containing(worktrees.Skip(1), full) ?? Containing(worktrees.Take(1), full);
        if (containing is not null)
        {
            string? candidateCommon = await CommonDirectoryOfAsync(full, cancellationToken).ConfigureAwait(false);
            if (common is not null && candidateCommon is null)
            {
                notes.Add($"{name} '{full}': git could not name its repository, so it was refused rather than trusted.");
                return null;
            }

            if (common is null || (candidateCommon is not null && IsSame(candidateCommon, common)))
            {
                return containing;
            }

            notes.Add($"{name} '{full}' belongs to another repository; it was refused.");
            return null;
        }

        notes.Add($"{name} '{full}' is outside this repository and its worktrees; it was refused.");
        return null;
    }

    private async Task<string?> CommonDirectoryOfAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            return await git.GetCommonDirectoryAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }
    }

    private async Task<string?> BranchOfAsync(string worktree, CancellationToken cancellationToken)
    {
        try
        {
            return await branches.ReadAsync(worktree, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }
    }

    private async Task<bool?> AncestorOfAsync(
        string worktree,
        string ancestor,
        string descendant,
        CancellationToken cancellationToken)
    {
        try
        {
            return await branches.IsAncestorAsync(worktree, ancestor, descendant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }
    }
}

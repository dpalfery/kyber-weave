using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Resolves one hook invocation's run context over the product's seams. The order is fixed: the worktree the event
/// names, then the live run record <c>run start</c> wrote, then the payload's <c>cwd</c>, then the process directory.
/// Every candidate must belong to this repository or one of its own git worktrees.
/// </summary>
/// <remarks>
/// No member throws on a missing or unreadable input. A candidate that cannot be validated, and a plan or task file
/// that cannot be read, adds a note naming the reason and the path tried, so a guard can escalate with the precise
/// cause instead of denying on an absence.
/// </remarks>
/// <param name="git">Names the repository's common directory and its worktrees.</param>
/// <param name="planPaths">Resolves the dispatch's plan inside a candidate worktree.</param>
/// <param name="planRoots">Chooses the worktree that holds the plan among the validated candidates.</param>
/// <param name="files">Checks whether a candidate directory, plan or task file exists.</param>
/// <param name="paths">Compares path spellings and keeps a candidate inside the repository.</param>
/// <param name="environment">The process directory a payload with no directory falls back to.</param>
/// <param name="runStates">Reads the records <c>run start</c> wrote.</param>
public sealed class HookRunContextResolver(
    IGitRepositoryFacts git,
    IPlanFileResolver planPaths,
    IPlanRootResolver planRoots,
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment,
    IRunStateStore runStates) : IHookRunContextResolver
{
    /// <inheritdoc />
    public HookRunContext Resolve(HookRunContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> notes = [];

        (string root, string? common) = Anchor(request, notes);
        IReadOnlyList<string> worktrees = Worktrees(root, common, notes);
        RunRecord? activeRun = ActiveRun(request.HomeDirectory, common, worktrees, notes);

        (string worktree, HookRunContextSource source) = Candidate(request, root, common, worktrees, activeRun, notes);
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

    /// <summary>The repository anchor: the first candidate git names a common directory for.</summary>
    private (string Root, string? Common) Anchor(HookRunContextRequest request, List<string> notes)
    {
        foreach ((string? candidate, string name) in new[]
                 {
                     (request.WorkspaceRoot, "the payload's workspace root"),
                     (request.EventCwd, "the payload's cwd"),
                     (request.ProcessDirectory, "the process directory"),
                     (environment.CurrentDirectory, "the process directory"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!TryFullPath(candidate!, candidate!, out string full, notes, name))
            {
                continue;
            }

            string? common = CommonDirectoryOf(full);
            if (common is not null)
            {
                return (full, common);
            }

            notes.Add($"{name} '{full}' names no repository.");
        }

        string fallback = FullPathOrRaw(request.ProcessDirectory ?? environment.CurrentDirectory);
        notes.Add($"no candidate names a repository; reading paths from '{fallback}' and letting the rules decide on the absence.");
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

    /// <summary>The newest live run record for this repository whose worktree still exists.</summary>
    private RunRecord? ActiveRun(
        string home,
        string? common,
        IReadOnlyList<string> worktrees,
        List<string> notes)
    {
        if (common is null || string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        IReadOnlyList<RunRecord>? records;
        try
        {
            records = runStates.ReadRecords(home);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            notes.Add($"the run records could not be read ({exception.Message}).");
            return null;
        }

        if (records is null)
        {
            notes.Add($"the run records under '{home}' could not be read.");
            return null;
        }

        RunRecord? newest = null;
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

            if (newest is null || Later(record, newest))
            {
                newest = record;
            }
        }

        return newest;
    }

    /// <summary>The worktree the event names, else the active run's, else the payload's cwd.</summary>
    private (string Worktree, HookRunContextSource Source) Candidate(
        HookRunContextRequest request,
        string root,
        string? common,
        IReadOnlyList<string> worktrees,
        RunRecord? activeRun,
        List<string> notes)
    {
        string? plan = PlanFileOf(request.Prompt);

        if (!string.IsNullOrWhiteSpace(request.AgentWorktree))
        {
            if (TryCandidate(request.AgentWorktree!, "the agent's working directory", root, common, worktrees, notes, out string agentWorktree))
            {
                return (agentWorktree, HookRunContextSource.EventWorktree);
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
                return (chosen, HookRunContextSource.EventWorktree);
            }
        }

        if (activeRun is not null)
        {
            notes.Add($"the live run for branch '{activeRun.Branch}' works in '{activeRun.Worktree}'.");
            return (activeRun.Worktree, HookRunContextSource.ActiveRun);
        }

        if (!string.IsNullOrWhiteSpace(request.EventCwd)
            && TryCandidate(request.EventCwd!, "the payload's cwd", root, common, worktrees, notes, out string cwd))
        {
            return (cwd, HookRunContextSource.PayloadCwd);
        }

        notes.Add($"the payload names no worktree of this repository; reading paths from '{root}'.");
        return (root, worktrees.Count > 0 && IsSame(root, worktrees[0])
            ? HookRunContextSource.PayloadCwd
            : HookRunContextSource.None);
    }

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
        if (!TryFullPath(candidate, root, out string full, notes, name))
        {
            return false;
        }

        string? containing = worktrees
            .OrderByDescending(worktree => worktree.Length)
            .FirstOrDefault(worktree => IsSame(worktree, full) || IsWithin(worktree, full));
        if (containing is not null)
        {
            string? candidateCommon = CommonDirectoryOf(full);
            if (common is null || candidateCommon is null || IsSame(candidateCommon, common))
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
        if (block.Headers.TryGetValue("PLAN_FILE", out string? valid))
        {
            return valid;
        }

        return block.RawValues.TryGetValue("PLAN_FILE", out string? raw) ? raw : null;
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

    private bool IsValidated(string candidate, string root, IReadOnlyList<string> worktrees)
    {
        string? full = FullPathOrNull(candidate, root);
        return full is not null
            && worktrees.Any(worktree => IsSame(worktree, full) || IsWithin(worktree, full));
    }

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

    private static bool Later(RunRecord candidate, RunRecord current) =>
        (candidate.StartedAt ?? DateTimeOffset.MinValue) > (current.StartedAt ?? DateTimeOffset.MinValue);
}

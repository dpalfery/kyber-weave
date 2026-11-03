using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>A task gate with its argv expanded for one run, ready to start.</summary>
/// <param name="Id">Slug identifying the gate within its agent.</param>
/// <param name="Blocking">Whether a failing verify command blocks the change.</param>
/// <param name="TimeoutSeconds">Seconds before the runner kills the process tree.</param>
/// <param name="Fix">The expanded fix command; empty when the gate declares none.</param>
/// <param name="Run">The expanded verify command.</param>
/// <param name="WorkingDirectory">Repository-relative directory the gate runs in; null means the repository root.</param>
public sealed record PlannedTaskGate(
    string Id,
    bool Blocking,
    int TimeoutSeconds,
    IReadOnlyList<string> Fix,
    IReadOnlyList<string> Run,
    string? WorkingDirectory = null)
{
    /// <summary>Whether the verify command also fails when it exits zero but prints to standard output.</summary>
    public bool FailOnOutput { get; init; }
}

/// <summary>A task gate that does not run for this change, and why.</summary>
/// <param name="Id">The gate's slug.</param>
/// <param name="Reason">A sentence the operator can act on.</param>
public sealed record SkippedTaskGate(string Id, string Reason);

/// <summary>What one agent's task gates resolve to for one changed set.</summary>
/// <param name="Gates">The gates that will start, in declaration order.</param>
/// <param name="Skipped">The gates that do not apply to this host or to this change.</param>
/// <param name="Refusals">
/// Reasons the run must not start at all. A non-empty list means no gate process is started.
/// </param>
public sealed record TaskGatePlan(
    IReadOnlyList<PlannedTaskGate> Gates,
    IReadOnlyList<SkippedTaskGate> Skipped,
    IReadOnlyList<string> Refusals);

/// <summary>The outcome of running planned task gates.</summary>
/// <param name="Report">The gate report, in the same schema as the repository-wide gates.</param>
/// <param name="FixFailures">Fix commands that did not succeed. They are warnings, not verdicts.</param>
public sealed record TaskGateRun(GateReport Report, IReadOnlyList<GateResult> FixFailures);

/// <summary>
/// Selects and expands one developer agent's task gates for one change.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place a <c>{changed}</c> or <c>{task}</c> token becomes a value. Both are
/// substituted into argv elements, never into a command line, so a changed file name cannot
/// introduce an option or a second command. Paths are checked before expansion: an absolute
/// path, a <c>..</c> escape, a leading <c>-</c>, or a symbolic link leading outside the root refuses the whole run.
/// </para>
/// <para>
/// Every decision here is made before a process starts. A refusal leaves the caller with nothing
/// to run, which is the property the unsafe-input tests pin.
/// </para>
/// </remarks>
public static class TaskGateSelection
{
    private const string ChangedToken = "{changed}";
    private const string TaskToken = "{task}";

    /// <summary>
    /// Refuses a <c>--task</c> value that is not a lookup name, because it is substituted into argv
    /// and may become part of a path a gate writes to.
    /// </summary>
    /// <returns>The reason it is refused, or <see langword="null"/> when the value is acceptable.</returns>
    public static string? ValidateTask(string? task)
    {
        if (task is null || ConfigSlug.IsValid(task))
            return null;

        return $"--task '{task}' is not a valid lookup name. Use lowercase alphanumerics separated by " +
               "single hyphens, e.g. 't2r-probe'.";
    }

    /// <summary>
    /// Checks one changed path, whether given by <c>--paths</c> or listed by git, before it can reach argv.
    /// </summary>
    /// <param name="root">The repository root the path must stay inside.</param>
    /// <param name="path">The repository-relative path to check.</param>
    /// <returns>The reason it is refused, or <see langword="null"/> when the path is safe to pass on.</returns>
    public static string? ValidatePath(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (string.IsNullOrWhiteSpace(path))
            return "Changed path is empty.";

        if (path.StartsWith('-'))
            return $"Changed path '{path}' starts with '-' and would be read as an option.";

        string normalized = Normalize(path);
        if (normalized.StartsWith('/') || Path.IsPathRooted(path))
            return $"Changed path '{path}' is absolute. Name it relative to the repository root.";

        if (normalized.Split('/').Contains(".."))
            return $"Changed path '{path}' leaves the repository root.";

        // The segment check above catches the spelling; GetRelativePath catches what remains,
        // such as a path that resolves outside the root through a platform-specific form.
        string full = Path.GetFullPath(Path.Combine(root, normalized));
        string relative = Path.GetRelativePath(root, full);
        if (relative == ".")
            return $"Changed path '{path}' names the repository root, not a file.";

        if (!IsWithin(root, full))
            return $"Changed path '{path}' leaves the repository root.";

        if (LeadsOutsideThroughSymlink(root, path))
            return $"Changed path '{path}' passes through a symbolic link that leads outside the repository root.";

        return null;
    }

    /// <summary>
    /// Resolves the agent's gates against the changed set and the host's operating system.
    /// </summary>
    /// <param name="gates">The agent's gates, in declaration order.</param>
    /// <param name="changed">The repository-relative changed paths, already validated with <see cref="ValidatePath"/>.</param>
    /// <param name="task">The validated <c>--task</c> slug, or <see langword="null"/> when none was given.</param>
    /// <param name="currentOs"><c>windows</c> or <c>unix</c>, for the gate's <c>os</c> filter.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="currentOs"/> is neither <c>windows</c> nor <c>unix</c>. Any other host value would
    /// be read as unix, so it is refused rather than guessed at.
    /// </exception>
    public static TaskGatePlan Plan(
        IReadOnlyList<ReviewTaskGate> gates,
        IReadOnlyList<string> changed,
        string? task,
        string currentOs)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentOs);
        if (currentOs is not ("windows" or "unix"))
            throw new ArgumentException($"currentOs must be 'windows' or 'unix', not '{currentOs}'.", nameof(currentOs));

        // Matching uses the separator-normalized form. What reaches argv is the path as it was given,
        // because on unix a backslash is part of a file name and rewriting it would name another file.
        // Windows reads either separator, so there argv keeps the forward-slash form it has always had.
        bool windows = currentOs == "windows";
        (string Argv, string Match)[] candidates = changed
            .Select(path => (Argv: windows ? Normalize(path) : path, Match: Normalize(path)))
            .ToArray();
        List<PlannedTaskGate> planned = [];
        List<SkippedTaskGate> skipped = [];
        List<string> refusals = [];

        foreach (ReviewTaskGate gate in gates)
        {
            // A gate with no program has nothing to start. The loader refuses this where the operator wrote it;
            // a caller that builds the record directly is refused here by name, and the other gates still plan.
            if (gate.Run.Count == 0)
            {
                refusals.Add(
                    $"Task gate '{gate.Id}' run has no command. Declare it as a list: run: [dotnet, format, KyberWeave.sln].");
                continue;
            }

            // A command that is a changed file would execute the file, so it is refused whether or not
            // the gate applies to this change. It is a defect in the configuration, not in the change.
            if (gate.Run[0] == ChangedToken || (gate.Fix.Count > 0 && gate.Fix[0] == ChangedToken))
            {
                refusals.Add(
                    $"Task gate '{gate.Id}' uses {{changed}} as its program. Name the command, and pass " +
                    "the files as arguments.");
                continue;
            }

            if (gate.Os is not null && gate.Os != currentOs)
            {
                skipped.Add(new SkippedTaskGate(gate.Id, $"it runs only on {gate.Os}, and this host is {currentOs}"));
                continue;
            }

            string[] matches = candidates
                .Where(path => gate.Include.Count == 0 || gate.Include.Any(pattern => PathGlob.IsMatch(pattern, path.Match)))
                .Select(path => path.Argv)
                .ToArray();
            if (matches.Length == 0)
            {
                skipped.Add(new SkippedTaskGate(gate.Id, "no changed file matches its include"));
                continue;
            }

            IReadOnlyList<string>? run = Expand(gate.Id, gate.Run, matches, task, refusals);
            IReadOnlyList<string>? fix = gate.Fix.Count == 0
                ? []
                : Expand(gate.Id, gate.Fix, matches, task, refusals);

            // A gate whose argv still names the {task} token is suppressed: the caller must
            // never start a process that writes to a literal {task} directory shared by
            // every worker. The refusal above names the gate; nothing planned names it.
            if (run is null || fix is null)
            {
                continue;
            }

            planned.Add(new PlannedTaskGate(gate.Id, gate.Blocking, gate.TimeoutSeconds, fix, run,
                gate.WorkingDirectory)
            {
                FailOnOutput = gate.FailOnOutput
            });
        }

        return new TaskGatePlan(planned, skipped, refusals.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <returns>
    /// The expanded argv, or <see langword="null"/> when <paramref name="task"/> is
    /// <see langword="null"/> and an element names the <c>{task}</c> token. A
    /// <see langword="null"/> return suppresses the gate: the caller plans nothing for it.
    /// </returns>
    private static IReadOnlyList<string>? Expand(
        string gateId,
        IReadOnlyList<string> argv,
        IReadOnlyList<string> matches,
        string? task,
        List<string> refusals)
    {
        List<string> expanded = new(argv.Count + matches.Count);
        foreach (string element in argv)
        {
            if (element == ChangedToken)
            {
                expanded.AddRange(matches);
                continue;
            }

            if (element.Contains(TaskToken, StringComparison.Ordinal))
            {
                if (task is null)
                {
                    refusals.Add(
                        $"Task gate '{gateId}' uses {{task}}, but no --task was given. Pass --task <slug> " +
                        "naming the task.");
                    return null;
                }

                expanded.Add(element.Replace(TaskToken, task, StringComparison.Ordinal));
                continue;
            }

            expanded.Add(element);
        }

        return expanded;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> or lies beneath it. Both must be full paths.
    /// </summary>
    internal static bool IsWithin(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith("../", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the file, or a directory above it, is a symbolic link whose target lies outside <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each prefix of the path is checked as its own entry, outermost first, so a link in the middle of the path is
    /// found even when the final file is an ordinary one. The walk splits on the separators this host reads, so it
    /// inspects the same entries the gate would open: a backslash is a separator on Windows and a name character elsewhere.
    /// </para>
    /// <para>
    /// A missing entry is not a link and is not an error here, so a deleted file passes. A link that cannot be
    /// resolved, such as a cycle, is treated as escaping, because it cannot be shown to stay inside the root.
    /// Only the link's target is read; nothing behind it is opened.
    /// </para>
    /// </remarks>
    internal static bool LeadsOutsideThroughSymlink(string root, string path)
    {
        string full = Path.GetFullPath(root);
        string current = full;
        string[] segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not { } linkTarget)
                continue;

            try
            {
                FileSystemInfo? resolved = entry.ResolveLinkTarget(returnFinalTarget: true);
                string target = resolved?.FullName
                    ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, linkTarget));
                if (!IsWithin(full, target))
                    return true;
            }
            catch (IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }
}

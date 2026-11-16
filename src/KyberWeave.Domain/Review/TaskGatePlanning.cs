using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>
/// Selects and expands one developer agent's task gates for one change.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place a <c>{changed}</c>, <c>{task}</c> or <c>{tests}</c> token becomes a value. All
/// three are substituted into argv elements, never into a command line, so a changed file name cannot
/// introduce an option or a second command. Paths are checked before expansion: an absolute
/// path, a <c>..</c> escape, a leading <c>-</c>, or a symbolic link leading outside the root refuses the whole run.
/// </para>
/// <para>
/// Every decision here is made before a process starts. A refusal leaves the caller with nothing
/// to run, which is the property the unsafe-input tests pin.
/// </para>
/// </remarks>
public static class TaskGatePlanning
{
    private const string ChangedToken = "{changed}";
    private const string TaskToken = "{task}";
    private const string TestsToken = "{tests}";
    public const int MaxTestClassesInFilter = 100;

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
    /// <param name="leadsOutsideThroughSymlink">Whether the path, or a directory above it, is a symbolic link leading outside the root.</param>
    public static string? ValidatePath(string root, string path, Func<string, string, bool> leadsOutsideThroughSymlink)
    {
        ArgumentNullException.ThrowIfNull(leadsOutsideThroughSymlink);
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

        if (leadsOutsideThroughSymlink(root, path))
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
    /// <param name="root">
    /// The repository root the <c>{tests}</c> token resolves its class files under, or <see langword="null"/>
    /// to use the nearest enclosing repository of this process.
    /// </param>
    /// <param name="declaredClasses">
    /// Test classes the task's plan names in its Test contract. They fill <c>{tests}</c> ahead of the classes looked up
    /// from the changed files.
    /// </param>
    /// <param name="rootAccess">What the caller found when it probed <paramref name="root"/>; null when none was probed.</param>
    /// <param name="resolveTests">Resolves the test classes that cover the matched files, for <c>{tests}</c>; null resolves none.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="currentOs"/> is neither <c>windows</c> nor <c>unix</c>. Any other host value would
    /// be read as unix, so it is refused rather than guessed at.
    /// </exception>
    public static TaskGatePlan Plan(
        IReadOnlyList<ReviewTaskGate> gates,
        IReadOnlyList<string> changed,
        string? task,
        string currentOs,
        string? root = null,
        IReadOnlyList<string>? declaredClasses = null,
        RepositoryRootAccess? rootAccess = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? resolveTests = null)
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

        if (root is not null && rootAccess is not null)
        {
            if (!rootAccess.Exists)
                refusals.Add($"The repository root '{root}' does not exist or is not a directory.");
            else if (rootAccess.AccessError is not null)
                refusals.Add($"The repository root '{root}' is not accessible: {rootAccess.AccessError}");

            if (refusals.Count > 0)
                return new TaskGatePlan([], skipped, refusals);
        }

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

            // The same for a class list where the program belongs: the source tree would then choose what runs.
            if (NamesTestsInProgram(gate))
            {
                refusals.Add(
                    $"Task gate '{gate.Id}' uses {{tests}} as its program. Name the command, and pass the " +
                    "test classes as arguments.");
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

            // Nothing mapped is a skip, not an empty filter: a class-less filter exits zero, and would be
            // read afterwards as a suite that ran and passed.
            string testsFilter = string.Empty;
            if (NamesTests(gate))
            {
                IReadOnlyList<string> classes;
                try
                {
                    // Classes a plan declares come first: in test-first work they may not exist yet, so a lookup from
                    // the changed files would miss them. The mapped classes still run, so existing coverage is kept.
                    classes = [.. (declaredClasses ?? []).Concat((resolveTests?.Invoke(matches) ?? [])).Distinct(StringComparer.Ordinal)];
                }
                catch (Exception ex) when (ex is InvalidOperationException)
                {
                    refusals.Add(ex.Message);
                    return new TaskGatePlan([], skipped, refusals);
                }

                if (classes.Count == 0)
                {
                    skipped.Add(new SkippedTaskGate(
                        gate.Id,
                        "no test class covers the changed files, so its filter would select nothing"));
                    continue;
                }

                if (classes.Count > MaxTestClassesInFilter)
                {
                    refusals.Add(
                        $"Task gate '{gate.Id}' resolved {classes.Count} test classes, exceeding the filter limit of {MaxTestClassesInFilter}. Run a scoped check instead.");
                    continue;
                }

                testsFilter = TestsFilter(classes);
            }

            IReadOnlyList<string>? run = Expand(gate.Id, gate.Run, matches, task, testsFilter, refusals);
            IReadOnlyList<string>? fix = gate.Fix.Count == 0
                ? []
                : Expand(gate.Id, gate.Fix, matches, task, testsFilter, refusals);

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
        string testsFilter,
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

            string current = element;
            if (current.Contains(TaskToken, StringComparison.Ordinal))
            {
                if (task is null)
                {
                    refusals.Add(
                        $"Task gate '{gateId}' uses {{task}}, but no --task was given. Pass --task <slug> " +
                        "naming the task.");
                    return null;
                }

                current = current.Replace(TaskToken, task, StringComparison.Ordinal);
            }

            if (UsesTestsToken(current))
            {
                current = current.Replace(TestsToken, testsFilter, StringComparison.Ordinal);
            }

            expanded.Add(current);
        }

        return expanded;
    }

    /// <summary>
    /// The filter that selects the resolved classes: one <c>FullyQualifiedName~</c> clause per class,
    /// joined by <c>|</c>, which is the form <c>dotnet test --filter</c> reads as "any of these".
    /// </summary>
    /// <remarks>
    /// The operator is <c>~</c> (contains) rather than <c>=</c> because VSTest test names include
    /// the method name (such as <c>Namespace.Class.Method</c>), so an exact equality filter against a
    /// class name matches zero tests. The prefix is spelled out on every clause because <c>|</c>
    /// binds to the whole expression: a bare name after it would be a syntax error rather than a
    /// second selector.
    /// </remarks>
    private static string TestsFilter(IReadOnlyList<string> classes)
    {
        return string.Join('|', classes.Select(name => "FullyQualifiedName~" + name));
    }

    private static bool NamesTests(ReviewTaskGate gate)
    {
        return gate.Run.Any(UsesTestsToken) || gate.Fix.Any(UsesTestsToken);
    }

    private static bool NamesTestsInProgram(ReviewTaskGate gate)
    {
        return UsesTestsToken(gate.Run[0]) || (gate.Fix.Count > 0 && UsesTestsToken(gate.Fix[0]));
    }

    private static bool UsesTestsToken(string element)
    {
        return element.Contains(TestsToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> or lies beneath it. Both must be full paths.
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith("../", StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }
}

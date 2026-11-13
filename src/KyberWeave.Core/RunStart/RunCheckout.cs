namespace KyberWeave.Core.RunStart;

/// <summary>One run <c>run start</c> recorded: the repository, its branch, and where it works.</summary>
/// <param name="Repository">The repository's common git directory, absolute.</param>
/// <param name="Branch">The run's branch.</param>
/// <param name="Worktree">The directory the run works in.</param>
public sealed record RunRecord(string Repository, string Branch, string Worktree);

/// <summary>
/// Where a live run's work belongs, from machine facts only: the run records under the operator's home and
/// the files git keeps in each checkout. Read by the canonical-checkout write guard on every write tool call,
/// so it starts no process: the branch a worktree has checked out is read from its <c>HEAD</c> file.
/// </summary>
/// <remarks>
/// The canonical checkout is the repository's main worktree, the directory that holds the common git
/// directory. A run that branched in place works in the canonical checkout itself, so nothing it writes there
/// is stray. Every unreadable fact answers "no live run", which allows.
/// </remarks>
public static class RunCheckout
{
    private const string GitEntry = ".git";
    private const string HeadRefPrefix = "ref: refs/heads/";
    private const int MaxLinkDepth = 16;

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>The canonical checkout of a common git directory: its parent when it is a <c>.git</c> directory, else null.</summary>
    public static string? CanonicalCheckoutOf(string commonDirectory)
    {
        if (string.IsNullOrWhiteSpace(commonDirectory))
        {
            return null;
        }

        string normalized = RealPath(commonDirectory);
        return string.Equals(Path.GetFileName(normalized), GitEntry, StringComparison.Ordinal)
            ? Path.GetDirectoryName(normalized)
            : null;
    }

    /// <summary>
    /// The live runs of the repository whose canonical checkout is <paramref name="canonicalCheckout" /> that
    /// work in a worktree of their own: the record's worktree exists, is not the canonical checkout, and still
    /// has the run's branch checked out.
    /// </summary>
    /// <returns>The runs, or null when the run records could not be read.</returns>
    public static IReadOnlyList<RunRecord>? LiveSeparateRuns(string homeDirectory, string canonicalCheckout)
    {
        IReadOnlyList<RunRecord>? records = RunBaseState.ReadRecords(homeDirectory);
        if (records is null)
        {
            return null;
        }

        string canonical = RealPath(canonicalCheckout);
        List<RunRecord> live = [];
        foreach (RunRecord record in records)
        {
            string? recordCanonical = CanonicalCheckoutOf(record.Repository);
            if (recordCanonical is null || !SamePath(recordCanonical, canonical))
            {
                continue;
            }

            string worktree = RealPath(record.Worktree);
            if (SamePath(worktree, canonical) || !Directory.Exists(worktree))
            {
                continue;
            }

            if (string.Equals(ReadCheckedOutBranch(worktree), record.Branch, StringComparison.Ordinal))
            {
                live.Add(record with { Worktree = worktree });
            }
        }

        return live;
    }

    /// <summary>
    /// The refusal for a write to <paramref name="path" />, or null to allow. A write is refused only when it
    /// lands inside a repository's canonical checkout while a live run of that repository works in a worktree
    /// of its own. A path in a linked worktree, outside every repository, or inside the git directory is allowed.
    /// </summary>
    /// <param name="path">The path the tool writes, absolute or relative to <paramref name="workingDirectory" />.</param>
    /// <param name="workingDirectory">The tool call's working directory.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records.</param>
    public static string? StrayWriteRefusal(string path, string workingDirectory, string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(homeDirectory))
        {
            return null;
        }

        string combined = Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(workingDirectory)
            ? path
            : Path.Combine(workingDirectory, path);
        if (!Path.IsPathRooted(combined))
        {
            return null;
        }

        string target = RealPath(combined);
        string? checkout = EnclosingMainCheckout(target);
        if (checkout is null)
        {
            return null;
        }

        string relative = Path.GetRelativePath(checkout, target).Replace('\\', '/');
        if (relative == "." || relative == GitEntry || relative.StartsWith(GitEntry + "/", StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyList<RunRecord>? runs = LiveSeparateRuns(homeDirectory, checkout);
        if (runs is not { Count: > 0 })
        {
            return null;
        }

        string suggestions = string.Join(
            " or ",
            runs.Select(run => $"'{Path.Combine(run.Worktree, relative)}' (run branch {run.Branch})"));
        return $"KW-ARB-CHECKOUT-001: '{target}' is in the repository's canonical checkout, but this run works in " +
               $"its own worktree. Write {suggestions} instead, and leave the canonical checkout untouched.";
    }

    /// <summary>
    /// The branch <paramref name="worktree" /> has checked out, read from its git files: a linked worktree's
    /// <c>.git</c> file names its git directory, a main checkout's <c>.git</c> directory holds <c>HEAD</c>.
    /// Null for a detached head or anything unreadable.
    /// </summary>
    public static string? ReadCheckedOutBranch(string worktree)
    {
        try
        {
            string entry = Path.Combine(worktree, GitEntry);
            string? gitDirectory = null;
            if (Directory.Exists(entry))
            {
                gitDirectory = entry;
            }
            else if (File.Exists(entry))
            {
                string line = File.ReadAllText(entry).Trim();
                const string prefix = "gitdir:";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string named = line[prefix.Length..].Trim();
                    gitDirectory = Path.IsPathRooted(named) ? named : Path.GetFullPath(Path.Combine(worktree, named));
                }
            }

            if (gitDirectory is null)
            {
                return null;
            }

            string head = File.ReadAllText(Path.Combine(gitDirectory, "HEAD")).Trim();
            return head.StartsWith(HeadRefPrefix, StringComparison.Ordinal) ? head[HeadRefPrefix.Length..] : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The main checkout that holds <paramref name="target" />: the nearest ancestor with a <c>.git</c> entry,
    /// when that entry is a directory. A <c>.git</c> file marks a linked worktree, whose writes are the run's.
    /// </summary>
    private static string? EnclosingMainCheckout(string target)
    {
        string? directory = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
        while (!string.IsNullOrEmpty(directory))
        {
            string entry = Path.Combine(directory, GitEntry);
            if (Directory.Exists(entry))
            {
                return directory;
            }

            if (File.Exists(entry))
            {
                return null;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left.TrimEnd('/', '\\'), right.TrimEnd('/', '\\'), PathComparison);

    /// <summary>
    /// The absolute path with every symbolic link on the way resolved, so <c>/var</c> and <c>/private/var</c>
    /// compare equal. Components that do not exist yet are kept as written.
    /// </summary>
    internal static string RealPath(string path) => RealPath(path, 0);

    private static string RealPath(string path, int depth)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || depth > MaxLinkDepth)
        {
            return Path.TrimEndingDirectorySeparator(full);
        }

        string current = root;
        string[] parts = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < parts.Length; index++)
        {
            string candidate = Path.Combine(current, parts[index]);
            string? link = null;
            try
            {
                FileSystemInfo info = new FileInfo(candidate);
                if (!info.Exists)
                {
                    info = new DirectoryInfo(candidate);
                }

                if (!info.Exists)
                {
                    current = Path.Combine([candidate, .. parts[(index + 1)..]]);
                    break;
                }

                link = info.LinkTarget;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                link = null;
            }

            current = link is null
                ? candidate
                : RealPath(Path.IsPathRooted(link) ? link : Path.Combine(current, link), depth + 1);
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}

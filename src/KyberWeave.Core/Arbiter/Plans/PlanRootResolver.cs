using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.Arbiter.Plans;

/// <summary>
/// Finds the working tree a dispatch's plan lives in. A conductor run works in a git worktree of the repository,
/// and its plan and task file exist only there, while some harnesses run the hook in the main checkout. Reading the
/// plan, the task file and the git facts against the hook's own directory then finds no plan at all.
/// </summary>
/// <remarks>
/// Every input is a machine fact: whether a file exists under a root, and the repository's own worktree list. The
/// dispatch text is read only to pick among several worktrees that all hold the plan, and to place a return whose
/// headers were stripped. When nothing resolves, the hook's directory stands and the rules decide on the absence.
/// </remarks>
public static class PlanRootResolver
{
    /// <summary>
    /// The root to evaluate <paramref name="prompt"/> against: <paramref name="hookRoot"/> when the plan exists
    /// there or nothing better resolves, otherwise the one worktree that holds the plan.
    /// </summary>
    /// <param name="hookRoot">The repository root the hook resolved from its working directory.</param>
    /// <param name="prompt">The dispatch prompt, headers included when it still has them.</param>
    /// <param name="listWorktrees">Lists the repository's worktrees; <see cref="GitFacts.ListWorktrees"/> in production.</param>
    public static string Resolve(string hookRoot, string prompt, Func<string, IReadOnlyList<string>>? listWorktrees = null)
    {
        ArgumentNullException.ThrowIfNull(hookRoot);
        ArgumentNullException.ThrowIfNull(prompt);
        listWorktrees ??= root => GitFacts.ListWorktrees(root);

        HeaderBlock block = HeaderBlock.Parse(prompt);
        string? planFile = block.Headers.TryGetValue("PLAN_FILE", out string? valid)
            ? valid
            : block.RawValues.TryGetValue("PLAN_FILE", out string? raw) ? raw : null;
        if (planFile is not null && Holds(hookRoot, planFile))
        {
            return hookRoot;
        }

        // Without a plan the only signal is a worktree path in the text; skip the git call when none can be there.
        if (planFile is null && !prompt.Contains("worktree", StringComparison.OrdinalIgnoreCase))
        {
            return hookRoot;
        }

        IReadOnlyList<string> others = [.. listWorktrees(hookRoot)
            .Where(worktree => !SamePath(worktree, hookRoot) && Directory.Exists(worktree))];
        if (others.Count == 0)
        {
            return hookRoot;
        }

        if (planFile is null)
        {
            // A return whose headers were stripped: the worktree its body names, when exactly one is named.
            List<string> named = [.. others.Where(worktree => Names(prompt, worktree))];
            return named.Count == 1 ? named[0] : hookRoot;
        }

        List<string> holding = [.. others.Where(worktree => Holds(worktree, planFile))];
        if (holding.Count == 0)
        {
            return hookRoot;
        }

        if (holding.Count == 1)
        {
            return holding[0];
        }

        List<string> namedHolding = [.. holding.Where(worktree => Names(prompt, worktree))];
        if (namedHolding.Count == 1)
        {
            return namedHolding[0];
        }

        return holding
            .OrderByDescending(worktree => File.GetLastWriteTimeUtc(PlanPathResolver.Resolve(planFile, worktree)!))
            .First();
    }

    private static bool Holds(string root, string planFile)
    {
        try
        {
            return PlanPathResolver.Resolve(planFile, root) is { } path && File.Exists(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the text names the worktree: its path as git lists it, or a directory token that is the same working
    /// tree reached through another spelling (on macOS git lists <c>/private/var/…</c> for <c>/var/…</c>).
    /// </summary>
    private static bool Names(string text, string worktree)
    {
        if (NamesPath(text, worktree))
        {
            return true;
        }

        string? identity = GitFileOf(worktree);
        return identity is not null && text
            .Split([' ', '\t', '\n', '\r', '`', '"', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.TrimEnd('.', ',', ';', ':'))
            .Where(token => Path.IsPathRooted(token))
            .Any(token => string.Equals(GitFileOf(token), identity, StringComparison.Ordinal));
    }

    /// <summary>A linked worktree's <c>.git</c> file names its private git directory, which identifies it.</summary>
    private static string? GitFileOf(string directory)
    {
        try
        {
            string gitFile = Path.Combine(directory, ".git");
            return File.Exists(gitFile) ? File.ReadAllText(gitFile).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool NamesPath(string text, string worktree)
    {
        string path = worktree.TrimEnd('/', '\\');
        int index = text.IndexOf(path, StringComparison.Ordinal);
        while (index >= 0)
        {
            int end = index + path.Length;
            if (end == text.Length || text[end] is '/' or '\\' or ' ' or '\n' or '\r' or '\t' or '`' or '"' or '\'' or ')')
            {
                return true;
            }

            index = text.IndexOf(path, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

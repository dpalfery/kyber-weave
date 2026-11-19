using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.FileSystem;

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
/// <param name="git">The repository's own worktree list.</param>
/// <param name="planPaths">Resolves the dispatch's plan inside a root.</param>
/// <param name="files">The file system a candidate root is checked against.</param>
/// <param name="paths">Decides whether two spellings name the same root.</param>
public sealed class PlanRootResolver(
    IGitRepositoryFacts git,
    IPlanFileResolver planPaths,
    IFileSystem files,
    IPathResolver paths) : IPlanRootResolver
{
    /// <summary>
    /// The root to evaluate <paramref name="prompt"/> against: <paramref name="hookRoot"/> when the plan exists
    /// there or nothing better resolves, otherwise the one worktree that holds the plan.
    /// </summary>
    /// <param name="hookRoot">The repository root the hook resolved from its working directory.</param>
    /// <param name="prompt">The dispatch prompt, headers included when it still has them.</param>
    /// <param name="listWorktrees">Lists the repository's worktrees; the git adapter in production.</param>
    public string Resolve(string hookRoot, string prompt, Func<string, IReadOnlyList<string>>? listWorktrees = null)
    {
        ArgumentNullException.ThrowIfNull(hookRoot);
        ArgumentNullException.ThrowIfNull(prompt);

        HeaderBlock block = HeaderBlock.Parse(prompt);
        string? planFile = block.Headers.TryGetValue("PLAN_FILE", out string? valid)
            ? valid
            : block.RawValues.TryGetValue("PLAN_FILE", out string? raw) ? raw : null;
        return ResolvePlanRoot(hookRoot, planFile, prompt, listWorktrees);
    }

    /// <summary>
    /// The root to read a dispatch's plan, task file and prompt against when its headers are already parsed.
    /// </summary>
    /// <param name="hookRoot">The repository root the hook resolved from its working directory.</param>
    /// <param name="planFile">The dispatch's <c>PLAN_FILE</c> value, or null when it names no plan.</param>
    /// <param name="text">The dispatch text, for the worktree it names when no plan is named; null for none.</param>
    /// <param name="listWorktrees">Lists the repository's worktrees; the git adapter in production.</param>
    public string ResolvePlanRoot(
        string hookRoot,
        string? planFile,
        string? text,
        Func<string, IReadOnlyList<string>>? listWorktrees = null)
    {
        ArgumentNullException.ThrowIfNull(hookRoot);
        listWorktrees ??= root => git.ListWorktrees(root);
        text ??= string.Empty;

        if (planFile is not null && Holds(hookRoot, planFile))
        {
            return hookRoot;
        }

        // Without a plan the only signal is a path in the text; skip the git call when the text holds neither a path
        // nor the word, because a worktree need not live in a directory named after it.
        if (planFile is null && !text.Contains("worktree", StringComparison.OrdinalIgnoreCase) && !HoldsAPath(text))
        {
            return hookRoot;
        }

        // A git call that cannot answer, or a worktree entry that is no full path, is no worktree list: the
        // hook's own directory stands and the rules decide on the absence.
        IReadOnlyList<string> others;
        try
        {
            others = [.. listWorktrees(hookRoot).Where(worktree => IsOtherRoot(worktree, hookRoot))];
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return hookRoot;
        }

        if (others.Count == 0)
        {
            return hookRoot;
        }

        if (planFile is null)
        {
            // A return whose headers were stripped: the worktree its body names, when exactly one is named.
            List<string> named = [.. others.Where(worktree => Names(text, worktree))];
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

        List<string> namedHolding = [.. holding.Where(worktree => Names(text, worktree))];
        if (namedHolding.Count == 1)
        {
            return namedHolding[0];
        }

        try
        {
            return holding
                .OrderByDescending(worktree => files.GetLastWriteTimeUtc(planPaths.Resolve(planFile, worktree)!))
                .First();
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            // A copy whose metadata cannot be read cannot be compared with the others, so no copy is picked and
            // the hook's own directory stands.
            return hookRoot;
        }
    }

    /// <summary>Whether a listed worktree is another root this dispatch may be evaluated in.</summary>
    /// <remarks>An entry that is not a fully qualified path cannot be compared, so it is not one.</remarks>
    private bool IsOtherRoot(string worktree, string hookRoot)
    {
        try
        {
            return !paths.AreSame(worktree, hookRoot) && files.DirectoryExists(worktree);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool Holds(string root, string planFile)
    {
        try
        {
            return planPaths.Resolve(planFile, root) is { } path && files.FileExists(path);
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
    private bool Names(string text, string worktree)
    {
        if (NamesPath(text, worktree))
        {
            return true;
        }

        string? identity = GitFileOf(worktree);
        return identity is not null
            && Tokens(text).Any(token => Path.IsPathRooted(token)
                && string.Equals(GitFileOf(token), identity, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether the text carries a token that could name a worktree: a rooted path. The word "worktree" alone is not
    /// the signal — a checkout beside the repository, or one in a temporary directory, is named by its path only —
    /// so this is what decides whether the worktree list is worth the git call.
    /// </summary>
    private static bool HoldsAPath(string text) => Tokens(text).Any(Path.IsPathRooted);

    /// <summary>The words of a return's body, trimmed of the punctuation a sentence leaves on a path.</summary>
    private static IEnumerable<string> Tokens(string text) =>
        text.Split([' ', '\t', '\n', '\r', '`', '"', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.TrimEnd('.', ',', ';', ':'));

    /// <summary>A linked worktree's <c>.git</c> file names its private git directory, which identifies it.</summary>
    private string? GitFileOf(string directory)
    {
        try
        {
            string gitFile = Path.Combine(directory, ".git");
            return files.FileExists(gitFile) ? files.ReadAllText(gitFile).Trim() : null;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
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
}

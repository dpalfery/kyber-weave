namespace KyberWeave.Core.Docs.Mcp;

/// <summary>
/// Turns a repository-relative configuration file name into the absolute path it names, and answers
/// nothing when that path is not inside the repository.
/// </summary>
/// <remarks>
/// <para>
/// Requirement 2.6 confines this feature to the repository and 5.2 says a write lands in a
/// repository-scope file or nowhere, so "is this path inside the repository" has exactly one answer
/// in this code, and this is it. Both the check and the repair ask the question — the check to
/// decide which file to read, the repair to decide which file it may write — and when each answered
/// it in its own private helper the two answers drifted: one refused a name holding a parent
/// reference before resolving it, the other resolved first and accepted whatever stayed inside the
/// root. A repository does not want two answers to this question (ADR 0052), because the answer
/// decides whether a byte this repository does not own can be touched.
/// </para>
/// <para>
/// A path outside the root is <em>refused</em> rather than merely reported, and that is the whole
/// point of the type: a caller that receives a path has already been told the repository owns it, so
/// no caller needs a second test, and none can forget one. The cost is that a caller wanting to
/// report on a file outside the tree has to name it some other way — which is the direction the
/// boundary draws: this feature reads and writes the repository it was given, and a harness file
/// somewhere else is not a configuration this repository can claim.
/// </para>
/// <para>
/// Containment is a property of the resolved path, not of the spelling, so the name is checked and
/// then resolved and then checked again as a path. The spelling check is not redundant with the
/// resolved one: it refuses <c>../shared/mcp.json</c> by looking at the name, without asking the
/// filesystem anything, and it refuses a name this process cannot resolve at all — where resolution
/// alone would answer with whatever the platform made of it.
/// </para>
/// </remarks>
internal static class McpRepositoryPath
{
    /// <summary>
    /// The absolute path <paramref name="relativePath"/> names inside <paramref name="repositoryRoot"/>,
    /// or null when it names nothing this repository owns.
    /// </summary>
    /// <param name="repositoryRoot">The repository whose boundary the path must stay inside.</param>
    /// <param name="relativePath">
    /// The path as a catalog row or a receipt record spells it: repository-relative, with either
    /// separator.
    /// </param>
    /// <remarks>
    /// <para>
    /// The name rule is <see cref="McpWiringReceipt.RepositoryRelative"/> rather than a second copy of
    /// it, because a catalog row and a receipt record are refused by the same test: both are a
    /// relative name this code combines with a root, and one implementation of "may be combined with a
    /// root" is what keeps them from disagreeing about a name such as <c>..foo</c> — a file inside the
    /// tree whose name merely begins with the characters that spell a parent reference.
    /// </para>
    /// <para>
    /// A name the platform cannot resolve — one holding a character the filesystem forbids, or one
    /// longer than the platform allows — answers null rather than throwing. That is a refusal like any
    /// other: an operator needs a finding that names the path, not an exception out of a check that
    /// runs during <c>docs init</c>.
    /// </para>
    /// <para>
    /// The root is resolved inside the same guard as the name, and that placement is the rule rather
    /// than an accident of ordering. Both are strings a caller supplied, so both can name something
    /// the platform refuses to resolve, and a root is the likelier of the two to be the reason: it
    /// arrives from an argv flag or a configuration value, and it is combined with every name in the
    /// tree. Resolving it outside the guard meant the promise above held for the name and not for the
    /// root — a root the filesystem rejects threw out of the repair, the removal and the fix list
    /// before any one of them had a path to refuse, which is the exception this type exists to
    /// answer with a finding instead.
    /// </para>
    /// </remarks>
    public static string? Contained(string repositoryRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (!McpWiringReceipt.RepositoryRelative(relativePath))
            return null;

        string root;
        string full;

        try
        {
            root = Path.GetFullPath(repositoryRoot);

            full = Path.GetFullPath(
                Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // The separator is what makes the test a containment test: without it a sibling directory
        // whose name merely starts with the root's — /work/repo-other beside /work/repo — would pass
        // as being inside it.
        string prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, StringComparison.Ordinal) ? full : null;
    }
}

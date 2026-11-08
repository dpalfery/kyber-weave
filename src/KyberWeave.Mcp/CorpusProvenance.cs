using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;

namespace KyberWeave.Mcp;

/// <summary>
/// Formats the provenance header every docs tool leads with: the absolute corpus root, the
/// branch and git directory it belongs to, the short HEAD revision with a dirty marker, and the
/// live document count.
/// </summary>
/// <remarks>
/// <para>
/// The header is the tell that a same-named server is bound to a different checkout. A
/// mis-rooted process otherwise answers with the same confident prose as a correct one, so
/// the root and revision have to be visible on every response rather than inferred from a
/// document count alone (issue #162).
/// </para>
/// <para>
/// <c>branch=</c> and <c>repo=</c> are what separates the two ways a root can differ from the
/// caller's own. A second worktree of this repository has another root and the same git
/// directory; a foreign repository has another git directory. Comparing <c>root=</c> alone
/// reads the first case as the second, and refuses an answer that is in fact correct.
/// </para>
/// <para>
/// The revision and dirty marker are read through <see cref="ProcessRunner"/> and never
/// invented: a missing git, a directory that is not a repository, or a failed command all yield
/// <c>rev=unavailable</c> and <c>dirty=unknown</c>, which are success paths for the header.
/// The branch and git directory are read from the working tree's own metadata, so the header
/// answers even where git is not installed. Values are cached per root and invalidated when
/// either the host's corpus stamp changes or a cheap fingerprint of <c>.git/HEAD</c> and
/// <c>.git/index</c> moves, so a commit that does not touch documentation still refreshes
/// <c>rev</c>/<c>dirty</c> without shelling out on every tool call.
/// </para>
/// </remarks>
internal static class CorpusProvenance
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<string, CachedIdentity> Cache =
        new(StringComparer.Ordinal);

    /// <summary>What a field carries when the information could not be read. Never a real value.</summary>
    private const string Unavailable = "unavailable";

    /// <summary>What a detached HEAD is reported as, which is a real state rather than a failure.</summary>
    private const string Detached = "detached";

    /// <summary>The prefix git writes in <c>.git</c> when the checkout is a linked worktree.</summary>
    private const string GitDirPrefix = "gitdir:";

    /// <summary>The one-line provenance header for a response over <paramref name="documentCount"/> documents.</summary>
    /// <param name="repositoryRoot">The bound repository root; normalised with <see cref="Path.GetFullPath(string)"/>.</param>
    /// <param name="documentCount">The live document count for the corpus, or null if unavailable.</param>
    /// <param name="corpusStamp">The host's corpus stamp, used with the git fingerprint to expire the cache.</param>
    public static string Line(string repositoryRoot, int? documentCount, int corpusStamp)
    {
        string root = Path.GetFullPath(repositoryRoot);
        CachedIdentity identity = Identity(root, corpusStamp);
        string docs = documentCount is { } count
            ? count.ToString(CultureInfo.InvariantCulture)
            : Unavailable;
        return "provenance: root=" + root
            + " branch=" + identity.Branch
            + " repo=" + identity.Repository
            + " rev=" + identity.Revision
            + " dirty=" + identity.Dirty
            + " documents=" + docs;
    }

    private static CachedIdentity Identity(string root, int corpusStamp)
    {
        long gitStamp = ComputeGitStamp(root);
        if (Cache.TryGetValue(root, out CachedIdentity cached)
            && cached.CorpusStamp == corpusStamp
            && cached.GitStamp == gitStamp)
        {
            return cached;
        }

        (string branch, string repository) = ReadGitLocation(root);
        (string revision, string dirty) = ReadGitIdentity(root);
        CachedIdentity identity = new(corpusStamp, gitStamp, branch, repository, revision, dirty);
        Cache[root] = identity;
        return identity;
    }

    /// <summary>
    /// The branch this checkout is on and the git directory it shares with every other worktree
    /// of the same repository.
    /// </summary>
    /// <remarks>
    /// Both are read from the working tree's metadata rather than by running git, so the header
    /// costs no child process. The distinction the pair exists for is the one <c>root=</c> cannot
    /// make: a second worktree of this repository has a different root and the same git directory,
    /// while a foreign repository differs in both.
    /// </remarks>
    private static (string Branch, string Repository) ReadGitLocation(string root)
    {
        string? gitDirectory = GitDirectoryFor(root);
        if (gitDirectory is null) return (Unavailable, Unavailable);

        return (ReadBranch(Path.Combine(gitDirectory, "HEAD")), CommonGitDirectory(gitDirectory));
    }

    /// <summary>
    /// The directory holding this checkout's own git metadata: <c>.git</c> itself in a normal
    /// checkout, or the worktree directory a linked worktree's <c>.git</c> file points at.
    /// </summary>
    private static string? GitDirectoryFor(string root)
    {
        string dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;

        try
        {
            string pointer = File.ReadAllText(dotGit).Trim();
            if (!pointer.StartsWith(GitDirPrefix, StringComparison.Ordinal)) return null;

            string target = pointer[GitDirPrefix.Length..].Trim();
            return target.Length == 0 ? null : Path.GetFullPath(target, root);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The git directory every worktree of this repository shares. A linked worktree's metadata
    /// directory carries a <c>commondir</c> file naming it; the main worktree is its own.
    /// </summary>
    private static string CommonGitDirectory(string gitDirectory)
    {
        string pointer = Path.Combine(gitDirectory, "commondir");
        if (!File.Exists(pointer)) return gitDirectory;

        try
        {
            string common = File.ReadAllText(pointer).Trim();
            return common.Length == 0 ? gitDirectory : Path.GetFullPath(common, gitDirectory);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return gitDirectory;
        }
    }

    /// <summary>The branch reported by <c>HEAD</c>, which is a ref name, a commit id when detached, or unavailable.</summary>
    private static string ReadBranch(string headPath)
    {
        string head;
        try
        {
            if (!File.Exists(headPath)) return Unavailable;
            head = File.ReadAllText(headPath).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Unavailable;
        }

        const string localBranchPrefix = "ref: refs/heads/";
        const string refPrefix = "ref: refs/";

        if (head.StartsWith(localBranchPrefix, StringComparison.Ordinal))
            return head[localBranchPrefix.Length..];
        if (head.StartsWith(refPrefix, StringComparison.Ordinal))
            return head[refPrefix.Length..];

        // Anything left is a commit id: the checkout is detached, which is a real answer.
        return head.Length == 0 ? Unavailable : Detached;
    }

    /// <summary>
    /// Fingerprint of the local Git tip and index so a commit or checkout that leaves the
    /// docs tree untouched still invalidates the cached identity.
    /// </summary>
    /// <remarks>
    /// A linked worktree keeps its <c>HEAD</c> in the directory its <c>.git</c> file points at,
    /// and switching branch there is exactly the event the reported <c>branch=</c> has to notice.
    /// So that one file is read through the indirection while the rest of the fingerprint stays
    /// under the bound root: it is a path git itself named, not a walk out of the checkout.
    /// </remarks>
    private static long ComputeGitStamp(string root)
    {
        string gitPath = Path.Combine(root, ".git");
        try
        {
            if (File.Exists(gitPath))
            {
                FileInfo link = new(gitPath);
                long pointerStamp = (link.LastWriteTimeUtc.Ticks * 31) + link.Length;

                // Worktree / submodule indirection: the file itself notices a retarget.
                return GitDirectoryFor(root) is { } worktree
                    ? MixFile(pointerStamp, Path.Combine(worktree, "HEAD"))
                    : pointerStamp;
            }

            if (!Directory.Exists(gitPath)) return 0;

            long stamp = 17;
            stamp = MixFile(stamp, Path.Combine(gitPath, "HEAD"));
            stamp = MixFile(stamp, Path.Combine(gitPath, "index"));
            return stamp;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long MixFile(long stamp, string path)
    {
        if (!File.Exists(path)) return stamp;
        FileInfo info = new(path);
        stamp = (stamp * 31) + info.LastWriteTimeUtc.Ticks;
        stamp = (stamp * 31) + info.Length;
        return stamp;
    }

    private static (string Revision, string Dirty) ReadGitIdentity(string root)
    {
        string? revision = RunGit(root, "rev-parse", "--short", "HEAD");
        string? status = RunGit(root, "status", "--porcelain", "--untracked-files=no");
        string dirty = status is null
            ? "unknown"
            : status.Trim().Length == 0 ? "no" : "yes";
        return (revision is null ? Unavailable : FirstLine(revision), dirty);
    }

    internal static string FirstLine(string text)
    {
        string trimmed = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(Unavailable).Trim();
        return string.IsNullOrEmpty(trimmed) ? Unavailable : trimmed;
    }

    private static string? RunGit(string root, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
            return result.ExitCode == 0 ? result.StandardOutput : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or TimeoutException)
        {
            return null;
        }
    }

    private readonly record struct CachedIdentity(
        int CorpusStamp,
        long GitStamp,
        string Branch,
        string Repository,
        string Revision,
        string Dirty);
}

using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Mcp;

namespace KyberWeave.Data.Docs.Mcp;

/// <summary>
/// Reads a checkout's identity: the branch and shared git directory from the working tree's own metadata, so the
/// provenance header costs no child process for them, and the short revision and dirty marker through git.
/// </summary>
/// <remarks>
/// Every file read goes through the file-system seam and every git call through the process seam. A missing git, a directory that is not a repository, or a failed command all yield <c>rev=unavailable</c> and
/// <c>dirty=unknown</c>, which are success paths for the header, never an invented value.
/// </remarks>
public sealed class GitCheckoutProbe(IFileSystem files, IProcessRunner process, IPathResolver paths) : IGitCheckoutProbe
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The prefix git writes in <c>.git</c> when the checkout is a linked worktree.</summary>
    private const string GitDirPrefix = "gitdir:";

    /// <inheritdoc />
    public GitCheckoutLocation ReadLocation(string root)
    {
        string? gitDirectory = GitDirectoryFor(root);
        if (gitDirectory is null)
            return new GitCheckoutLocation(CorpusProvenance.Unavailable, CorpusProvenance.Unavailable);

        return new GitCheckoutLocation(ReadBranch(Path.Combine(gitDirectory, "HEAD")), CommonGitDirectory(gitDirectory));
    }

    /// <inheritdoc />
    public GitCheckoutRevision ReadRevision(string root)
    {
        string? revision = RunGit(root, "rev-parse", "--short", "HEAD");
        string? status = RunGit(root, "status", "--porcelain", "--untracked-files=no");
        string dirty = status is null
            ? "unknown"
            : status.Trim().Length == 0 ? "no" : "yes";
        return new GitCheckoutRevision(
            revision is null ? CorpusProvenance.Unavailable : CorpusProvenance.FirstLine(revision),
            dirty);
    }

    /// <inheritdoc />
    public long Fingerprint(string root)
    {
        string gitPath = Path.Combine(root, ".git");
        try
        {
            if (files.FileExists(gitPath))
            {
                long pointerStamp = (files.GetLastWriteTimeUtc(gitPath).Ticks * 31) + files.GetLength(gitPath);

                // Worktree / submodule indirection: the file itself notices a retarget.
                return GitDirectoryFor(root) is { } worktree
                    ? MixFile(pointerStamp, Path.Combine(worktree, "HEAD"))
                    : pointerStamp;
            }

            if (!files.DirectoryExists(gitPath)) return 0;

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

    /// <summary>
    /// The directory holding this checkout's own git metadata: <c>.git</c> itself in a normal
    /// checkout, or the worktree directory a linked worktree's <c>.git</c> file points at.
    /// </summary>
    private string? GitDirectoryFor(string root)
    {
        string dotGit = Path.Combine(root, ".git");
        if (files.DirectoryExists(dotGit)) return dotGit;
        if (!files.FileExists(dotGit)) return null;

        try
        {
            string pointer = files.ReadAllText(dotGit).Trim();
            if (!pointer.StartsWith(GitDirPrefix, StringComparison.Ordinal)) return null;

            string target = pointer[GitDirPrefix.Length..].Trim();
            return target.Length == 0 ? null : paths.GetFullPath(target, root);
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
    private string CommonGitDirectory(string gitDirectory)
    {
        string pointer = Path.Combine(gitDirectory, "commondir");
        if (!files.FileExists(pointer)) return gitDirectory;

        try
        {
            string common = files.ReadAllText(pointer).Trim();
            return common.Length == 0 ? gitDirectory : paths.GetFullPath(common, gitDirectory);
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
    private string ReadBranch(string headPath)
    {
        string head;
        try
        {
            if (!files.FileExists(headPath)) return CorpusProvenance.Unavailable;
            head = files.ReadAllText(headPath).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CorpusProvenance.Unavailable;
        }

        const string localBranchPrefix = "ref: refs/heads/";
        const string refPrefix = "ref: refs/";

        if (head.StartsWith(localBranchPrefix, StringComparison.Ordinal))
            return head[localBranchPrefix.Length..];
        if (head.StartsWith(refPrefix, StringComparison.Ordinal))
            return head[refPrefix.Length..];

        // Anything left is a commit id: the checkout is detached, which is a real answer.
        return head.Length == 0 ? CorpusProvenance.Unavailable : CorpusProvenance.Detached;
    }

    private long MixFile(long stamp, string path)
    {
        if (!files.FileExists(path)) return stamp;
        stamp = (stamp * 31) + files.GetLastWriteTimeUtc(path).Ticks;
        stamp = (stamp * 31) + files.GetLength(path);
        return stamp;
    }

    private string? RunGit(string root, params string[] arguments)
    {
        ProcessRunRequest request = new()
        {
            Program = "git",
            Arguments = arguments,
            WorkingDirectory = root,
            Timeout = GitTimeout,
            StandardInput = string.Empty,
        };

        try
        {
            ProcessRunResult result = process.RunAsync(request).GetAwaiter().GetResult();
            return result.ExitCode == 0 && !result.TimedOut ? result.StandardOutput : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or FileNotFoundException
            or DirectoryNotFoundException
            or InvalidOperationException)
        {
            return null;
        }
    }
}

using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;

namespace KyberWeave.Data.Docs.Mcp;

/// <summary>
/// Reads a checkout's identity: the branch and shared git directory from the working tree's own metadata, so the
/// provenance header costs no child process for them, and the short revision and dirty marker through git.
/// </summary>
/// <remarks>
/// A missing git, a directory that is not a repository, or a failed command all yield <c>rev=unavailable</c> and
/// <c>dirty=unknown</c>, which are success paths for the header, never an invented value.
/// </remarks>
internal sealed class GitCheckoutProbe : IGitCheckoutProbe
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
            if (!File.Exists(headPath)) return CorpusProvenance.Unavailable;
            head = File.ReadAllText(headPath).Trim();
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

    private static long MixFile(long stamp, string path)
    {
        if (!File.Exists(path)) return stamp;
        FileInfo info = new(path);
        stamp = (stamp * 31) + info.LastWriteTimeUtc.Ticks;
        stamp = (stamp * 31) + info.Length;
        return stamp;
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
}

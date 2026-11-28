using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Lists the files a working tree changed, by running git with argv and never a shell.</summary>
/// <param name="programs">Starts git.</param>
/// <param name="syncRun">The synchronous runner a test supplies; null uses the kernel's synchronous runner.</param>
public sealed class GitChangeReader(
    IProcessRunner programs,
    Func<ProcessStartInfo, string, ProcessResult>? syncRun = null) : IGitChangeReader
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The process seam the reader was built over, retained for async consumers.</summary>
    internal IProcessRunner Processes => programs;

    /// <summary>
    /// Lists the paths changed since <paramref name="baseRef"/> that still exist, together with untracked files.
    /// </summary>
    /// <remarks>
    /// Deleted paths are left out on purpose: a gate cannot read a file that is gone. The output is
    /// NUL-separated so a name git would otherwise quote comes through exactly as written.
    /// </remarks>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref to diff the working tree against.</param>
    /// <exception cref="InvalidOperationException">git could not list the files.</exception>
    public IReadOnlyList<string> Resolve(string workingDirectory, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string[] changed = RunGitPaths(
            workingDirectory,
            "diff", "-z", "--name-only", "--relative", "--diff-filter=ACMRT", baseRef, "--");
        string[] untracked = RunGitPaths(
            workingDirectory,
            "ls-files", "-z", "--others", "--exclude-standard", "--");

        return GitChangeParsing.Combine(changed, untracked);
    }

    /// <summary>The checked-out branch name, or an empty string on a detached head.</summary>
    /// <exception cref="InvalidOperationException">git could not answer.</exception>
    public string CurrentBranch(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        string[] output = RunGitPaths(workingDirectory, "branch", "--show-current");
        return output.Length == 0 ? string.Empty : output[0].Trim();
    }

    /// <summary>The commit <c>HEAD</c> names.</summary>
    /// <exception cref="InvalidOperationException">git could not answer.</exception>
    public string HeadCommit(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        string[] output = RunGitPaths(workingDirectory, "rev-parse", "--verify", "HEAD");
        return output.Length == 0 ? string.Empty : output[0].Trim();
    }

    /// <summary>The merge-base commit of <paramref name="baseRef"/> and <c>HEAD</c>.</summary>
    /// <exception cref="InvalidOperationException">git could not answer.</exception>
    public string MergeBaseCommit(string workingDirectory, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        string[] output = RunGitPaths(workingDirectory, "merge-base", baseRef, "HEAD");
        return output.Length == 0 ? string.Empty : output[0].Trim();
    }

    /// <summary>Whether <paramref name="commit"/> is <c>HEAD</c> or one of its ancestors; false when git does not know it.</summary>
    public bool IsAncestorOfHead(string workingDirectory, string commit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        try
        {
            RunGitPaths(workingDirectory, "merge-base", "--is-ancestor", commit, "HEAD");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists every path the working tree changed since <paramref name="commit"/>, deleted paths included,
    /// together with untracked files.
    /// </summary>
    /// <remarks>
    /// A deleted file can break a gate as surely as an edited one, so unlike <see cref="Resolve"/> this keeps it.
    /// A commit git does not know, such as one a rebase dropped, is an error: the caller then cannot compare.
    /// </remarks>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="commit">The commit to compare the working tree with.</param>
    /// <exception cref="InvalidOperationException">git could not list the files.</exception>
    public IReadOnlyList<string> ResolveSince(string workingDirectory, string commit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        string[] changed = RunGitPaths(workingDirectory, "diff", "-z", "--name-only", "--relative", commit, "--");
        string[] untracked = RunGitPaths(
            workingDirectory,
            "ls-files", "-z", "--others", "--exclude-standard", "--");

        return GitChangeParsing.Combine(changed, untracked);
    }

    /// <summary>
    /// Lists the paths changed since the merge base of <paramref name="baseRef"/> and
    /// <c>HEAD</c> with the kind of each change, together with untracked files.
    /// </summary>
    /// <remarks>
    /// The change under review is the working tree, not the last commit: the diff runs
    /// from the merge base to the files on disk, so staged renames, uncommitted edits,
    /// and untracked files are all in scope. A deleted path is reported as
    /// <see cref="ChangeKind.Deleted"/> rather than dropped — a deleted archive carries
    /// no document to check, and the caller tells that apart from a path that was never
    /// touched. Output is NUL-separated so a name git would otherwise quote comes
    /// through exactly as written.
    /// </remarks>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref the merge base is resolved against.</param>
    /// <exception cref="InvalidOperationException">
    /// No merge base resolves, so the scope is unknown. The message says how to fetch
    /// the history the scope needs.
    /// </exception>
    public IReadOnlyList<ChangedPath> ResolveSinceMergeBase(string workingDirectory, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string mergeBase = ResolveMergeBase(workingDirectory, baseRef);

        string[] status = RunGitPaths(
            workingDirectory,
            "diff", "-z", "--name-status", "-M", "--relative", mergeBase, "--");
        string[] untracked = RunGitPaths(
            workingDirectory,
            "ls-files", "-z", "--others", "--exclude-standard", "--");

        return GitChangeParsing.ParseNameStatus(status, untracked);
    }

    /// <summary>
    /// The unified diff from the merge base of <paramref name="baseRef"/> and <c>HEAD</c> to the files on disk, so
    /// staged and uncommitted edits are in it. Untracked files are not: <see cref="ResolveSinceMergeBase"/> lists them.
    /// </summary>
    /// <returns>The merge-base commit and the patch text.</returns>
    /// <exception cref="InvalidOperationException">No merge base resolves, or git could not produce the diff.</exception>
    public (string MergeBase, string Patch) DiffSinceMergeBase(string workingDirectory, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string[] output = RunGitPaths(workingDirectory, "merge-base", baseRef, "HEAD");
        if (output.Length == 0)
            throw new InvalidOperationException($"'{baseRef}' and HEAD have no common commit");

        string mergeBase = output[0].Trim();
        return (mergeBase, DiffSince(workingDirectory, mergeBase));
    }

    /// <summary>The unified diff from <paramref name="commit"/> to the files on disk; untracked files are not in it.</summary>
    /// <exception cref="InvalidOperationException">git could not produce the diff.</exception>
    public string DiffSince(string workingDirectory, string commit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        return RunGit(workingDirectory, "diff", "--no-color", "--relative", commit, "--");
    }

    /// <summary>
    /// Lists the one-line commits from <paramref name="fromCommit"/> to <c>HEAD</c> that touched any of
    /// <paramref name="paths"/>, newest first.
    /// </summary>
    /// <remarks>
    /// The path list is given after <c>--</c>, so a path that begins with a dash is a path and never an option, and
    /// <c>--literal-pathspecs</c> makes every path a literal name rather than a pattern. A path that is not
    /// repository-relative is ignored: git would read it as a path on the machine, not in the change.
    /// </remarks>
    /// <exception cref="InvalidOperationException">git could not list the commits.</exception>
    public IReadOnlyList<string> LogOneline(string workingDirectory, string fromCommit, IReadOnlyList<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCommit);
        ArgumentNullException.ThrowIfNull(paths);

        string[] relative = [.. paths.Where(CitedLineCheck.IsRepositoryRelative)];
        if (relative.Length == 0)
            return [];

        List<string> arguments = ["--literal-pathspecs", "log", "--oneline", "--no-color", $"{fromCommit}..HEAD", "--"];
        arguments.AddRange(relative);
        string output = RunGit(workingDirectory, [.. arguments]);
        return
        [
            .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];
    }

    /// <summary>
    /// Resolves the merge base of <paramref name="baseRef"/> and <c>HEAD</c>, failing
    /// closed with a fetch hint when the history on disk cannot answer.
    /// </summary>
    private string ResolveMergeBase(string workingDirectory, string baseRef)
    {
        string[] output;
        try
        {
            output = RunGitPaths(workingDirectory, "merge-base", baseRef, "HEAD");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Archive closeout scope is unknown: cannot determine what this change archives " +
                $"from base '{baseRef}' ({ex.Message.Trim()}). " +
                $"Fetch the full history ('git fetch --unshallow' or 'git fetch origin') and re-run.",
                ex);
        }

        if (output.Length == 0)
        {
            throw new InvalidOperationException(
                $"Archive closeout scope is unknown: cannot determine what this change archives " +
                $"from base '{baseRef}' (git merge-base reported no common commit). " +
                $"Fetch the full history ('git fetch --unshallow' or 'git fetch origin') and re-run.");
        }

        return output[0].Trim();
    }

    private string[] RunGitPaths(string workingDirectory, params string[] arguments) =>
        GitChangeParsing.SplitPaths(RunGit(workingDirectory, arguments));

    private string RunGit(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // The change reader is a synchronous adapter over git for the review services; its callers stay synchronous
        // until the review entry points move. Production uses the kernel's synchronous runner; a test supplies its own.
        ProcessResult result = syncRun is { } run
            ? run(startInfo, string.Empty)
            : ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited with code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }
}

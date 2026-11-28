using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Lists the files a working tree changed, by running git with argv and never a shell.</summary>
/// <remarks>
/// The <c>…Async</c> members run git through the injected <see cref="IProcessRunner"/> and never block a thread. The
/// remaining members are synchronous because their callers sit behind synchronous contracts
/// (<c>IReviewCycleSelector</c>, <c>IReviewWorkTreeService</c>, <c>IReviewEvidenceService</c>, <c>IDocsSchemaGate</c>);
/// the injected runner has no synchronous run, so they go through <paramref name="syncRun"/> or, when it is null,
/// the kernel's synchronous runner, until those callers move to the async members.
/// </remarks>
/// <param name="programs">Starts git for the <c>…Async</c> members.</param>
/// <param name="syncRun">The synchronous runner the synchronous members use; null uses the kernel's synchronous runner.</param>
public sealed class GitChangeReader(
    IProcessRunner programs,
    Func<ProcessStartInfo, string, ProcessResult>? syncRun = null) : IGitChangeReader
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Lists the paths changed since <paramref name="baseRef"/> that still exist, together with untracked files.
    /// </summary>
    /// <remarks>
    /// Deleted paths are left out on purpose: a gate cannot read a file that is gone. The output is
    /// NUL-separated so a name git would otherwise quote comes through exactly as written.
    /// </remarks>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref to diff the working tree against.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    /// <exception cref="InvalidOperationException">git could not list the files.</exception>
    /// <exception cref="Win32Exception">git could not be started.</exception>
    /// <exception cref="TimeoutException">git outlived its time limit.</exception>
    public async Task<IReadOnlyList<string>> ResolveAsync(
        string workingDirectory, string baseRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string[] changed = await RunGitPathsAsync(
                workingDirectory,
                cancellationToken,
                "diff", "-z", "--name-only", "--relative", "--diff-filter=ACMRT", baseRef, "--")
            .ConfigureAwait(false);
        string[] untracked = await RunGitPathsAsync(
                workingDirectory,
                cancellationToken,
                "ls-files", "-z", "--others", "--exclude-standard", "--")
            .ConfigureAwait(false);

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
    /// A deleted file can break a gate as surely as an edited one, so unlike <see cref="ResolveAsync"/> this keeps it.
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
    /// The asynchronous twin of <see cref="ResolveSinceMergeBase"/>: the same scope and the same refusal, run through
    /// the injected process runner.
    /// </summary>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref the merge base is resolved against.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    /// <exception cref="InvalidOperationException">No merge base resolves, so the scope is unknown.</exception>
    /// <exception cref="Win32Exception">git could not be started.</exception>
    /// <exception cref="TimeoutException">git outlived its time limit.</exception>
    public async Task<IReadOnlyList<ChangedPath>> ResolveSinceMergeBaseAsync(
        string workingDirectory, string baseRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string mergeBase = await ResolveMergeBaseAsync(workingDirectory, baseRef, cancellationToken).ConfigureAwait(false);

        string[] status = await RunGitPathsAsync(
                workingDirectory,
                cancellationToken,
                "diff", "-z", "--name-status", "-M", "--relative", mergeBase, "--")
            .ConfigureAwait(false);
        string[] untracked = await RunGitPathsAsync(
                workingDirectory,
                cancellationToken,
                "ls-files", "-z", "--others", "--exclude-standard", "--")
            .ConfigureAwait(false);

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

    /// <summary>The asynchronous twin of <see cref="DiffSinceMergeBase"/>, run through the injected process runner.</summary>
    /// <param name="workingDirectory">The repository root.</param>
    /// <param name="baseRef">The ref the merge base is resolved against.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    /// <returns>The merge-base commit and the patch text.</returns>
    /// <exception cref="InvalidOperationException">No merge base resolves, or git could not produce the diff.</exception>
    /// <exception cref="Win32Exception">git could not be started.</exception>
    /// <exception cref="TimeoutException">git outlived its time limit.</exception>
    public async Task<(string MergeBase, string Patch)> DiffSinceMergeBaseAsync(
        string workingDirectory, string baseRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string[] output = await RunGitPathsAsync(workingDirectory, cancellationToken, "merge-base", baseRef, "HEAD")
            .ConfigureAwait(false);
        if (output.Length == 0)
            throw new InvalidOperationException($"'{baseRef}' and HEAD have no common commit");

        string mergeBase = output[0].Trim();
        return (mergeBase, await DiffSinceAsync(workingDirectory, mergeBase, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The unified diff from <paramref name="commit"/> to the files on disk; untracked files are not in it.</summary>
    /// <exception cref="InvalidOperationException">git could not produce the diff.</exception>
    public string DiffSince(string workingDirectory, string commit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        return RunGit(workingDirectory, "diff", "--no-color", "--relative", commit, "--");
    }

    /// <summary>The asynchronous twin of <see cref="DiffSince"/>, run through the injected process runner.</summary>
    /// <param name="workingDirectory">The repository root.</param>
    /// <param name="commit">The commit to compare the working tree with.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    /// <exception cref="InvalidOperationException">git could not produce the diff.</exception>
    /// <exception cref="Win32Exception">git could not be started.</exception>
    /// <exception cref="TimeoutException">git outlived its time limit.</exception>
    public async Task<string> DiffSinceAsync(string workingDirectory, string commit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        return await RunGitAsync(workingDirectory, cancellationToken, "diff", "--no-color", "--relative", commit, "--")
            .ConfigureAwait(false);
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
    /// <param name="workingDirectory">The repository root.</param>
    /// <param name="fromCommit">The commit the history starts after.</param>
    /// <param name="paths">The repository-relative paths the commits must have touched.</param>
    /// <param name="cancellationToken">Cancels the run and kills git's process tree.</param>
    /// <exception cref="InvalidOperationException">git could not list the commits.</exception>
    /// <exception cref="Win32Exception">git could not be started.</exception>
    /// <exception cref="TimeoutException">git outlived its time limit.</exception>
    public async Task<IReadOnlyList<string>> LogOnelineAsync(
        string workingDirectory, string fromCommit, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCommit);
        ArgumentNullException.ThrowIfNull(paths);

        string[] relative = [.. paths.Where(CitedLineCheck.IsRepositoryRelative)];
        if (relative.Length == 0)
            return [];

        List<string> arguments = ["--literal-pathspecs", "log", "--oneline", "--no-color", $"{fromCommit}..HEAD", "--"];
        arguments.AddRange(relative);
        string output = await RunGitAsync(workingDirectory, cancellationToken, [.. arguments]).ConfigureAwait(false);
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

    /// <summary>The asynchronous twin of <see cref="ResolveMergeBase"/>.</summary>
    private async Task<string> ResolveMergeBaseAsync(string workingDirectory, string baseRef, CancellationToken cancellationToken)
    {
        string[] output;
        try
        {
            output = await RunGitPathsAsync(workingDirectory, cancellationToken, "merge-base", baseRef, "HEAD").ConfigureAwait(false);
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

    private async Task<string[]> RunGitPathsAsync(
        string workingDirectory, CancellationToken cancellationToken, params string[] arguments) =>
        GitChangeParsing.SplitPaths(
            await RunGitAsync(workingDirectory, cancellationToken, arguments).ConfigureAwait(false));

    private static ProcessStartInfo GitStartInfo(string workingDirectory, string[] arguments)
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

        return startInfo;
    }

    private string RunGit(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = GitStartInfo(workingDirectory, arguments);

        // The synchronous members serve callers that sit behind synchronous contracts, and the injected runner has no
        // synchronous run: they use the kernel's synchronous runner, or the one a test supplies.
        ProcessResult result = syncRun is { } run
            ? run(startInfo, string.Empty)
            : ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
        return Output(result, arguments);
    }

    private async Task<string> RunGitAsync(
        string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        ProcessResult result = await programs
            .RunAsync(GitStartInfo(workingDirectory, arguments), string.Empty, GitTimeout, cancellationToken)
            .ConfigureAwait(false);
        return Output(result, arguments);
    }

    private static string Output(ProcessResult result, string[] arguments)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited with code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }
}

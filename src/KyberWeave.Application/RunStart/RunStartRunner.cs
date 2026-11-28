using System.Globalization;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Application.RunStart;

/// <summary>
///     Runs the git commands a run start needs, through <see cref="IGitCommandRunner" />, so every child is
///     bounded by that runner's timeout and no command in this step reaches the network beyond the one
///     <c>git fetch</c> the step is for.
/// </summary>
/// <remarks>
///     Every git call passes argv as separate arguments, never a concatenated string, and runs in the repository
///     root so a command started from a subdirectory still names the same repository.
/// </remarks>
public sealed class RunStartRunner : IRunStartRunner
{
    private const string CodeGraphExecutable = "codegraph";
    private const string NoTrackOption = "--no-track";
    private const string TrackOption = "--track";
    private const string WorktreeStampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The highest numeric suffix tried for runs that start in the same second.</summary>
    private const int MaxWorktreeSuffix = 100;

    private readonly IGitCommandRunner _git;
    private readonly IWritableFileSystem _files;
    private readonly IProcessEnvironment _environment;
    private readonly IPathResolver _paths;
    private readonly string _repositoryRoot;

    /// <summary>Creates a runner over the checkout at <paramref name="repositoryRoot" />.</summary>
    /// <param name="git">The git seam, bounded by its own timeout.</param>
    /// <param name="files">The file system worktree folders and their claims live on.</param>
    /// <param name="environment">The process state the CodeGraph lookup reads.</param>
    /// <param name="paths">Resolves the repository root into the full path the worktree folder sits inside.</param>
    /// <param name="repositoryRoot">The repository root every git command runs in.</param>
    public RunStartRunner(IGitCommandRunner git, IWritableFileSystem files, IProcessEnvironment environment, IPathResolver paths, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        _git = git;
        _files = files;
        _environment = environment;
        _paths = paths;
        _repositoryRoot = repositoryRoot;
    }

    /// <summary>The repository root every git command runs in.</summary>
    public string RepositoryRoot => _repositoryRoot;

    /// <summary>Runs one git command and reports what it wrote.</summary>
    /// <param name="arguments">argv for git, one element per argument.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<GitOutcome> GitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return await _git.RunAsync(_repositoryRoot, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The repository root git reports for the current directory, or null outside a repository.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<string?> DetectRepositoryRootAsync(CancellationToken cancellationToken = default)
    {
        GitOutcome outcome = await GitAsync(["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded && outcome.StandardOutput.Trim().Length > 0
            ? outcome.StandardOutput.Trim()
            : null;
    }

    /// <summary>The git status outcome for the current checkout.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<GitOutcome> CheckoutStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GitAsync(["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the checkout has no uncommitted change, or null when the probe failed.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<bool?> CheckoutIsCleanAsync(CancellationToken cancellationToken = default)
    {
        GitOutcome outcome = await CheckoutStatusAsync(cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded ? outcome.StandardOutput.Trim().Length == 0 : null;
    }

    /// <summary>The git worktree list outcome for the current repository.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<GitOutcome> WorktreeListAsync(CancellationToken cancellationToken = default)
    {
        return await GitAsync(["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a second worktree is attached to this repository, or null when the probe failed.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<bool?> AnotherWorktreeIsAttachedAsync(CancellationToken cancellationToken = default)
    {
        GitOutcome outcome = await WorktreeListAsync(cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded ? GitWorktreePorcelain.HasMultiple(outcome.StandardOutput) : null;
    }

    /// <summary>
    ///     What <c>origin/HEAD</c> points at, or null when git cannot say.
    /// </summary>
    /// <remarks>
    ///     A clone whose <c>origin/HEAD</c> was never set prints the ref's own name rather than a symbolic
    ///     target, and treating that as a branch would name a branch called <c>HEAD</c>.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<string?> OriginHeadSymbolicRefAsync(CancellationToken cancellationToken = default)
    {
        GitOutcome outcome = await GitAsync(
            ["rev-parse", "--abbrev-ref", RunStartNaming.RemoteHead], cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
            return null;

        string resolved = outcome.StandardOutput.Trim();
        return resolved.Length == 0 || string.Equals(resolved, RunStartNaming.RemoteHead, StringComparison.Ordinal)
            ? null
            : resolved;
    }

    /// <summary>The commit a revision resolves to, or null when it does not resolve.</summary>
    /// <param name="revision">The revision.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<string?> ResolveRevisionAsync(string revision, CancellationToken cancellationToken = default)
    {
        GitOutcome outcome = await GitAsync(["rev-parse", "--verify", "--quiet", revision], cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded && outcome.StandardOutput.Trim().Length > 0
            ? outcome.StandardOutput.Trim()
            : null;
    }

    /// <summary>Whether a local branch of that name exists.</summary>
    /// <param name="branch">The branch.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<bool> HasLocalBranchAsync(string branch, CancellationToken cancellationToken = default)
    {
        GitOutcome outcome = await GitAsync(
            ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded;
    }

    /// <summary>Whether the remote has a branch of that name.</summary>
    /// <param name="branch">The branch.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<bool> HasRemoteBranchAsync(string branch, CancellationToken cancellationToken = default)
    {
        GitOutcome outcome = await GitAsync(
            ["rev-parse", "--verify", "--quiet", "refs/remotes/" + RunStartNaming.Remote + "/" + branch],
            cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded;
    }

    /// <summary>
    ///     The nearest tag at a revision, else its abbreviated commit, else null when the revision cannot be
    ///     described at all.
    /// </summary>
    /// <param name="revision">The revision to describe.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<string?> DescribeAsync(string revision, CancellationToken cancellationToken = default)
    {
        GitOutcome described = await GitAsync(["describe", "--tags", "--always", revision], cancellationToken).ConfigureAwait(false);
        if (described.Succeeded && described.StandardOutput.Trim().Length > 0)
            return described.StandardOutput.Trim();

        GitOutcome abbreviated = await GitAsync(["rev-parse", "--short", revision], cancellationToken).ConfigureAwait(false);
        return abbreviated.Succeeded && abbreviated.StandardOutput.Trim().Length > 0
            ? abbreviated.StandardOutput.Trim()
            : null;
    }

    /// <summary>
    ///     How far the two revisions have diverged: commits only on <paramref name="left" />, and commits only
    ///     on <paramref name="right" />. Both are null when git cannot compare them.
    /// </summary>
    /// <param name="left">The revision counted first, normally <c>HEAD</c>.</param>
    /// <param name="right">The revision counted second, normally the base ref.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<(int? Ahead, int? Behind)> DivergenceAsync(string left, string right, CancellationToken cancellationToken = default)
    {
        GitOutcome outcome = await GitAsync(
            ["rev-list", "--left-right", "--count", left + "..." + right], cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
            return (null, null);

        string[] counts = outcome.StandardOutput
            .Split(['\t', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length < 2 ||
            !int.TryParse(counts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ahead) ||
            !int.TryParse(counts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int behind))
        {
            return (null, null);
        }

        return (ahead, behind);
    }

    /// <summary>
    ///     The commits <paramref name="tip" /> has that <paramref name="baseRef" /> does not, with their full
    ///     messages, in one call.
    /// </summary>
    /// <param name="baseRef">The ref the resumed branch started from.</param>
    /// <param name="tip">The resumed branch or its remote-tracking ref.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <remarks>
    ///     One call rather than one per commit: a resume reads every task the branch finished, so a log per
    ///     commit would make resuming cost grow with the length of the run. Records are NUL-separated because
    ///     a commit message spans lines and git forbids a NUL inside one, so no separator here is ambiguous.
    ///     The range is the branch's own work: a task recorded before <paramref name="baseRef" /> belongs to
    ///     whatever finished it, not to this run. Oldest first, so the last task listed is where the run got
    ///     to and the operator reads the progress in the order it happened.
    /// </remarks>
    public async Task<GitOutcome> CommitMessagesAsync(string baseRef, string tip, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(tip);

        return await GitAsync(
            ["log", "--reverse", "-z", "--format=%H%n%B", baseRef + ".." + tip, "--"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The address <c>origin</c> points at, or null when there is no such remote.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<string?> OriginAddressAsync(CancellationToken cancellationToken = default)
    {
        GitOutcome outcome = await GitAsync(["remote", "get-url", RunStartNaming.Remote], cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded && outcome.StandardOutput.Trim().Length > 0
            ? outcome.StandardOutput.Trim()
            : null;
    }

    /// <summary>
    ///     Whether the CodeGraph binary resolves on <c>PATH</c>.
    /// </summary>
    /// <remarks>
    ///     Presence, not a version probe: exit 3 exists so a caller can tell "install something" apart from
    ///     "something broke", and only a binary that is not there at all has installing as its remedy. A
    ///     binary that is present and misbehaves is a tool failure, which is exit 1.
    /// </remarks>
    public bool CodeGraphBinaryIsAvailable()
    {
        return ResolveOnPath(CodeGraphExecutable) is not null;
    }

    /// <summary>Refreshes the remote-tracking refs, refusing to prompt.</summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<GitOutcome> FetchAsync(CancellationToken cancellationToken = default)
    {
        return await GitAsync(["fetch", RunStartNaming.Remote, "--prune"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Creates <paramref name="branch" /> in the checkout it is called in, starting from
    ///     <paramref name="startRef" />.
    /// </summary>
    /// <param name="branch">The branch to create.</param>
    /// <param name="startRef">The remote-tracking ref the branch starts at.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<GitOutcome> CreateBranchInPlaceAsync(string branch, string startRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(startRef);

        return await GitAsync(["switch", "-c", branch, NoTrackOption, startRef], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks out an existing branch in the current checkout.</summary>
    /// <param name="branch">The branch to check out.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<GitOutcome> SwitchToBranchAsync(string branch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        return await GitAsync(["switch", branch], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The worktree path a branch of that name is created at: inside <c>.agent_worktrees</c> under the
    ///     repository root, which <see cref="EnsureWorktreeFolderExcludedAsync" /> keeps out of git's status.
    /// </summary>
    /// <param name="branch">The branch the worktree holds.</param>
    /// <param name="startedAt">When the run started; the folder name carries it in UTC.</param>
    /// <remarks>
    ///     The name starts with a UTC timestamp so a repeat of the same branch never lands on an earlier
    ///     folder and a listing sorts by time. The stamp has no colon, which Windows forbids in a file name.
    ///     Only the folder is stamped: the branch keeps the shape the pull request naming check requires. Two
    ///     runs in one second take a numeric suffix. The returned path is claimed with a <c>.claim</c> file beside
    ///     it; the caller removes that with <see cref="ReleaseWorktreeClaim" /> once git has run.
    /// </remarks>
    public string WorktreePathFor(string branch, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        string root = Path.TrimEndingDirectorySeparator(_paths.GetFullPath(_repositoryRoot, _environment.CurrentDirectory));
        string container = Path.Combine(root, AgentWorktreeFolder.Name);
        string stamp = startedAt.UtcDateTime.ToString(WorktreeStampFormat, CultureInfo.InvariantCulture);
        string folder = stamp + "-" + branch.Replace('/', '-');

        // Bounded: only a folder or claim that really exists moves to the next suffix, and the cap stops even a
        // directory flooded with them. Past it the last name is returned and git reports why it cannot be used.
        string candidate = Path.Combine(container, folder);
        for (int attempt = 2; attempt <= MaxWorktreeSuffix && !TryClaim(container, candidate); attempt++)
            candidate = Path.Combine(container, folder + "-" + attempt.ToString(CultureInfo.InvariantCulture));

        return candidate;
    }

    /// <summary>Removes the claim <see cref="WorktreePathFor" /> took, once git has made the folder or failed to.</summary>
    /// <param name="worktreePath">The path <see cref="WorktreePathFor" /> returned.</param>
    public void ReleaseWorktreeClaim(string worktreePath)
    {
        try
        {
            _files.DeleteFile(ClaimPathFor(worktreePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A claim left behind only makes a later run in the same second take the next suffix.
        }
    }

    /// <summary>
    ///     Excludes <see cref="AgentWorktreeFolder.Name" /> from git's status in the local exclude file, writing the
    ///     line only when no line already holds it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>The outcome: succeeded when the file already excludes the folder or now does; failed when git could
    ///     not name the file or the file could not be read or written.</returns>
    /// <remarks>
    ///     <c>git rev-parse --git-path info/exclude</c> names the file git actually reads: a linked worktree shares
    ///     the exclude file of the main checkout, and git answers with that file's location. A relative answer is
    ///     resolved against the repository root, where git ran. The file is local, so the exclusion never enters a
    ///     commit.
    /// </remarks>
    public async Task<GitOutcome> EnsureWorktreeFolderExcludedAsync(CancellationToken cancellationToken = default)
    {
        GitOutcome located = await GitAsync(
            ["rev-parse", "--git-path", "info/exclude"], cancellationToken).ConfigureAwait(false);
        if (!located.Succeeded)
            return located;

        // git answers with one path on one line. Anything else (nothing, several lines, a character no path may hold)
        // is not a path to write to, so it fails the step rather than reaching the file system.
        string answer = located.StandardOutput.Trim();
        if (answer.Length == 0 || answer.Contains('\n', StringComparison.Ordinal) || answer.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return new GitOutcome(1, string.Empty, $"git did not name the exclude file: '{answer}'");

        string root = Path.TrimEndingDirectorySeparator(_paths.GetFullPath(_repositoryRoot, _environment.CurrentDirectory));
        string excludePath = Path.Combine(root, answer);

        try
        {
            string directory = Path.GetDirectoryName(excludePath) ?? root;
            if (!_files.DirectoryExists(directory))
                _files.CreateDirectory(directory);

            if (!_files.FileExists(excludePath))
            {
                _files.WriteAllTextAtomic(excludePath, AgentWorktreeFolder.ExcludeLine + "\n");
                return new GitOutcome(0, string.Empty, string.Empty);
            }

            string existing = _files.ReadAllText(excludePath);
            if (existing.Split('\n').Any(line => string.Equals(line.Trim(), AgentWorktreeFolder.ExcludeLine, StringComparison.Ordinal)))
                return new GitOutcome(0, string.Empty, string.Empty);

            // A file that ends without a newline gets one first, so the appended line starts a line of its own. The
            // file is the user's own, so it is replaced in one move: a failure part way never leaves it half written.
            string separator = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n";
            _files.WriteAllTextAtomic(excludePath, existing + separator + AgentWorktreeFolder.ExcludeLine + "\n");
            return new GitOutcome(0, string.Empty, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GitOutcome(1, string.Empty, $"could not update '{excludePath}': {ex.Message}");
        }
    }

    /// <summary>
    ///     Claims <paramref name="candidate" /> with a file only one process can create, so two runs in the same
    ///     second never pick the same folder. Checking that the folder is absent is not enough on its own: both
    ///     runs would see it absent before either created it.
    /// </summary>
    private bool TryClaim(string container, string candidate)
    {
        if (_files.PathExists(candidate))
            return false;

        try
        {
            _files.CreateDirectory(container);
            _files.CreateNewFile(ClaimPathFor(candidate));
        }
        catch (UnauthorizedAccessException)
        {
            // An unwritable container cannot hold a claim; git reports that folder's real error when it tries.
            return true;
        }
        catch (IOException)
        {
            // Only a claim another run holds means "taken". Any other I/O error (a full disk, a path too long)
            // would fail every suffix the same way, so it is left for git to report.
            return !_files.FileExists(ClaimPathFor(candidate));
        }

        // A run that finished between the check above and the claim has made the folder and dropped its claim.
        if (!_files.PathExists(candidate))
            return true;

        ReleaseWorktreeClaim(candidate);
        return false;
    }

    private static string ClaimPathFor(string worktreePath) => worktreePath + ".claim";

    /// <summary>
    ///     Creates <paramref name="branch" /> in a worktree at <paramref name="worktreePath" />, starting from
    ///     <paramref name="startRef" />.
    /// </summary>
    /// <param name="branch">The branch to create.</param>
    /// <param name="startRef">The remote-tracking ref the branch starts at.</param>
    /// <param name="worktreePath">The directory the worktree occupies.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<GitOutcome> CreateBranchInWorktreeAsync(
        string branch, string startRef, string worktreePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(startRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        return await AddWorktreeAsync(worktreePath, ["-b", branch, NoTrackOption], startRef, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks an existing branch out in a worktree at <paramref name="worktreePath" />.</summary>
    /// <param name="branch">The existing branch.</param>
    /// <param name="worktreePath">The directory the worktree occupies.</param>
    /// <param name="startRef">
    ///     The remote-tracking ref the branch starts from (for example <c>origin/feat/foo</c>) when
    ///     the branch does not yet exist locally. When null or when the branch already exists locally,
    ///     only the branch name is checked out.
    /// </param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    public async Task<GitOutcome> AddWorktreeForBranchAsync(
        string branch, string worktreePath, string? startRef = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        if (!await HasLocalBranchAsync(branch, cancellationToken).ConfigureAwait(false)
            && !string.IsNullOrWhiteSpace(startRef))
        {
            return await AddWorktreeAsync(worktreePath, ["-b", branch, TrackOption], startRef, cancellationToken).ConfigureAwait(false);
        }

        return await AddWorktreeAsync(worktreePath, [], branch, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitOutcome> AddWorktreeAsync(
        string worktreePath, string[] branchOptions, string commitish, CancellationToken cancellationToken)
    {
        string? parent = Path.GetDirectoryName(worktreePath);
        if (!string.IsNullOrEmpty(parent))
            _files.CreateDirectory(parent);

        List<string> arguments = ["worktree", "add"];
        arguments.AddRange(branchOptions);
        arguments.Add(worktreePath);
        arguments.Add(commitish);

        return await GitAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The first file of that name on <c>PATH</c>, or null. Windows also tries the <c>PATHEXT</c>
    ///     extensions, so an installed <c>.cmd</c> shim counts as present.
    /// </summary>
    private string? ResolveOnPath(string name)
    {
        string? path = _environment.GetVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string[] extensions = OperatingSystem.IsWindows()
            ? (_environment.GetVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (string directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory, name + extension);
                if (_files.FileExists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}

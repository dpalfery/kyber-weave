using System.Diagnostics;
using System.Globalization;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.RunStart;

/// <summary>What one git invocation wrote, and how it ended.</summary>
/// <param name="ExitCode">The exit code git returned.</param>
/// <param name="StandardOutput">Everything git wrote to standard output.</param>
/// <param name="StandardError">Everything git wrote to standard error.</param>
public readonly record struct GitOutcome(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Whether git exited zero.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>The first non-empty line of standard error, for a failure message.</summary>
    public string FirstErrorLine
    {
        get
        {
            string? line = (StandardError + "\n" + StandardOutput)
                .Split('\n')
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.Length > 0);
            return line ?? "no output";
        }
    }
}

/// <summary>
///     Runs the git commands a run start needs, through <see cref="IProcessExecutor" />, so every child is
///     bounded by that executor's timeout and no command in this step reaches the network beyond the one
///     <c>git fetch</c> the step is for.
/// </summary>
/// <remarks>
///     <see cref="IProcessExecutor" /> is declared in Core but keeps the CLI's Squad namespace so existing
///     references compile unchanged; that is why this file imports it. Every git call passes argv through
///     <see cref="ProcessStartInfo.ArgumentList" />, never a concatenated string, and runs in the repository
///     root so a command started from a subdirectory still names the same repository.
/// </remarks>
public sealed class RunStartRunner
{
    private const string GitExecutable = "git";
    private const string CodeGraphExecutable = "codegraph";
    private const string NoTrackOption = "--no-track";

    private readonly IProcessExecutor _executor;
    private readonly string _repositoryRoot;

    /// <summary>Creates a runner over the checkout at <paramref name="repositoryRoot" />.</summary>
    /// <param name="executor">The injected process seam, bounded by the caller's timeout.</param>
    /// <param name="repositoryRoot">The repository root every git command runs in.</param>
    public RunStartRunner(IProcessExecutor executor, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        _executor = executor;
        _repositoryRoot = repositoryRoot;
    }

    /// <summary>The repository root every git command runs in.</summary>
    public string RepositoryRoot => _repositoryRoot;

    /// <summary>Runs one git command and reports what it wrote.</summary>
    /// <param name="arguments">argv for git, one element per argument.</param>
    public GitOutcome Git(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessResult result = Run(GitExecutable, arguments);
        return new GitOutcome(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    /// <summary>The repository root git reports for the current directory, or null outside a repository.</summary>
    public string? DetectRepositoryRoot()
    {
        GitOutcome outcome = Git("rev-parse", "--show-toplevel");
        return outcome.Succeeded && outcome.StandardOutput.Trim().Length > 0
            ? outcome.StandardOutput.Trim()
            : null;
    }

    /// <summary>The git status outcome for the current checkout.</summary>
    public GitOutcome CheckoutStatus()
    {
        return Git("status", "--porcelain");
    }

    /// <summary>Whether the checkout has no uncommitted change, or null when the probe failed.</summary>
    public bool? CheckoutIsClean()
    {
        GitOutcome outcome = CheckoutStatus();
        return outcome.Succeeded ? outcome.StandardOutput.Trim().Length == 0 : null;
    }

    /// <summary>Whether a second worktree is attached to this repository.</summary>
    public bool AnotherWorktreeIsAttached()
    {
        GitOutcome outcome = Git("worktree", "list", "--porcelain");
        if (!outcome.Succeeded)
            return false;

        return outcome.StandardOutput
            .Split('\n')
            .Count(line => line.StartsWith("worktree ", StringComparison.Ordinal)) > 1;
    }

    /// <summary>
    ///     What <c>origin/HEAD</c> points at, or null when git cannot say.
    /// </summary>
    /// <remarks>
    ///     A clone whose <c>origin/HEAD</c> was never set prints the ref's own name rather than a symbolic
    ///     target, and treating that as a branch would name a branch called <c>HEAD</c>.
    /// </remarks>
    public string? OriginHeadSymbolicRef()
    {
        GitOutcome outcome = Git("rev-parse", "--abbrev-ref", RunStartPlanner.RemoteHead);
        if (!outcome.Succeeded)
            return null;

        string resolved = outcome.StandardOutput.Trim();
        return resolved.Length == 0 || string.Equals(resolved, RunStartPlanner.RemoteHead, StringComparison.Ordinal)
            ? null
            : resolved;
    }

    /// <summary>The commit a revision resolves to, or null when it does not resolve.</summary>
    public string? ResolveRevision(string revision)
    {
        GitOutcome outcome = Git("rev-parse", "--verify", "--quiet", revision);
        return outcome.Succeeded && outcome.StandardOutput.Trim().Length > 0
            ? outcome.StandardOutput.Trim()
            : null;
    }

    /// <summary>Whether a local branch of that name exists.</summary>
    public bool HasLocalBranch(string branch)
    {
        return Git("rev-parse", "--verify", "--quiet", "refs/heads/" + branch).Succeeded;
    }

    /// <summary>Whether the remote has a branch of that name.</summary>
    public bool HasRemoteBranch(string branch)
    {
        return Git("rev-parse", "--verify", "--quiet", "refs/remotes/" + RunStartPlanner.Remote + "/" + branch)
            .Succeeded;
    }

    /// <summary>
    ///     The nearest tag at a revision, else its abbreviated commit, else null when the revision cannot be
    ///     described at all.
    /// </summary>
    /// <param name="revision">The revision to describe.</param>
    public string? Describe(string revision)
    {
        GitOutcome described = Git("describe", "--tags", "--always", revision);
        if (described.Succeeded && described.StandardOutput.Trim().Length > 0)
            return described.StandardOutput.Trim();

        GitOutcome abbreviated = Git("rev-parse", "--short", revision);
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
    public (int? Ahead, int? Behind) Divergence(string left, string right)
    {
        GitOutcome outcome = Git("rev-list", "--left-right", "--count", left + "..." + right);
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

    /// <summary>The address <c>origin</c> points at, or null when there is no such remote.</summary>
    public string? OriginAddress()
    {
        GitOutcome outcome = Git("remote", "get-url", RunStartPlanner.Remote);
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
    public static bool CodeGraphBinaryIsAvailable()
    {
        return ResolveOnPath(CodeGraphExecutable) is not null;
    }

    /// <summary>Refreshes the remote-tracking refs, refusing to prompt.</summary>
    public GitOutcome Fetch()
    {
        return Git("fetch", RunStartPlanner.Remote, "--prune");
    }

    /// <summary>
    ///     Creates <paramref name="branch" /> in the checkout it is called in, starting from
    ///     <paramref name="startRef" />.
    /// </summary>
    /// <param name="branch">The branch to create.</param>
    /// <param name="startRef">The remote-tracking ref the branch starts at.</param>
    public GitOutcome CreateBranchInPlace(string branch, string startRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(startRef);

        return Git("switch", "-c", branch, NoTrackOption, startRef);
    }

    /// <summary>Checks out an existing branch in the current checkout.</summary>
    /// <param name="branch">The branch to check out.</param>
    public GitOutcome SwitchToBranch(string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        return Git("switch", branch);
    }

    /// <summary>
    ///     The worktree path a branch of that name is created at: a sibling of the repository, so it never
    ///     lands inside the checkout the operator is standing in.
    /// </summary>
    /// <param name="branch">The branch the worktree holds.</param>
    public string WorktreePathFor(string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_repositoryRoot));
        string? parent = Path.GetDirectoryName(root);
        string name = Path.GetFileName(root);
        string flattened = branch.Replace('/', '-');

        return Path.Combine(
            string.IsNullOrEmpty(parent) ? root : parent,
            name + ".worktrees",
            flattened);
    }

    /// <summary>
    ///     Creates <paramref name="branch" /> in a worktree at <paramref name="worktreePath" />, starting from
    ///     <paramref name="startRef" />.
    /// </summary>
    /// <param name="branch">The branch to create.</param>
    /// <param name="startRef">The remote-tracking ref the branch starts at.</param>
    /// <param name="worktreePath">The directory the worktree occupies.</param>
    public GitOutcome CreateBranchInWorktree(string branch, string startRef, string worktreePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(startRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        return AddWorktree(worktreePath, ["-b", branch, NoTrackOption], startRef);
    }

    /// <summary>Checks an existing branch out in a worktree at <paramref name="worktreePath" />.</summary>
    /// <param name="branch">The existing branch.</param>
    /// <param name="worktreePath">The directory the worktree occupies.</param>
    public GitOutcome AddWorktreeForBranch(string branch, string worktreePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        return AddWorktree(worktreePath, [], branch);
    }

    private GitOutcome AddWorktree(string worktreePath, string[] branchOptions, string commitish)
    {
        string? parent = Path.GetDirectoryName(worktreePath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        List<string> arguments = ["worktree", "add"];
        arguments.AddRange(branchOptions);
        arguments.Add(worktreePath);
        arguments.Add(commitish);

        return Git([.. arguments]);
    }

    private ProcessResult Run(string executable, string[] arguments)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _repositoryRoot
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return _executor.Run(startInfo, string.Empty);
    }

    /// <summary>
    ///     The first file of that name on <c>PATH</c>, or null. Windows also tries the <c>PATHEXT</c>
    ///     extensions, so an installed <c>.cmd</c> shim counts as present.
    /// </summary>
    private static string? ResolveOnPath(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (string directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}

using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Core.Arbiter;

/// <summary>Finds the repository root through git, or through a walk up to the nearest <c>.git</c> entry.</summary>
/// <param name="git">Runs git.</param>
/// <param name="files">Probes the file system.</param>
/// <param name="paths">Turns the caller's spelling into a full path.</param>
/// <param name="environment">
/// Names the directory a relative path is resolved against, and the home directory the user-level store lives under.
/// </param>
public sealed class ArbiterRepositoryLocator(
    IGitCommandRunner git,
    IWritableFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : IArbiterRepositoryLocator
{
    /// <inheritdoc />
    public string ResolveRepositoryRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string working = paths.GetFullPath(path, environment.CurrentDirectory);
        try
        {
            string workingDirectory = GitWorkingDirectory(working);
            GitOutcome result = git.Run(workingDirectory, ["rev-parse", "--show-toplevel"]);
            string root = result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(root))
                return paths.GetFullPath(root, workingDirectory);
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // No git, no root: the caller works from the path itself.
        }

        return working;
    }

    /// <inheritdoc />
    public async Task<string> ResolveRepositoryRootAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string working = paths.GetFullPath(path, environment.CurrentDirectory);
        try
        {
            string workingDirectory = GitWorkingDirectory(working);
            GitOutcome result = await git.RunAsync(
                workingDirectory, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
            string root = result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(root))
                return paths.GetFullPath(root, workingDirectory);
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // No git, no root: the caller works from the path itself.
        }

        return working;
    }

    /// <inheritdoc />
    public string ResolveArbiterDirectory(string path)
    {
        string root = ResolveRepositoryRoot(path);
        string home = environment.HomeDirectory;
        if (string.IsNullOrWhiteSpace(home))
        {
            return ArbiterStateDirectory.ForProject(root);
        }

        string working = paths.GetFullPath(path, environment.CurrentDirectory);
        string userLevel = ArbiterStateDirectory.ForGlobal(home, MainCheckoutOf(working, root), environment.CurrentDirectory);
        return HasLedgerOrDecisionLog(userLevel, files)
            ? userLevel
            : ArbiterStateDirectory.ForProject(root);
    }

    /// <inheritdoc />
    public async Task<string> ResolveArbiterDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        string root = await ResolveRepositoryRootAsync(path, cancellationToken).ConfigureAwait(false);
        string home = environment.HomeDirectory;
        if (string.IsNullOrWhiteSpace(home))
        {
            return ArbiterStateDirectory.ForProject(root);
        }

        string working = paths.GetFullPath(path, environment.CurrentDirectory);
        string mainCheckout = await MainCheckoutOfAsync(working, root, cancellationToken).ConfigureAwait(false);
        string userLevel = ArbiterStateDirectory.ForGlobal(home, mainCheckout, environment.CurrentDirectory);
        return HasLedgerOrDecisionLog(userLevel, files)
            ? userLevel
            : ArbiterStateDirectory.ForProject(root);
    }

    /// <summary>Whether a directory holds a ledger or a decision log, the two files a hook records through.</summary>
    private static bool HasLedgerOrDecisionLog(string directory, IFileSystem files) =>
        files.FileExists(Path.Combine(directory, InFlightLedger.FileName))
        || files.FileExists(Path.Combine(directory, DecisionLog.FileName));

    /// <summary>
    /// The main checkout of the repository at <paramref name="working" />: the parent of the common git directory,
    /// which every worktree of one repository shares, so a linked worktree answers with the checkout it was added from.
    /// <paramref name="root" /> when git names none, or names one that is not a <c>.git</c> directory.
    /// </summary>
    /// <remarks>
    /// The user-level hook keys its store by the directory the dispatching session works in, which for a run is the
    /// main checkout: a command run inside the run's linked worktree has to reach that same key from where it stands.
    /// </remarks>
    private string MainCheckoutOf(string working, string root)
    {
        try
        {
            string workingDirectory = GitWorkingDirectory(working);
            GitOutcome result = git.Run(workingDirectory, ["rev-parse", "--path-format=absolute", "--git-common-dir"]);
            string common = result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(common))
            {
                return root;
            }

            string commonDirectory = paths.GetFullPath(common, workingDirectory);
            return string.Equals(Path.GetFileName(commonDirectory), ".git", StringComparison.Ordinal)
                ? Path.GetDirectoryName(commonDirectory) ?? root
                : root;
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // No common directory, no main checkout: the key falls back to the root.
            return root;
        }
    }

    /// <summary>The main checkout of the repository, without blocking a thread while git runs.</summary>
    private async Task<string> MainCheckoutOfAsync(string working, string root, CancellationToken cancellationToken)
    {
        try
        {
            string workingDirectory = GitWorkingDirectory(working);
            GitOutcome result = await git.RunAsync(
                workingDirectory,
                ["rev-parse", "--path-format=absolute", "--git-common-dir"],
                cancellationToken).ConfigureAwait(false);
            string common = result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(common))
            {
                return root;
            }

            string commonDirectory = paths.GetFullPath(common, workingDirectory);
            return string.Equals(Path.GetFileName(commonDirectory), ".git", StringComparison.Ordinal)
                ? Path.GetDirectoryName(commonDirectory) ?? root
                : root;
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // No common directory, no main checkout: the key falls back to the root.
            return root;
        }
    }

    /// <summary>The directory git runs in for a path: the path itself, or its parent when it is not a directory.</summary>
    private string GitWorkingDirectory(string fullPath) =>
        files.DirectoryExists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath;

    /// <inheritdoc />
    public string FindGitRoot(string start)
    {
        string full = paths.GetFullPath(start, environment.CurrentDirectory);
        for (string? directory = full; directory is not null;)
        {
            if (files.PathExists(Path.Combine(directory, ".git")))
            {
                return directory;
            }

            string? parent = Path.GetDirectoryName(directory);
            directory = parent == directory ? null : parent;
        }

        return full;
    }
}

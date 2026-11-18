using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Core.Arbiter;

/// <summary>Finds the repository root through git, or through a walk up to the nearest <c>.git</c> entry.</summary>
/// <param name="git">Runs git.</param>
/// <param name="files">Probes the file system.</param>
/// <param name="paths">Turns the caller's spelling into a full path.</param>
/// <param name="environment">Names the directory a relative path is resolved against.</param>
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
            string workingDirectory = files.DirectoryExists(working) ? working : Path.GetDirectoryName(working) ?? working;
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
    public string ResolveArbiterDirectory(string path) =>
        Path.Combine(ResolveRepositoryRoot(path), "artifacts", "arbiter");

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

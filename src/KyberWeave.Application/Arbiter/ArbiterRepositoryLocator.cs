using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Core.Arbiter;

/// <summary>Finds the repository root through git, or through a walk up to the nearest <c>.git</c> entry.</summary>
/// <param name="git">Runs git.</param>
/// <param name="files">Probes the file system.</param>
public sealed class ArbiterRepositoryLocator(IGitCommandRunner git, IWritableFileSystem files) : IArbiterRepositoryLocator
{
    /// <inheritdoc />
    public string ResolveRepositoryRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string working = Path.GetFullPath(path);
        try
        {
            string workingDirectory = files.DirectoryExists(working) ? working : Path.GetDirectoryName(working) ?? ".";
            GitOutcome result = git.Run(workingDirectory, ["rev-parse", "--show-toplevel"]);
            string root = result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(root))
                return root;
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
        string full = Path.GetFullPath(start);
        for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
        {
            string marker = Path.Combine(directory.FullName, ".git");
            if (files.PathExists(marker))
            {
                return directory.FullName;
            }
        }

        return full;
    }
}

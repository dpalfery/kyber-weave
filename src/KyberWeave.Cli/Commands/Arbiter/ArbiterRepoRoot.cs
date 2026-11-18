using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Finds the repository root a file-reading Arbiter command contains its paths under.</summary>
/// <param name="files">Answers whether a <c>.git</c> entry exists.</param>
/// <param name="paths">Resolves the starting path against the working directory.</param>
/// <param name="environment">Names the working directory.</param>
public sealed class ArbiterRepoRoot(IFileSystem files, IPathResolver paths, IProcessEnvironment environment)
{
    /// <summary>
    /// The nearest directory at or above <paramref name="start"/> that holds a <c>.git</c> entry (a directory in a
    /// clone, a file in a worktree), or <paramref name="start"/> itself when there is none.
    /// </summary>
    /// <remarks>
    /// The walk stops at the first match rather than following any link, so the root is the one git itself would name.
    /// A command run outside any repository contains its paths under the directory it was run in.
    /// </remarks>
    public string Find(string start)
    {
        string full = paths.GetFullPath(start, environment.CurrentDirectory);
        for (string? directory = full; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string git = Path.Combine(directory, ".git");
            if (files.DirectoryExists(git) || files.FileExists(git))
            {
                return directory;
            }
        }

        return full;
    }
}

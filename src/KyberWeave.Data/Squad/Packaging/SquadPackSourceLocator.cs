using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Squad.Packaging;

/// <summary>
/// Locates the canonical Kyber-Squad product source tree within the current repository checkout.
/// </summary>
/// <param name="files">Probes the solution and manifest markers.</param>
/// <param name="paths">Resolves the working directory to a full path.</param>
/// <param name="environment">Supplies the directory a relative working directory is resolved against.</param>
public sealed class SquadPackSourceLocator(IFileSystem files, IPathResolver paths, IProcessEnvironment environment)
    : ISquadSourceLocator
{
    private const string SolutionFileName = "KyberWeave.sln";
    private const string SquadRelativePath = "products/kyber-squad/squad.yml";
    private const string CanonicalDirectoryRelativePath = "products/kyber-squad";

    /// <summary>
    /// Checks for the required repository solution marker and canonical squad manifest strictly
    /// in the immediate working directory without searching parent directories or falling back
    /// to embedded resources.
    /// </summary>
    /// <param name="workingDirectory">The directory to inspect.</param>
    /// <returns>
    /// The full path to the <c>products/kyber-squad</c> source directory when both markers exist;
    /// otherwise <see langword="null"/>.
    /// </returns>
    public string? Resolve(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        string root = paths.GetFullPath(workingDirectory, environment.CurrentDirectory);
        if (!files.DirectoryExists(root))
        {
            return null;
        }

        string solutionPath = Path.Combine(root, SolutionFileName);
        string squadPath = Path.Combine(
            root,
            SquadRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (files.FileExists(solutionPath) && files.FileExists(squadPath))
        {
            return paths.GetFullPath(
                Path.Combine(root, CanonicalDirectoryRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                root);
        }

        return null;
    }
}

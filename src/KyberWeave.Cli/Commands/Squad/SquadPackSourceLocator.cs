namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Locates the canonical Kyber-Squad product source tree within the current repository checkout.
/// </summary>
public static class SquadPackSourceLocator
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
    public static string? Resolve(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        string root = Path.GetFullPath(workingDirectory);
        string solutionPath = Path.Combine(root, SolutionFileName);
        string squadPath = Path.Combine(
            root,
            SquadRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(solutionPath) && File.Exists(squadPath))
        {
            return Path.GetFullPath(Path.Combine(
                root,
                CanonicalDirectoryRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        return null;
    }
}

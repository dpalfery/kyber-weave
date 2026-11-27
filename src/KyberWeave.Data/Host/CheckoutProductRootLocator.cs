using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Paths;

namespace KyberWeave.Data.Host;

/// <summary>Finds a product tree in the checkout the command runs from.</summary>
/// <remarks>
/// It looks only in the directory it is given, requires the solution file beside the product tree, and never climbs to a
/// parent or falls back to an embedded copy, so a run from anywhere else is refused rather than reading a different tree.
/// </remarks>
public sealed class CheckoutProductRootLocator(IFileSystem files, IPathResolver paths, IProcessEnvironment environment) : IProductRootLocator
{
    private const string SolutionFileName = "KyberWeave.sln";

    /// <inheritdoc />
    public string? Locate(string workingDirectory, string productRelativePath)
    {
        ArgumentNullException.ThrowIfNull(productRelativePath);
        if (string.IsNullOrWhiteSpace(workingDirectory) || !files.DirectoryExists(workingDirectory))
        {
            return null;
        }

        string root = paths.GetFullPath(workingDirectory, environment.CurrentDirectory);
        string solutionPath = Path.Combine(root, SolutionFileName);
        string productPath = Path.Combine(root, productRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return files.FileExists(solutionPath) && files.DirectoryExists(productPath)
            ? TrailingSeparator.Keep(productPath, paths.GetFullPath(productPath, root))
            : null;
    }
}

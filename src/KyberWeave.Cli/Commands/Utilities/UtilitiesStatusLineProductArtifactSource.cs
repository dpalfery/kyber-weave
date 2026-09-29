using KyberWeave.Core.Utilities.StatusLine;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>Reads the status-line artifacts from the canonical product tree.</summary>
/// <remarks>
///     The product layout is the one the artifact contract suite pins:
///     <c>products/kyber-utilities/statusline/{claude,antigravity,pi}/</c>, with one directory per
///     target and the deployable files inside it. Every regular file beneath a target directory is
///     deployed, so an extracted Pi extension can carry the helper modules it imports; the manifest
///     that records each file's origin and licence is not deployed, because it describes the artifacts
///     rather than being one.
/// </remarks>
internal sealed class UtilitiesStatusLineProductArtifactSource : IUtilitiesStatusLineArtifactSource
{
    private const string ManifestJsonFileName = "manifest.json";
    private const string ManifestYamlFileName = "manifest.yml";

    private readonly string _productRoot;

    /// <param name="productRoot">The absolute path of <c>products/kyber-utilities/statusline</c>.</param>
    public UtilitiesStatusLineProductArtifactSource(string productRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot);
        _productRoot = Path.GetFullPath(productRoot);
    }

    /// <inheritdoc />
    public IReadOnlyList<StatusLineDeploymentFile> Load(StatusLineTarget target)
    {
        string token = UtilitiesStatusLineTargetCatalog.GetToken(target);
        string directory = Path.Combine(_productRoot, DirectoryName(target));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(
                $"The {token} status-line artifacts are missing from '{directory}'. Run this command " +
                "from the repository checkout that holds 'products/kyber-utilities/statusline'.");

        List<StatusLineDeploymentFile> files = [];
        foreach (string physicalPath in EnumerateFiles(directory))
        {
            string fileName = Path.GetFileName(physicalPath);
            if (string.Equals(fileName, ManifestJsonFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, ManifestYamlFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            string relativePath = Path
                .GetRelativePath(directory, physicalPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            files.Add(new StatusLineDeploymentFile(relativePath, File.ReadAllBytes(physicalPath)));
        }

        if (files.Count == 0)
            throw new DirectoryNotFoundException(
                $"The {token} status-line artifact directory '{directory}' holds no deployable files.");

        return files;
    }

    /// <summary>The product subdirectory each target's artifacts live under.</summary>
    private static string DirectoryName(StatusLineTarget target)
    {
        return target switch
        {
            StatusLineTarget.Claude => "claude",
            StatusLineTarget.Agy => "antigravity",
            StatusLineTarget.Pi => "pi",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "No status-line artifact directory exists for this target.")
        };
    }

    /// <summary>
    ///     Walks the artifact directory one level at a time, skipping symlinks.
    /// </summary>
    /// <remarks>
    ///     <see cref="SearchOption.AllDirectories" /> follows directory symbolic links, so a link inside
    ///     the artifact tree could make the walk read files outside it (C# coding standard, "Safe
    ///     filesystem enumeration"). The product tree is repository-controlled, but the walk holds to
    ///     the same rule as every other walk over a directory this repository did not create at run
    ///     time.
    /// </remarks>
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos(
                         "*",
                         new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
            {
                if (entry.LinkTarget is not null) continue;

                switch (entry)
                {
                    case DirectoryInfo child:
                        pending.Push(child.FullName);
                        break;
                    case FileInfo file:
                        yield return file.FullName;
                        break;
                }
            }
        }
    }
}

/// <summary>Locates the canonical status-line artifact tree inside a repository checkout.</summary>
/// <remarks>
///     Mirrors <c>SquadPackSourceLocator</c>: it looks only in the immediate working directory, requires
///     the solution marker beside the product tree, and never climbs to a parent or falls back to an
///     embedded copy. A deploy run from anywhere else is refused with a hint naming the required
///     directory rather than silently reading a different tree.
/// </remarks>
internal static class UtilitiesStatusLineProductLocator
{
    private const string SolutionFileName = "KyberWeave.sln";
    private const string ProductRelativePath = "products/kyber-utilities/statusline";

    /// <summary>The absolute artifact root, or <see langword="null" /> when the markers are absent.</summary>
    public static string? Resolve(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)) return null;

        string root = Path.GetFullPath(workingDirectory);
        string solutionPath = Path.Combine(root, SolutionFileName);
        string productPath = Path.Combine(root, ProductRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(solutionPath) && Directory.Exists(productPath)
            ? Path.GetFullPath(productPath)
            : null;
    }
}

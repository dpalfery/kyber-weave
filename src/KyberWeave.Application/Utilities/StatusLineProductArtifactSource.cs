using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.StatusLine;
using KyberWeave.Application.StatusLine;

namespace KyberWeave.Application.Utilities;

/// <summary>Reads the status-line artifacts from the canonical product tree.</summary>
/// <remarks>
///     The product layout is the one the artifact contract suite pins:
///     <c>products/kyber-utilities/statusline/{claude,antigravity,pi}/</c>, with one directory per
///     target and the deployable files inside it. Every regular file beneath a target directory is
///     deployed, so an extracted Pi extension can carry the helper modules it imports; the manifest
///     that records each file's origin and licence is not deployed, because it describes the artifacts
///     rather than being one.
/// </remarks>
internal sealed class StatusLineProductArtifactSource : IUtilitiesStatusLineArtifactSource
{
    private const string ManifestJsonFileName = "manifest.json";
    private const string ManifestYamlFileName = "manifest.yml";

    private readonly IStatusLineFiles _files;
    private readonly string _productRoot;

    /// <param name="files">Reads the artifact tree.</param>
    /// <param name="paths">Turns the product root into a full path.</param>
    /// <param name="environment">Names the directory a relative product root is read against.</param>
    /// <param name="productRoot">The absolute path of <c>products/kyber-utilities/statusline</c>.</param>
    public StatusLineProductArtifactSource(
        IStatusLineFiles files,
        IPathResolver paths,
        IProcessEnvironment environment,
        string productRoot)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot);
        _files = files;
        _productRoot = paths.GetFullPath(productRoot, environment.CurrentDirectory);
    }

    /// <inheritdoc />
    public IReadOnlyList<StatusLineDeploymentFile> Load(StatusLineTarget target)
    {
        string token = UtilitiesStatusLineTargetCatalog.GetToken(target);
        string directory = Path.Combine(_productRoot, DirectoryName(target));
        if (!_files.DirectoryExists(directory))
            throw new DirectoryNotFoundException(
                $"The {token} status-line artifacts are missing from '{directory}'. Run this command " +
                "from the repository checkout that holds 'products/kyber-utilities/statusline'.");

        List<StatusLineDeploymentFile> files = [];
        foreach (string physicalPath in _files.EnumerateRegularFiles(directory))
        {
            string fileName = Path.GetFileName(physicalPath);
            if (string.Equals(fileName, ManifestJsonFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, ManifestYamlFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            string relativePath = Path
                .GetRelativePath(directory, physicalPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            files.Add(new StatusLineDeploymentFile(relativePath, _files.ReadAllBytes(physicalPath)));
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
            StatusLineTarget.CommandCode => "commandcode",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "No status-line artifact directory exists for this target.")
        };
    }
}

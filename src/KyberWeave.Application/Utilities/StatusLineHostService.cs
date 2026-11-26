using System.Security.Cryptography;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Domain.StatusLine;
using KyberWeave.Application.StatusLine;

namespace KyberWeave.Application.Utilities;

/// <summary>Answers the host questions the status-line commands ask, over <see cref="IStatusLineFiles"/> and the process environment.</summary>
/// <remarks>
///     Shared by <c>status</c> and <c>doctor</c> so the two cannot disagree about what counts as drift. An unreadable
///     file answers <see cref="UtilitiesStatusLineFileState.Unreadable"/> rather than <see cref="UtilitiesStatusLineFileState.Ok"/>,
///     so a removal that cannot prove it wrote a file never deletes it and a status that cannot prove it is intact
///     never reports it healthy.
/// </remarks>
public sealed class StatusLineHostService(
    IStatusLineFiles files,
    IProcessEnvironment environment,
    IProductRootLocator productRoots,
    IFileSystemPathSemantics pathSemantics,
    IPathResolver paths) : IStatusLineHostService
{
    /// <summary>Where the status-line artifact tree sits in the checkout.</summary>
    internal const string ProductRelativePath = "products/kyber-utilities/statusline";

    /// <inheritdoc />
    public IUtilitiesStatusLineArtifactSource ResolveArtifactSource()
    {
        string? productRoot = productRoots.Locate(environment.CurrentDirectory, ProductRelativePath);
        if (productRoot is null)
            throw new DirectoryNotFoundException(
                "The Kyber Utilities status-line artifacts were not found. Run this command from the " +
                "repository checkout that holds 'products/kyber-utilities/statusline'.");

        return new StatusLineProductArtifactSource(files, paths, environment, productRoot);
    }

    /// <inheritdoc />
    public bool IsOnPath(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        string? path = environment.GetVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executable);
            if (IsExecutableFile(candidate)) return true;
        }

        return false;
    }

    private bool IsExecutableFile(string candidate)
    {
        try
        {
            return files.FileExists(candidate) && files.HasUserExecuteBit(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A dangling symbolic link reports that it exists but cannot be inspected: not found here.
            return false;
        }
    }

    /// <inheritdoc />
    public UtilitiesStatusLineFileState EvaluateOwnedFile(string physicalPath, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        if (!files.FileExists(physicalPath)) return UtilitiesStatusLineFileState.Missing;

        byte[] bytes;
        try
        {
            bytes = files.ReadAllBytes(physicalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UtilitiesStatusLineFileState.Unreadable;
        }

        string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? UtilitiesStatusLineFileState.Ok
            : UtilitiesStatusLineFileState.Drift;
    }

    /// <inheritdoc />
    public bool HasExecutableBit(string physicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);

        return files.HasUserExecuteBit(physicalPath);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The deployment core writes bytes, not modes, so the executable bit the two command variants need is applied
    ///     here after the plan is applied. Pi's extension is loaded by Node and is left as written.
    /// </remarks>
    public void ApplyExecutableBit(string stagingRoot, IReadOnlyList<StatusLineDeploymentFile> deployed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(deployed);

        if (OperatingSystem.IsWindows()) return;

        foreach (StatusLineDeploymentFile file in deployed)
        {
            if (!UtilitiesStatusLineArtifacts.RequiresExecutableBit(file.RelativePath)) continue;

            string physicalPath = SquadPathPolicy.ResolveFile(stagingRoot, file.RelativePath, pathSemantics);
            if (!files.FileExists(physicalPath)) continue;

            files.MarkExecutable(physicalPath);
        }
    }
}

using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IRepositoryTreeProbe"/>: answers every question from what the test declares, touching no disk.</summary>
/// <remarks>
/// An empty instance says no directory exists, no root is accessible and no path passes through a link, which is the
/// safe answer for a test that converts a caller and does not exercise the probe.
/// </remarks>
internal sealed class FakeRepositoryTreeProbe : IRepositoryTreeProbe
{
    /// <summary>The directories that exist, by full path.</summary>
    public HashSet<string> Directories { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The roots and the access each one has; a root not named here does not exist.</summary>
    public Dictionary<string, RepositoryRootAccess> Roots { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The root and repository-relative path of each link that leads outside that root.</summary>
    public HashSet<(string Root, string Path)> EscapingPaths { get; init; } = [];

    /// <inheritdoc />
    public RepositoryRootAccess ProbeRoot(string root) =>
        Roots.TryGetValue(root, out RepositoryRootAccess? access) ? access : new RepositoryRootAccess(false);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directories.Contains(path);

    /// <inheritdoc />
    public bool LeadsOutsideThroughSymlink(string root, string path) => EscapingPaths.Contains((root, path));
}

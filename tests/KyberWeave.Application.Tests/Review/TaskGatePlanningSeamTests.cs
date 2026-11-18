using KyberWeave.Application.Review;
using KyberWeave.Contracts.Paths;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// A changed path is checked against the repository before it can reach a gate's argv, and the full path it is checked
/// as comes from the path seam rather than from the process's own directory.
/// </summary>
public sealed class TaskGatePlanningSeamTests
{
    private readonly RecordingPaths _paths = new();

    [Fact]
    public void AChangedPathIsResolvedThroughThePathSeamAgainstTheRoot()
    {
        string? problem = TaskGatePlanning.ValidatePath(_paths, "/repo", "src/a.cs", (_, _) => false);

        Assert.Null(problem);
        Assert.Equal([("/repo", "src/a.cs")], _paths.Resolved);
    }

    [Fact]
    public void AChangedPathThatLeavesTheRootIsRefusedWithoutAskingTheFileSystemForALink()
    {
        bool asked = false;

        string? problem = TaskGatePlanning.ValidatePath(_paths, "/repo", "../outside.cs", (_, _) => asked = true);

        Assert.NotNull(problem);
        Assert.False(asked);
        Assert.Empty(_paths.Resolved);
    }

    /// <summary>The path seam, recording what a caller asked it to resolve.</summary>
    private sealed class RecordingPaths : IPathResolver
    {
        private readonly FakePathResolver _inner = FakePathResolver.OverEmptyFileSystem();

        public List<(string Base, string Path)> Resolved { get; } = [];

        public string GetFullPath(string path, string baseDirectory)
        {
            Resolved.Add((baseDirectory, path));
            return _inner.GetFullPath(path, baseDirectory);
        }

        public bool AreSame(string first, string second) => _inner.AreSame(first, second);

        public bool IsWithin(string root, string candidate) => _inner.IsWithin(root, candidate);

        public string Canonicalize(string path) => _inner.Canonicalize(path);

        public string ResolvePhysicalRoot(string targetRoot) => _inner.ResolvePhysicalRoot(targetRoot);
    }
}

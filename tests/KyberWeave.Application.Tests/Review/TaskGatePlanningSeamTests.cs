using KyberWeave.Application.Review;
using KyberWeave.Contracts.Paths;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// A changed path is checked against the repository before it can reach a gate's argv: the root and the path are
/// resolved by the path seam, and whether the path stays inside the root is the seam's answer, which follows symbolic
/// links and the volume's case rule instead of comparing spellings.
/// </summary>
public sealed class TaskGatePlanningSeamTests
{
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/work");

    private readonly RecordingPaths _paths = new();

    [Fact]
    public void AChangedPathIsResolvedThroughThePathSeamAgainstTheRoot()
    {
        string? problem = TaskGatePlanning.ValidatePath(_paths, _environment, "/repo", "src/a.cs", (_, _) => false);

        Assert.Null(problem);
        Assert.Equal(("/repo", "src/a.cs"), _paths.Resolved[^1]);
    }

    [Fact]
    public void ARelativeRootIsResolvedAgainstTheInjectedWorkingDirectoryInsteadOfThrowing()
    {
        string? problem = TaskGatePlanning.ValidatePath(_paths, _environment, "repo", "src/a.cs", (_, _) => false);

        Assert.Null(problem);
        Assert.Equal(("/work", "repo"), _paths.Resolved[0]);
        Assert.Equal(("/work/repo", "src/a.cs"), _paths.Resolved[1]);
    }

    [Fact]
    public void ContainmentIsThePathSeamsAnswerRatherThanASpellingComparison()
    {
        _paths.Outside.Add("/repo/src/a.cs");

        string? problem = TaskGatePlanning.ValidatePath(_paths, _environment, "/repo", "src/a.cs", (_, _) => false);

        Assert.NotNull(problem);
        Assert.Contains("leaves the repository root", problem, StringComparison.Ordinal);
        Assert.Contains(("/repo", "/repo/src/a.cs"), _paths.Containment);
    }

    [Fact]
    public void APathReachingTheRootByALinkOutOfItIsRefusedByTheSeam()
    {
        InMemoryFileSystem files = new();
        files.CreateDirectory("/repo");
        files.CreateDirectory("/outside");
        files.CreateDirectoryLink("/repo/link", "/outside");
        FakePathResolver paths = new(files);

        string? problem = TaskGatePlanning.ValidatePath(paths, _environment, "/repo", "link/a.cs", (_, _) => false);

        Assert.NotNull(problem);
        Assert.Contains("leaves the repository root", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedPathThatLeavesTheRootIsRefusedWithoutAskingTheFileSystemForALink()
    {
        bool asked = false;

        string? problem = TaskGatePlanning.ValidatePath(_paths, _environment, "/repo", "../outside.cs", (_, _) => asked = true);

        Assert.NotNull(problem);
        Assert.False(asked);
        Assert.Empty(_paths.Resolved);
    }

    /// <summary>The path seam, recording what a caller asked it to resolve and to contain.</summary>
    private sealed class RecordingPaths : IPathResolver
    {
        private readonly FakePathResolver _inner = FakePathResolver.OverEmptyFileSystem();

        public List<(string Base, string Path)> Resolved { get; } = [];

        public List<(string Root, string Candidate)> Containment { get; } = [];

        /// <summary>Candidates the seam reports outside the root whatever their spelling says.</summary>
        public HashSet<string> Outside { get; } = new(StringComparer.Ordinal);

        public string GetFullPath(string path, string baseDirectory)
        {
            Resolved.Add((baseDirectory, path));
            return _inner.GetFullPath(path, baseDirectory);
        }

        public bool AreSame(string first, string second) => _inner.AreSame(first, second);

        public bool IsWithin(string root, string candidate)
        {
            Containment.Add((root, candidate));
            return !Outside.Contains(candidate) && _inner.IsWithin(root, candidate);
        }

        public string Canonicalize(string path) => _inner.Canonicalize(path);

        public string ResolvePhysicalRoot(string targetRoot) => _inner.ResolvePhysicalRoot(targetRoot);
    }
}

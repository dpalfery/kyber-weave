using System.Diagnostics;
using System.Text;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review.Catalogue;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// <see cref="ProjectRootDetection"/> over a real repository: the cases whose answer comes from git's own listing
/// (ignore rules, a committed vendor tree, a tracked file deleted from the work tree) or from a real symlink. The
/// walk-based and marker cases run in memory in <c>ProjectRootDetectorTests</c>.
/// </summary>
public sealed class ProjectRootDetectorGitAndSymlinkTests : IDisposable
{
    private static readonly string[] GoModuleMarkers = ["go.mod"];

    private readonly TempDirectory _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private static ProjectRootSet Detect(string repositoryRoot, string[] markers, ProjectRootNesting nesting) =>
        ProjectRootDetection.Detect(
            RealRepositoryFileLister.Create().List(repositoryRoot),
            markers,
            nesting,
            false,
            null);

    /// <summary>
    /// A directory that version control ignores is not part of the repository, so a module left there
    /// as scratch output is not gated. This case needs real git, because the ignore rules come from git.
    /// </summary>
    [Fact]
    public async Task AGitIgnoredDirectoryIsNeverARoot()
    {
        string repo = _fixture.Path;
        await Git(repo, "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "scratch/\n", new UTF8Encoding(false));
        Touch(repo, "scratch/go.mod");
        Touch(repo, "app/go.mod");

        ProjectRootSet set = Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Equal(["app"], set.Roots.Select(root => root.RootDirectory).ToArray());
    }

    /// <summary>
    /// A vendored tree that was committed is still vendored code. Git lists it as tracked, so the
    /// directory exclusion must apply on top of the git listing, not only to untracked files.
    /// </summary>
    [Fact]
    public async Task ACommittedVendorDirectoryIsNeverARoot()
    {
        string repo = _fixture.Path;
        await Git(repo, "init", "-q");
        Touch(repo, "vendor/go.mod");
        Touch(repo, "app/go.mod");
        await Git(repo, "add", "-A");
        await Git(
            repo,
            "-c", "user.name=kyber-weave-tests",
            "-c", "user.email=tests@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "-q", "-m", "fixture");

        ProjectRootSet set = Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Equal(["app"], set.Roots.Select(root => root.RootDirectory).ToArray());
    }

    /// <summary>
    /// A marker reached only through a symlink is not a regular file, so the walk skips it. A gate
    /// pointed at a link would check whatever the link happens to target rather than the tree.
    /// </summary>
    [Fact]
    public void ASymlinkedMarkerIsNeverARoot()
    {
        string repo = _fixture.Path;
        Touch(repo, "real/go.mod");
        Directory.CreateDirectory(Path.Combine(repo, "linked"));
        File.CreateSymbolicLink(Path.Combine(repo, "linked", "go.mod"), Path.Combine(repo, "real", "go.mod"));

        ProjectRootSet set = Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Equal(["real"], set.Roots.Select(root => root.RootDirectory).ToArray());
    }

    /// <summary>
    /// Git still lists a deleted-but-tracked file, so the detector checks the file exists before
    /// treating it as a marker. Otherwise a removed module keeps its gate until someone commits.
    /// </summary>
    [Fact]
    public async Task ADeletedButTrackedMarkerIsNeverARoot()
    {
        string repo = _fixture.Path;
        await Git(repo, "init", "-q");
        Touch(repo, "app/go.mod");
        await Git(repo, "add", "-A");
        await Git(
            repo,
            "-c", "user.name=kyber-weave-tests",
            "-c", "user.email=tests@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "-q", "-m", "fixture");
        File.Delete(Path.Combine(repo, "app", "go.mod"));

        ProjectRootSet set = Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Empty(set.Roots);
    }

    private static void Touch(string repo, string relativePath)
    {
        string path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
    }

    private static async Task Git(string repo, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} exited with {result.ExitCode}: {result.StandardError.Trim()}");
    }
}

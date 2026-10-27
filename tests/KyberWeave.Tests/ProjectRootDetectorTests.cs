using System.Diagnostics;
using System.Text;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review.Catalogue;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="ProjectRootDetector"/> finds the project roots of one family in a repository tree. The
/// result must be the same for the same tree, capped at eight roots, and carry a valid slug for each
/// root so the generated gate ids stay lookup-safe. Dependency and build-output directories and
/// git-ignored paths are never roots, because a gate pointed at them would check code nobody owns.
/// </summary>
public sealed class ProjectRootDetectorTests : IDisposable
{
    private static readonly string[] GoModuleMarkers = ["go.mod"];

    private static readonly string[] CargoMarkers = ["Cargo.toml"];

    private static readonly string[] SolutionMarkers = ["*.slnx", "*.sln"];

    private static readonly string[] PairMarkers = ["tsconfig.json", "package.json"];

    private static readonly string[] CsprojFallback = ["*.csproj"];

    private readonly TempDirectory _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    /// <summary>A family with no marker in the tree has no roots at all, so no gate is written for it.</summary>
    [Fact]
    public void ATreeWithoutAMarkerHasNoRoots()
    {
        string repo = _fixture.Path;
        Touch(repo, "src/program.cs");

        ProjectRootSet set = ProjectRootDetector.Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Empty(set.Roots);
        Assert.False(set.Capped);
    }

    /// <summary>
    /// Roots come back in ordinal path order, so the same tree always yields the same generated output
    /// regardless of the order the file system lists directories in.
    /// </summary>
    [Fact]
    public void EachGoModuleIsARootInOrdinalPathOrder()
    {
        string repo = _fixture.Path;
        Touch(repo, "b/go.mod");
        Touch(repo, "a/go.mod");

        ProjectRootSet set = ProjectRootDetector.Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Equal(["a", "b"], set.Roots.Select(root => root.RootDirectory).ToArray());
        Assert.Equal(["a/go.mod", "b/go.mod"], set.Roots.Select(root => root.MarkerPath).ToArray());
        Assert.Equal(["a", "b"], set.Roots.Select(root => root.Slug).ToArray());
    }

    /// <summary>
    /// Under <c>outermost</c> nesting a Cargo workspace member is covered by its workspace root. Gating
    /// each member as well would run the same workspace checks several times over.
    /// </summary>
    [Fact]
    public void ACargoWorkspaceRootCoversItsMembers()
    {
        string repo = _fixture.Path;
        Touch(repo, "Cargo.toml");
        Touch(repo, "crates/x/Cargo.toml");

        ProjectRootSet set = ProjectRootDetector.Detect(repo, CargoMarkers, ProjectRootNesting.Outermost);

        ProjectRoot root = Assert.Single(set.Roots);
        Assert.Equal(".", root.RootDirectory);
        Assert.Equal("Cargo.toml", root.MarkerPath);
    }

    /// <summary>
    /// A dependency directory holds package manifests that are not the repository's own projects. A
    /// real project beside it proves the detector is looking, so the test cannot pass by finding nothing.
    /// </summary>
    [Fact]
    public void APackageInsideNodeModulesIsNeverARoot()
    {
        string repo = _fixture.Path;
        Touch(repo, "node_modules/left-pad/package.json");
        Touch(repo, "node_modules/left-pad/tsconfig.json");
        Touch(repo, "web/package.json");
        Touch(repo, "web/tsconfig.json");

        ProjectRootSet set = ProjectRootDetector.Detect(
            repo,
            PairMarkers,
            ProjectRootNesting.Each,
            pair: true);

        Assert.Equal(["web"], set.Roots.Select(root => root.RootDirectory).ToArray());
    }

    /// <summary>
    /// A directory that version control ignores is not part of the repository, so a module left there
    /// as scratch output is not gated. This case needs real git, because the ignore rules come from git.
    /// </summary>
    [Fact]
    public void AGitIgnoredDirectoryIsNeverARoot()
    {
        string repo = _fixture.Path;
        Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "scratch/\n", new UTF8Encoding(false));
        Touch(repo, "scratch/go.mod");
        Touch(repo, "app/go.mod");

        ProjectRootSet set = ProjectRootDetector.Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Equal(["app"], set.Roots.Select(root => root.RootDirectory).ToArray());
    }

    /// <summary>
    /// A vendored tree that was committed is still vendored code. Git lists it as tracked, so the
    /// directory exclusion must apply on top of the git listing, not only to untracked files.
    /// </summary>
    [Fact]
    public void ACommittedVendorDirectoryIsNeverARoot()
    {
        string repo = _fixture.Path;
        Git(repo, "init", "-q");
        Touch(repo, "vendor/go.mod");
        Touch(repo, "app/go.mod");
        Git(repo, "add", "-A");
        Git(
            repo,
            "-c", "user.name=kyber-weave-tests",
            "-c", "user.email=tests@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "-q", "-m", "fixture");

        ProjectRootSet set = ProjectRootDetector.Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Equal(["app"], set.Roots.Select(root => root.RootDirectory).ToArray());
    }

    /// <summary>
    /// Nine roots would write nine copies of every root-bound gate. The detector reports the cap so
    /// the gate writer can comment those gates out and list the roots for the operator to pick from.
    /// </summary>
    [Fact]
    public void NineGoModulesCapTheFamily()
    {
        string repo = _fixture.Path;
        for (int i = 1; i <= 9; i++)
            Touch(repo, $"m{i}/go.mod");

        ProjectRootSet set = ProjectRootDetector.Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.True(set.Capped, "More than eight roots must report Capped.");
        Assert.Equal(9, set.Roots.Count);
    }

    /// <summary>The cap is a strict "more than eight" rule. Exactly eight roots are still written in full.</summary>
    [Fact]
    public void EightGoModulesAreNotCapped()
    {
        string repo = _fixture.Path;
        for (int i = 1; i <= 8; i++)
            Touch(repo, $"m{i}/go.mod");

        ProjectRootSet set = ProjectRootDetector.Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.False(set.Capped);
        Assert.Equal(8, set.Roots.Count);
    }

    /// <summary>
    /// Slugs are the lookup-safe part of a gate id. A path with a space is lowercased and its separators
    /// turned into hyphens. Two paths that reduce to the same slug get a numeric suffix in ordinal order,
    /// and the repository root is named <c>root</c>. Every slug must pass the same check the config
    /// loader uses, or the generated id is rejected.
    /// </summary>
    [Fact]
    public void SlugsLowercaseSeparatorsAndSuffixCollisions()
    {
        string repo = _fixture.Path;
        Touch(repo, "go.mod");
        Touch(repo, "apps/Web API/go.mod");
        Touch(repo, "apps/web-api/go.mod");

        ProjectRootSet set = ProjectRootDetector.Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Equal(
            [".", "apps/Web API", "apps/web-api"],
            set.Roots.Select(root => root.RootDirectory).ToArray());
        Assert.Equal(
            ["root", "apps-web-api", "apps-web-api-2"],
            set.Roots.Select(root => root.Slug).ToArray());
        Assert.All(set.Roots, root =>
            Assert.True(ConfigSlug.IsValid(root.Slug), $"Slug '{root.Slug}' for '{root.RootDirectory}' is not a valid id part."));
    }

    /// <summary>
    /// A TypeScript project is a directory that holds every marker in the pair. A <c>tsconfig.json</c>
    /// without a <c>package.json</c> beside it is not a TypeScript project, so no gate is written for it.
    /// </summary>
    [Fact]
    public void ATsconfigAloneIsNotAPairRoot()
    {
        string repo = _fixture.Path;
        Touch(repo, "web/tsconfig.json");

        ProjectRootSet set = ProjectRootDetector.Detect(
            repo,
            PairMarkers,
            ProjectRootNesting.Each,
            pair: true);

        Assert.Empty(set.Roots);
    }

    /// <summary>
    /// The pair's root is named by its first listed marker, so the marker path is stable whichever file
    /// the directory listing happens to return first.
    /// </summary>
    [Fact]
    public void ADirectoryHoldingEveryPairMarkerIsARootNamedByItsFirstMarker()
    {
        string repo = _fixture.Path;
        Touch(repo, "web/package.json");
        Touch(repo, "web/tsconfig.json");

        ProjectRootSet set = ProjectRootDetector.Detect(
            repo,
            PairMarkers,
            ProjectRootNesting.Each,
            pair: true);

        ProjectRoot root = Assert.Single(set.Roots);
        Assert.Equal("web", root.RootDirectory);
        Assert.Equal("web/tsconfig.json", root.MarkerPath);
    }

    /// <summary>
    /// A C# repository with no solution file still has a project to check. A single <c>.csproj</c> is
    /// used as the root, which keeps a lone project from going unchecked just because it has no <c>.sln</c>.
    /// </summary>
    [Fact]
    public void ASingleCsprojWithoutASolutionIsTheFallbackRoot()
    {
        string repo = _fixture.Path;
        Touch(repo, "src/App.csproj");

        ProjectRootSet set = ProjectRootDetector.Detect(
            repo,
            SolutionMarkers,
            ProjectRootNesting.Each,
            fallback: CsprojFallback);

        ProjectRoot root = Assert.Single(set.Roots);
        Assert.Equal("src", root.RootDirectory);
        Assert.Equal("src/App.csproj", root.MarkerPath);
    }

    /// <summary>
    /// The fallback is used only when it matches exactly one file. Two unrelated projects with no solution
    /// give no guess at which one the operator meant, so neither is a root.
    /// </summary>
    [Fact]
    public void TwoCsprojWithoutASolutionAreNotAFallbackRoot()
    {
        string repo = _fixture.Path;
        Touch(repo, "a/A.csproj");
        Touch(repo, "b/B.csproj");

        ProjectRootSet set = ProjectRootDetector.Detect(
            repo,
            SolutionMarkers,
            ProjectRootNesting.Each,
            fallback: CsprojFallback);

        Assert.Empty(set.Roots);
    }

    /// <summary>
    /// Detection runs again on every review and every init. A second call over the same tree must return
    /// the same roots, markers, slugs and cap, or the generated output drifts between runs.
    /// </summary>
    [Fact]
    public void ASecondCallOverTheSameTreeReturnsTheSameResult()
    {
        string repo = _fixture.Path;
        Touch(repo, "b/go.mod");
        Touch(repo, "a/go.mod");
        Touch(repo, "apps/Web API/go.mod");

        ProjectRootSet first = ProjectRootDetector.Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);
        ProjectRootSet second = ProjectRootDetector.Detect(repo, GoModuleMarkers, ProjectRootNesting.Each);

        Assert.Equal(Project(first), Project(second));
        Assert.Equal(first.Capped, second.Capped);
    }

    private static (string RootDirectory, string MarkerPath, string Slug)[] Project(ProjectRootSet set) =>
        set.Roots.Select(root => (root.RootDirectory, root.MarkerPath, root.Slug)).ToArray();

    private static void Touch(string repo, string relativePath)
    {
        string path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
    }

    private static void Git(string repo, params string[] arguments)
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

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} exited with {result.ExitCode}: {result.StandardError.Trim()}");
    }
}

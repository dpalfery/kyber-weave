using System.Diagnostics;
using System.Text;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>kyber-weave review catalogue</c> prints the catalogue <c>review:</c> block to stdout
/// and writes no file, so gates added in a later release can be copied into a host config by hand.
/// </summary>
/// <remarks>
/// Fixtures come from <c>tests/fixtures/gate-catalogue/&lt;family&gt;/clean/</c>, copied into a
/// fresh temp git repository per test. The expected block is the YAML
/// <c>GateCatalogue.Render</c> produces for the same fixture and technologies — the block
/// <c>docs init</c> would write — reached here through <c>ReviewBlockWriter.Plan</c>, which
/// detects each family's roots the same way.
/// </remarks>
public sealed class ReviewCatalogueCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Stdout equals the YAML block <c>GateCatalogue.Render</c> produces for the fixture and
    /// its declared technologies: the block <c>docs init</c> writes.
    /// </summary>
    [Fact]
    public void StdoutEqualsTheRenderedCatalogueBlockForTheFixtureTechnologies()
    {
        string repo = NewFixtureRepo("go", ["go"]);
        ReviewGatesPlan plan = ReviewBlockWriter.Plan(repo, ["go"]);
        Assert.True(
            plan.ShouldWrite,
            "The go fixture planned no review block, so the stdout comparison proves nothing.");

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewCatalogue().Run(["review", "catalogue", repo]));

        Assert.True(
            execution.Result == 0,
            $"review catalogue exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        Assert.Equal(plan.Rendered.Yaml.TrimEnd(), execution.Output.TrimEnd());
    }

    /// <summary>
    /// The command writes no file: every file under PATH is byte-identical after the run.
    /// </summary>
    [Fact]
    public void CatalogueWritesNoFileUnderPath()
    {
        string repo = NewFixtureRepo("go", ["go"]);
        string[] before = Snapshot(repo);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewCatalogue().Run(["review", "catalogue", repo]));

        Assert.True(
            execution.Result == 0,
            $"review catalogue exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        Assert.Equal(before, Snapshot(repo));
    }

    /// <summary>
    /// An unknown <c>--technology</c> slug exits 2 with a nearest-match hint, as
    /// <c>KyberStandardsTemplates.Render</c> gives it.
    /// </summary>
    [Fact]
    public void UnknownTechnologyExitsTwoAndSuggestsTheNearestSlug()
    {
        string repo = NewFixtureRepo("go", ["go"]);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewCatalogue().Run(["review", "catalogue", repo, "--technology", "gox"]));

        Assert.Equal(2, execution.Result);
        Assert.Contains("gox", execution.Output, StringComparison.Ordinal);
        Assert.Contains("Did you mean 'go'?", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A known slug with no catalogue family is not an error: it reports the gap and exits 0.
    /// </summary>
    [Fact]
    public void TechnologyWithNoCatalogueFamilyExitsZeroAndReportsNoGatesShipped()
    {
        string repo = NewFixtureRepo("go", ["go"]);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewCatalogue().Run(["review", "catalogue", repo, "--technology", "azure"]));

        Assert.True(
            execution.Result == 0,
            $"review catalogue --technology azure exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        Assert.Contains("# no gates shipped for azure", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A broken <c>.kyber-weave/kyber-weave.yml</c> exits 1: the config load error is fatal.
    /// </summary>
    [Fact]
    public void BrokenHostConfigExitsOne()
    {
        string repo = NewFixtureRepo("go", ["go"]);
        File.WriteAllText(ConfigPath(repo), "ontology: [unclosed\n  technologies: : : bad\n");

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewCatalogue().Run(["review", "catalogue", repo]));

        Assert.Equal(1, execution.Result);
    }

    /// <summary>
    /// A PATH the platform cannot resolve returns the config-load diagnostic and exits 1 instead of crashing.
    /// </summary>
    [Fact]
    public void UnresolvablePathExitsOneWithTheConfigDiagnostic()
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewCatalogue().Run(["review", "catalogue", "bad\0path"]));

        Assert.Equal(1, execution.Result);
        Assert.Contains("KW-CONFIG-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Technology slugs are validated case-insensitively, so they are rendered the same way: <c>Go</c> emits
    /// the block <c>go</c> does rather than silently nothing.
    /// </summary>
    [Fact]
    public void TechnologySlugCaseDoesNotChangeTheRenderedBlock()
    {
        string repo = NewFixtureRepo("go", ["go"]);

        CapturedConsoleExecution<int> lower = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewCatalogue().Run(["review", "catalogue", repo, "--technology", "go"]));
        CapturedConsoleExecution<int> mixed = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewCatalogue().Run(["review", "catalogue", repo, "--technology", "Go"]));

        Assert.Equal(0, mixed.Result);
        Assert.False(string.IsNullOrWhiteSpace(lower.Output));
        Assert.Equal(lower.Output, mixed.Output);
    }

    /// <summary>
    /// Copies the family's clean fixture into a fresh temp git repository and declares the
    /// family's technologies. Never builds in place: the repository's
    /// <c>Directory.Build.props</c> would otherwise apply to the fixture.
    /// </summary>
    private string NewFixtureRepo(string family, IReadOnlyList<string> technologies)
    {
        string repo = Path.Combine(_temp.Path, family + "-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(FixtureCleanPath(family), repo);
        WriteHostConfig(repo, technologies);
        InitGit(repo);
        return repo;
    }

    private static string FixtureCleanPath(string family) =>
        Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "fixtures", "gate-catalogue", family, "clean");

    private static string ConfigPath(string repo) =>
        Path.Combine(repo, ".kyber-weave", "kyber-weave.yml");

    private static void WriteHostConfig(string repo, IReadOnlyList<string> technologies)
    {
        StringBuilder yaml = new();
        yaml.AppendLine("ontology:");
        yaml.AppendLine("  docs-root: docs");
        if (technologies.Count == 0)
        {
            yaml.AppendLine("  technologies: []");
        }
        else
        {
            yaml.AppendLine("  technologies:");
            foreach (string technology in technologies)
            {
                yaml.Append("    - ").AppendLine(technology);
            }
        }

        string directory = Path.Combine(repo, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), yaml.ToString());
    }

    private static void InitGit(string repo)
    {
        MustGit(repo, "init", "--quiet");
        MustGit(repo, "add", "-A");
        MustGit(
            repo, "-c", "user.name=t2r", "-c", "user.email=t2r@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "fixture");
    }

    private static void MustGit(string repo, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {result.StandardOutput} {result.StandardError}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>
    /// Every file under the repository (outside <c>.git</c>) as <c>relative-path:sha256</c>
    /// lines, sorted, so a run that writes nothing leaves the snapshot unchanged.
    /// </summary>
    private static string[] Snapshot(string repo)
    {
        List<string> entries = new();
        foreach (string file in Directory.GetFiles(repo, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(repo, file);
            if (relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                string.Equals(relative, ".git", StringComparison.Ordinal))
            {
                continue;
            }

            entries.Add(relative + ":" + Sha256(file));
        }

        entries.Sort(StringComparer.Ordinal);
        return entries.ToArray();
    }

    private static string Sha256(string path)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
        return Convert.ToHexString(hash);
    }
}

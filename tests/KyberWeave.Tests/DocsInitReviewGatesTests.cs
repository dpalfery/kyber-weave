using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review.Catalogue;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs init</c> appends the rendered <c>review:</c> block when the host configuration has
/// none, never changes an existing block, and honours the opt-out. Each family fixture proves
/// the written section byte for byte against its approved <c>expected-review.yml</c>.
/// </summary>
/// <remarks>
/// RED (task 7, test-first): no review block is written, so every assertion that requires a
/// written <c>review:</c> section fails. The <c>expected-review.yml</c> files are RED
/// placeholders; GREEN replaces each with the approved first-run output after hand review.
/// </remarks>
public sealed class DocsInitReviewGatesTests : IDisposable
{
    private const string ConfigRelativePath = ".kyber-weave/kyber-weave.yml";
    private const string NoGatesForAgent = "KW-REVIEW-027";
    private const string OptOutLine = "review gates not written (--no-review-gates)";
    private const string CiTemplateLine =
        "CI: copy templates/github-actions/kyber-weave-review-gate.yml from " +
        "https://github.com/dpalfery/kyber-weave into .github/workflows/.";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>One argument per gate-catalogue family fixture directory.</summary>
    public static IEnumerable<object[]> FamilyFixtureNames()
    {
        yield return new object[] { "csharp" };
        yield return new object[] { "go" };
        yield return new object[] { "python" };
        yield return new object[] { "rust" };
        yield return new object[] { "typescript" };
    }

    /// <summary>
    /// The written <c>review:</c> section must equal the family's approved expected file.
    /// A hand edit to the catalogue that changes what a host receives fails here rather
    /// than silently shipping different gates to the next host that runs <c>docs init</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(FamilyFixtureNames))]
    public void DocsInitWritesTheApprovedReviewBlock(string family)
    {
        string repo = NewFixtureRepo(family, TechnologiesFor(family));

        DocsScaffolder.Scaffold(repo);

        string written = NormalizeVersionStamp(ReadReviewSection(repo));
        string expected = NormalizeVersionStamp(File.ReadAllText(ExpectedReviewPath(family)));
        Assert.Equal(expected, written);
    }

    /// <summary>
    /// A host-owned <c>review:</c> section is never rewritten, with or without
    /// <c>--force</c>. Regenerating it would discard gates the operator tuned by hand.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExistingReviewSectionSurvivesDocsInitUnchanged(bool force)
    {
        string repo = NewFixtureRepo("go", ["go"]);
        string original = HandOwnedConfigWithReview();
        File.WriteAllText(ConfigPath(repo), original);
        MustGit(repo, "add", "-A");
        MustGit(
            repo, "-c", "user.name=t2r", "-c", "user.email=t2r@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "hand-owned review");

        // The hand-owned block must load, or the scaffold preflight would reject it and
        // this test would prove nothing about preservation.
        KyberWeaveConfig loaded = KyberWeaveConfigLoader.Load(repo);
        Assert.Contains(
            loaded.Review.Gates, gate => string.Equals(gate.Id, "hand-owned-council", StringComparison.Ordinal));

        DocsScaffolder.Scaffold(repo, force: force);

        Assert.Equal(original, File.ReadAllText(ConfigPath(repo)));
    }

    /// <summary>
    /// The second run finds <c>review:</c> and writes nothing, so the host configuration is
    /// byte-identical to the first run's result. A diff on every re-run would make adoption
    /// look perpetually dirty.
    /// </summary>
    [Fact]
    public void RunningDocsInitTwiceLeavesTheHostConfigByteIdentical()
    {
        string repo = NewFixtureRepo("go", ["go"]);

        DocsScaffolder.Scaffold(repo);
        string first = File.ReadAllText(ConfigPath(repo));

        DocsScaffolder.Scaffold(repo);
        string second = File.ReadAllText(ConfigPath(repo));

        Assert.False(
            string.IsNullOrEmpty(ReadReviewSection(repo)),
            "The first run wrote no review: section, so the second run proves nothing about idempotency.");
        Assert.Equal(first, second);
    }

    /// <summary>
    /// Adding the section must not touch any other byte: comments, key order, and the
    /// file's newline style belong to the operator. Both newline styles are covered because
    /// the append uses the file's existing style.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AnExistingConfigWithoutAReviewSectionKeepsEveryOtherByte(string newline)
    {
        string repo = NewFixtureRepo("go", ["go"]);
        string original = string.Join(
            newline,
            ["# Ours, not the tool's.", "ontology:", "  docs-root: docs", "  technologies:", "    - go", string.Empty]);
        File.WriteAllText(ConfigPath(repo), original);
        MustGit(repo, "add", "-A");
        MustGit(
            repo, "-c", "user.name=t2r", "-c", "user.email=t2r@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "config without review");

        DocsScaffolder.Scaffold(repo);

        string updated = File.ReadAllText(ConfigPath(repo));
        Assert.Contains(newline + "review:", updated, StringComparison.Ordinal);
        int reviewIndex = updated.IndexOf(newline + "review:", StringComparison.Ordinal);
        Assert.Equal(original.TrimEnd('\r', '\n') + newline, updated.Substring(0, reviewIndex));
    }

    /// <summary>
    /// The opt-out writes no gates and says so. An operator who manages review gates by
    /// hand must not find a generated block after asking for none.
    /// </summary>
    [Fact]
    public void NoReviewGatesWritesNothingAndSaysSo()
    {
        string repo = NewFixtureRepo("go", ["go"]);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => DocsApp().Run(["docs", "init", repo, "--no-skill", "--no-review-gates"]));

        Assert.True(
            execution.Result == 0,
            $"docs init --no-review-gates exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        Assert.Contains(OptOutLine, execution.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, ReadReviewSection(repo));
    }

    /// <summary>
    /// A declared technology with no catalogue entry is reported, not failed. A stack
    /// without shipped gates is a valid state, and failing on it would block adoption.
    /// </summary>
    [Fact]
    public void AHostWithOnlyUnfamiliedTechnologiesWritesNoReviewSection()
    {
        string repo = NewRepo(["azure"]);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => DocsApp().Run(["docs", "init", repo, "--no-skill"]));

        Assert.True(
            execution.Result == 0,
            $"docs init on an azure-only host exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        Assert.Contains("no gates shipped for azure", execution.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, ReadReviewSection(repo));
    }

    /// <summary>
    /// Merged <c>--kyber-standards</c> technologies render the families whose roots exist
    /// live and comment the rest out with the marker the host should point at. The go-only
    /// fixture has exactly one root, so every other family must explain itself.
    /// </summary>
    [Fact]
    public void KyberStandardsOnAGoOnlyFixtureWritesGoGatesAndCommentsTheOtherFamiliesOut()
    {
        string repo = NewFixtureRepo("go", ["go"]);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => DocsApp().Run(["docs", "init", repo, "--no-skill", "--kyber-standards"]));

        Assert.True(
            execution.Result == 0,
            $"docs init --kyber-standards exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        IReadOnlyList<GateCatalogueFamily> families = GateCatalogue.LoadEmbedded();
        GateCatalogueFamily go = families.Single(
            candidate => candidate.Technologies.Contains("go", StringComparer.Ordinal));
        string configText = File.ReadAllText(ConfigPath(repo));
        Assert.Contains(
            configText.Split('\n'),
            line => line.TrimStart().StartsWith("- id: go-", StringComparison.Ordinal));
        foreach (GateCatalogueFamily other in families)
        {
            if (string.Equals(other.Family, go.Family, StringComparison.Ordinal))
            {
                continue;
            }

            string reason = $"no {other.Markers[0]} found";
            Assert.Contains(reason, configText, StringComparison.Ordinal);
            Assert.Contains(reason, execution.Output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Writing gates is reported per technology — the ids written, the gates commented
    /// out with their reasons — and points at the CI template. The exact ids are pinned
    /// by the approved-files test; this pins the reporting contract.
    /// </summary>
    [Fact]
    public void DocsInitListsWrittenAndCommentedOutGatesPerTechnology()
    {
        string repo = NewFixtureRepo("go", ["go"]);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => DocsApp().Run(["docs", "init", repo, "--no-skill"]));

        Assert.True(
            execution.Result == 0,
            $"docs init exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        Assert.Contains(CiTemplateLine, execution.Output, StringComparison.Ordinal);
        Assert.Matches(@"\bgo-[a-z0-9-]+\b", execution.Output);
        Assert.False(
            string.IsNullOrEmpty(ReadReviewSection(repo)),
            "docs init wrote no review: section, so there was nothing to list.");
    }

    /// <summary>
    /// Leaving an existing block in place still tells the operator which catalogue gates
    /// are missing from it, without changing the exit code. Silence here would let a host
    /// drift behind the catalogue without noticing.
    /// </summary>
    [Fact]
    public void AnExistingReviewSectionListsMissingCatalogueGatesAsInformation()
    {
        string repo = NewFixtureRepo("go", ["go"]);
        File.WriteAllText(ConfigPath(repo), HandOwnedConfigWithReview());
        MustGit(repo, "add", "-A");
        MustGit(
            repo, "-c", "user.name=t2r", "-c", "user.email=t2r@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "hand-owned review");

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => DocsApp().Run(["docs", "init", repo, "--no-skill"]));

        Assert.True(
            execution.Result == 0,
            $"docs init exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");
        Assert.Contains("go-vet", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The file <c>docs init</c> writes must load through <c>KyberWeaveConfigLoader</c>
    /// with no configuration error. A catalogue that cannot load is a bug the tests catch
    /// first, not the host's first review.
    /// </summary>
    [Fact]
    public void TheWrittenHostConfigLoadsWithoutAConfigurationError()
    {
        string repo = NewFixtureRepo("go", ["go"]);

        DocsScaffolder.Scaffold(repo);

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(repo);
        Assert.True(
            loaded.Success,
            $"docs init wrote a host config that does not load: {loaded.Error ?? "no error"}");
        Assert.DoesNotContain(
            KyberWeaveConfigLoader.ConfigLoadErrorCode, loaded.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.NotNull(loaded.Config);
        Assert.NotEmpty(loaded.Config.Review.Gates);
    }

    /// <summary>
    /// After <c>docs init</c> on the rust fixture, <c>review gates --agent rust-dev</c>
    /// runs that agent's gates instead of reporting that no gates are declared. Toolchain
    /// runs need cargo; the selection itself is asserted from the written configuration
    /// when cargo is missing, so the test holds on hosts without the toolchain.
    /// The R6.4 trigger is the task-gate skill, which always names a task, so the run
    /// passes <c>--task r6-4-probe</c>: rust-clippy uses
    /// <c>--target-dir .agent_scratchpad/{task}/target</c>, which is refused without one.
    /// </summary>
    [Fact]
    public void ReviewGatesRunsTheWrittenRustGatesAndReportsGatesDeclared()
    {
        string repo = NewFixtureRepo("rust", ["rust"]);
        DocsScaffolder.Scaffold(repo);
        File.AppendAllText(Path.Combine(repo, "src", "lib.rs"), "\n// review probe\n");
        string reportPath = Path.Combine(_temp.Path, "rust-gates-" + Guid.NewGuid().ToString("N") + ".json");

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => ReviewApp().Run(
            [
                "review", "gates", repo, "--agent", "rust-dev", "--task", "r6-4-probe",
                "--paths", "src/lib.rs", "--out", reportPath
            ]));

        Assert.DoesNotContain(NoGatesForAgent, execution.Output, StringComparison.Ordinal);
        if (CargoAvailable())
        {
            Assert.True(
                File.Exists(reportPath),
                $"review gates wrote no report. Output:{Environment.NewLine}{execution.Output}");
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.NotEmpty(report.RootElement.GetProperty("gates").EnumerateArray());
        }
        else
        {
            KyberWeaveConfig config = KyberWeaveConfigLoader.Load(repo);
            Assert.True(
                config.Review.TaskGates.ContainsKey("rust-dev"),
                "docs init wrote no rust-dev task gates, so there was nothing to select.");
            Assert.NotEmpty(config.Review.TaskGates["rust-dev"]);
        }
    }

    private static IReadOnlyList<string> TechnologiesFor(string family)
    {
        GateCatalogueFamily catalogue = GateCatalogue.LoadEmbedded()
            .Single(candidate => string.Equals(candidate.Family, family, StringComparison.Ordinal));
        return catalogue.Technologies;
    }

    /// <summary>
    /// Copies the family's clean fixture into a fresh temp git repository and declares the
    /// family's technologies, ready for <c>docs init</c>. Never builds in place: the
    /// repository's <c>Directory.Build.props</c> would otherwise apply to the fixture.
    /// </summary>
    private string NewFixtureRepo(string family, IReadOnlyList<string> technologies)
    {
        string repo = Path.Combine(_temp.Path, family + "-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(FixtureCleanPath(family), repo);
        WriteHostConfig(repo, technologies);
        InitGit(repo);
        return repo;
    }

    private string NewRepo(IReadOnlyList<string> technologies)
    {
        string repo = Path.Combine(_temp.Path, "host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        WriteHostConfig(repo, technologies);
        InitGit(repo);
        return repo;
    }

    private static void InitGit(string repo)
    {
        MustGit(repo, "init", "--quiet");
        MustGit(repo, "add", "-A");
        MustGit(
            repo, "-c", "user.name=t2r", "-c", "user.email=t2r@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "fixture");
    }

    private static string FixtureCleanPath(string family) =>
        Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "fixtures", "gate-catalogue", family, "clean");

    private static string ExpectedReviewPath(string family) =>
        Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "fixtures", "gate-catalogue", family, "expected-review.yml");

    /// <summary>
    /// The header comment stamps the build version, which carries the commit hash; blank it
    /// so the fixtures stay valid on every commit.
    /// </summary>
    private static string NormalizeVersionStamp(string yaml) =>
        Regex.Replace(yaml, "written by Kyber-Weave \\S+", "written by Kyber-Weave <version>");

    private static string ConfigPath(string repo) => Path.Combine(repo, ConfigRelativePath);

    /// <summary>
    /// The written <c>review:</c> section: from the top-level <c>review</c> key (a commented
    /// <c># review:</c> does not count) to the end of the file. Empty when absent.
    /// </summary>
    private static string ReadReviewSection(string repo)
    {
        string[] lines = File.ReadAllLines(ConfigPath(repo));
        for (int index = 0; index < lines.Length; index++)
        {
            if (Regex.IsMatch(lines[index], "^review[ \\t]*:"))
            {
                return string.Join("\n", lines, index, lines.Length - index) + "\n";
            }
        }

        return string.Empty;
    }

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

    /// <summary>
    /// A hand-owned host configuration: operator comments, the go stack, and a review
    /// block with ids the catalogue never emits, so every catalogue gate for go is missing
    /// from it. Mirrors the minimal shape the review-gates command tests load.
    /// </summary>
    private static string HandOwnedConfigWithReview() =>
        """
        # Ours, not the tool's.
        ontology:
          docs-root: docs
          technologies:
            - go
        review:
          gates:
            - id: hand-owned-council
              run: [git, --version]
          task-gates:
            go-dev:
              - id: hand-owned-task
                include: ["**/*.go"]
                run: [git, --version]
        """;

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

    private static CommandApp DocsApp()
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.AddBranch("docs", docs => { docs.AddCommand<DocsInitCommand>("init"); });
        });
        return app;
    }

    private static CommandApp ReviewApp()
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.UseStrictParsing();
            config.AddBranch("review", review => { review.AddCommand<ReviewGatesCommand>("gates"); });
        });
        return app;
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

    private static bool CargoAvailable()
    {
        try
        {
            ProcessStartInfo startInfo = new("cargo", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            return ProcessRunner.ReadToEnd(process).ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review.Catalogue;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Real-toolchain run for every version 1 catalogue family (R9.6, task 13): each family's
/// rendered gates run against its fixture project with the real toolchain, once clean (which
/// must pass) and once per seeded defect (which must fail its named gate).
/// </summary>
/// <remarks>
/// <para>
/// This class never runs in the default unit-test run: the real toolchains add minutes, so
/// every case skips unless <c>KW_GATE_TOOLCHAIN=1</c> (the <c>gate-catalogue-toolchains</c>
/// CI job on <c>ubuntu-latest</c> sets it). A local macOS run iterates the shape but is
/// recorded as "macOS, not Linux" and never closes the task.
/// </para>
/// <para>
/// Each case copies the fixture into a fresh temp git repository, declares the family's
/// technologies, runs <c>docs init</c> in-process (the same <see cref="CommandApp"/> shape
/// <c>DocsInitCommandTests</c> drives), then runs <c>review gates</c> for the council and
/// once more with <c>--agent</c> for the family's agent. The defect cases assert the named
/// gate failed with <c>KW-REVIEW-022</c> and never <c>KW-REVIEW-033</c>: 033 would mean the
/// gate could not start, which proves nothing about the probe. The actual exit code of each
/// defect run is written to the test output for the closeout record.
/// </para>
/// <para>
/// The agent runs pass <c>--task ship-gate-13</c> because the catalogue's task gates embed
/// <c>{task}</c> (csharp-build's <c>--artifacts-path</c>, rust-clippy's <c>--target-dir</c>),
/// which is refused without a task slug. The typescript runs run <c>npm ci</c> inside the
/// temp copy first, so only Node itself must be preinstalled.
/// </para>
/// </remarks>
public sealed class GateCatalogueToolchainTests(ITestOutputHelper output) : IDisposable
{
    private const string ToolchainFlag = "KW_GATE_TOOLCHAIN";
    private const string ConfigRelativePath = ".kyber-weave/kyber-weave.yml";
    private const string PassedCode = "KW-REVIEW-021";
    private const string FailedCode = "KW-REVIEW-022";
    private const string NotStartedCode = "KW-REVIEW-033";
    private const string TaskSlug = "ship-gate-13";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>One row per gate-catalogue family: the fixture directory, its agent, and one gated source file.</summary>
    public static IEnumerable<object[]> CleanCases()
    {
        yield return new object[] { "csharp", "csharp-dev", "src/Lib/Lib.cs" };
        yield return new object[] { "go", "go-dev", "fixture.go" };
        yield return new object[] { "python", "python-dev", "src/fixture/__init__.py" };
        yield return new object[] { "rust", "rust-dev", "src/lib.rs" };
        yield return new object[] { "typescript", "typescript-dev", "src/index.ts" };
    }

    /// <summary>
    /// One row per seeded defect: the family, its agent, a gated source file, the defect
    /// directory under <c>defects/</c>, and the council gate id that defect must fail.
    /// </summary>
    public static IEnumerable<object[]> DefectCases()
    {
        yield return new object[] { "csharp", "csharp-dev", "src/Lib/Lib.cs", "build", "csharp-build" };
        yield return new object[] { "csharp", "csharp-dev", "src/Lib/Lib.cs", "format-analyzers", "csharp-format-analyzers" };
        yield return new object[] { "csharp", "csharp-dev", "src/Lib/Lib.cs", "format-style", "csharp-format-style" };
        yield return new object[] { "csharp", "csharp-dev", "src/Lib/Lib.cs", "format-whitespace", "csharp-format-whitespace" };
        yield return new object[] { "csharp", "csharp-dev", "tests/Lib.Tests/CalculatorTests.cs", "test", "csharp-test" };
        yield return new object[] { "go", "go-dev", "fixture.go", "build", "go-build" };
        yield return new object[] { "go", "go-dev", "fixture.go", "fmt", "go-fmt" };
        yield return new object[] { "go", "go-dev", "fixture_test.go", "test", "go-test" };
        yield return new object[] { "go", "go-dev", "fixture.go", "vet", "go-vet" };
        yield return new object[] { "python", "python-dev", "src/fixture/__init__.py", "format", "python-format" };
        yield return new object[] { "python", "python-dev", "tests/test_fixture.py", "lint", "python-lint" };
        yield return new object[] { "python", "python-dev", "tests/test_fixture.py", "test", "python-test" };
        yield return new object[] { "python", "python-dev", "src/fixture/__init__.py", "typecheck", "python-typecheck" };
        yield return new object[] { "rust", "rust-dev", "src/lib.rs", "clippy", "rust-clippy" };
        yield return new object[] { "rust", "rust-dev", "src/lib.rs", "fmt", "rust-fmt" };
        yield return new object[] { "rust", "rust-dev", "src/lib.rs", "test", "rust-test" };
        yield return new object[] { "typescript", "typescript-dev", "src/index.ts", "lint", "typescript-lint" };
        yield return new object[] { "typescript", "typescript-dev", "test/index.test.mjs", "test", "typescript-test" };
        yield return new object[] { "typescript", "typescript-dev", "src/index.ts", "typecheck", "typescript-typecheck" };
    }

    /// <summary>
    /// The clean fixture passes the council gates and the family agent's gates with the
    /// real toolchain: exit zero, every gate <c>KW-REVIEW-021</c>, none failed or unstarted.
    /// </summary>
    [Theory]
    [MemberData(nameof(CleanCases))]
    public void CleanFixturePassesCouncilAndAgentGates(string family, string agent, string sourcePath)
    {
        RequireToolchain();

        string repo = NewToolchainRepo(family, "clean");

        string councilReport = NewReportPath("council");
        CliRun council = Gates(CouncilArguments(repo, councilReport));
        output.WriteLine($"{family} clean council exit code: {council.ExitCode}");
        AssertExitCode(0, council);
        Assert.Contains(PassedCode, council.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(FailedCode, council.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(NotStartedCode, council.Output, StringComparison.Ordinal);
        AssertAllGatesPassed(ReadReport(councilReport));

        string agentReport = NewReportPath("agent");
        CliRun gated = Gates(AgentArguments(repo, agent, sourcePath, agentReport));
        output.WriteLine($"{family} clean agent ({agent}) exit code: {gated.ExitCode}");
        AssertExitCode(0, gated);
        Assert.Contains(PassedCode, gated.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(FailedCode, gated.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(NotStartedCode, gated.Output, StringComparison.Ordinal);
        AssertAllGatesPassed(ReadReport(agentReport));
    }

    /// <summary>
    /// The seeded defect fails its named council gate with <c>KW-REVIEW-022</c>, never
    /// <c>KW-REVIEW-033</c>. The agent run gates the defected file: when the agent selects
    /// the gate it must fail too; an agent whose task gates do not cover the defect only has
    /// to start every gate it runs.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefectCases))]
    public void SeededDefectFailsItsNamedGate(
        string family, string agent, string sourcePath, string defect, string expectedGateId)
    {
        RequireToolchain();

        string repo = NewToolchainRepo(family, Path.Combine("defects", defect));

        string councilReport = NewReportPath("council");
        CliRun council = Gates(CouncilArguments(repo, councilReport));
        output.WriteLine($"{family} defect {defect} council exit code: {council.ExitCode}");
        AssertExitCode(1, council);
        Assert.Contains(FailedCode, council.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(NotStartedCode, council.Output, StringComparison.Ordinal);
        AssertGateFailed(ReadReport(councilReport), expectedGateId);

        string agentReport = NewReportPath("agent");
        CliRun gated = Gates(AgentArguments(repo, agent, sourcePath, agentReport));
        output.WriteLine($"{family} defect {defect} agent ({agent}) exit code: {gated.ExitCode}");
        Assert.DoesNotContain(NotStartedCode, gated.Output, StringComparison.Ordinal);
        using JsonDocument report = ReadReport(agentReport);
        if (GateIds(report).Contains(expectedGateId, StringComparer.Ordinal))
        {
            Assert.False(
                GatePassed(report, expectedGateId),
                $"Gate '{expectedGateId}' was reported passed, but the '{defect}' defect seeds its failure.");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Toolchain gate and repository setup.
    // -----------------------------------------------------------------------------------------

    private static void RequireToolchain()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(ToolchainFlag), "1", StringComparison.Ordinal))
        {
            Assert.Skip(
                $"Gate-catalogue toolchain run needs {ToolchainFlag}=1 with the real toolchains " +
                "installed; it runs in the gate-catalogue-toolchains CI job, not the default unit-test run.");
        }
    }

    /// <summary>
    /// Copies the fixture variant into a fresh temp git repository, declares the family's
    /// technologies, runs <c>docs init</c> in-process, and commits the scaffolded tree.
    /// Never builds in place: the repository's own build props must not apply to the fixture.
    /// </summary>
    private string NewToolchainRepo(string family, string variant)
    {
        string source = Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "fixtures", "gate-catalogue", family, variant);
        Assert.True(
            Directory.Exists(source),
            $"Gate-catalogue fixture '{source}' is missing; the toolchain run has nothing to copy.");

        string repo = Path.Combine(_temp.Path, family + "-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(source, repo);
        MustGit(repo, "init", "--quiet");
        WriteHostConfig(repo, TechnologiesFor(family));
        MustGit(repo, "add", "-A");
        Commit(repo, "fixture");

        CapturedConsoleExecution<int> init = ProcessConsoleCapture.Run(
            () => DocsApp().Run(["docs", "init", repo, "--no-skill"]));
        Assert.True(
            init.Result == 0,
            $"docs init exited {init.Result} on the {family} {variant} fixture. " +
            $"Output:{Environment.NewLine}{init.Output}");

        MustGit(repo, "add", "-A");
        Commit(repo, "docs init");

        if (string.Equals(family, "typescript", StringComparison.Ordinal))
        {
            NpmCi(repo);
        }

        return repo;
    }

    private static IReadOnlyList<string> TechnologiesFor(string family)
    {
        GateCatalogueFamily catalogue = GateCatalogue.LoadEmbedded()
            .Single(candidate => string.Equals(candidate.Family, family, StringComparison.Ordinal));
        return catalogue.Technologies;
    }

    /// <summary>
    /// Installs the typescript fixture's locked dependencies inside the temp copy, so the
    /// <c>npm run</c> gates test the fixture rather than the network. Reports the transcript
    /// on failure: a failed install is a broken premise, not a failed gate.
    /// </summary>
    private void NpmCi(string repo)
    {
        ProcessStartInfo startInfo = new("npm")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("ci");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromMinutes(10));
        output.WriteLine($"typescript npm ci exit code: {result.ExitCode}");
        Assert.True(
            result.ExitCode == 0,
            $"npm ci exited {result.ExitCode} in '{repo}'. " +
            $"Stdout:{Environment.NewLine}{result.StandardOutput}" +
            $"Stderr:{Environment.NewLine}{result.StandardError}");
    }

    // -----------------------------------------------------------------------------------------
    // Gate invocations and report assertions.
    // -----------------------------------------------------------------------------------------

    private string NewReportPath(string label)
    {
        return Path.Combine(_temp.Path, label + "-" + Guid.NewGuid().ToString("N") + ".json");
    }

    private static string[] CouncilArguments(string repo, string reportPath)
    {
        return ["review", "gates", repo, "--format", "json", "--out", reportPath];
    }

    private static string[] AgentArguments(string repo, string agent, string sourcePath, string reportPath)
    {
        return
        [
            "review", "gates", repo,
            "--agent", agent,
            "--task", TaskSlug,
            "--paths", sourcePath,
            "--format", "json",
            "--out", reportPath
        ];
    }

    /// <summary>
    /// The CLI boundary: a strict-parsing <see cref="CommandApp"/> with the docs and review
    /// branches registered, driven with console output captured and the process-wide console
    /// lock held. The report path travels in the arguments, so parallel cases never share one.
    /// </summary>
    private static CliRun Gates(string[] arguments)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CreateApp().Run(arguments));
        return new CliRun(execution.Result, execution.Output);
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

    private static CommandApp CreateApp()
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.UseStrictParsing();
            config.AddBranch("docs", docs => { docs.AddCommand<DocsInitCommand>("init"); });
            config.AddBranch("review", review => { review.AddCommand<ReviewGatesCommand>("gates"); });
        });
        return app;
    }

    private static JsonDocument ReadReport(string reportPath)
    {
        Assert.True(
            File.Exists(reportPath),
            $"review gates wrote no gate report at '{reportPath}'.");
        return JsonDocument.Parse(File.ReadAllText(reportPath));
    }

    private static string[] GateIds(JsonDocument report)
    {
        return report.RootElement.GetProperty("gates").EnumerateArray()
            .Select(gate => gate.GetProperty("id").GetString() ?? string.Empty)
            .ToArray();
    }

    private static bool GatePassed(JsonDocument report, string id)
    {
        return report.RootElement.GetProperty("gates").EnumerateArray()
            .Single(gate => gate.GetProperty("id").GetString() == id)
            .GetProperty("passed").GetBoolean();
    }

    private static void AssertAllGatesPassed(JsonDocument report)
    {
        using (report)
        {
            string[] failed = report.RootElement.GetProperty("gates").EnumerateArray()
                .Where(gate => !gate.GetProperty("passed").GetBoolean())
                .Select(gate => gate.GetProperty("id").GetString() ?? string.Empty)
                .ToArray();
            Assert.True(
                report.RootElement.GetProperty("gates").GetArrayLength() > 0,
                "The run reported no gates, so passing proves nothing.");
            Assert.True(
                failed.Length == 0,
                $"Clean fixture gates reported failed: {string.Join(", ", failed)}.");
        }
    }

    private static void AssertGateFailed(JsonDocument report, string expectedGateId)
    {
        using (report)
        {
            Assert.Contains(expectedGateId, GateIds(report));
            Assert.False(
                GatePassed(report, expectedGateId),
                $"Gate '{expectedGateId}' was reported passed, but its defect was seeded.");
        }
    }

    private static void AssertExitCode(int expected, CliRun run)
    {
        Assert.True(
            expected == run.ExitCode,
            $"Expected exit code {expected}, got {run.ExitCode}. Output:{Environment.NewLine}{run.Output}");
    }

    // -----------------------------------------------------------------------------------------
    // Repository fixtures. Each file operation is made under the temporary root only.
    // -----------------------------------------------------------------------------------------

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

        string directory = Path.Combine(repo, Path.GetDirectoryName(ConfigRelativePath)!);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(repo, ConfigRelativePath), yaml.ToString());
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

    private static void Commit(string repo, string message)
    {
        MustGit(
            repo,
            "-c", "user.name=ship-gate-13",
            "-c", "user.email=ship-gate-13@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "--quiet", "-m", message);
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
            CreateNoWindow = true
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

    private sealed record CliRun(int ExitCode, string Output);
}

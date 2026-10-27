using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Processes;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins how <c>kyber-weave review gates</c> without <c>--agent</c> selects the repository-wide council gates
/// by their <c>include</c> and <c>os</c> keys, and how it reports a changed set it cannot determine. Driven
/// through a real <see cref="CommandApp"/> against a temporary git repository with its own
/// <c>.kyber-weave/kyber-weave.yml</c>.
/// </summary>
/// <remarks>
/// The only program any gate runs is <c>git</c>, or a program the test names on purpose to be missing, so the
/// same assertions hold on macOS and Linux. Assertions read the boundary a caller sees: the exit code, the rule
/// identifiers rendered to the console, and the gate report written by <c>--out</c>. A gate that must not start
/// proves it by leaving no mark in the repository's own git configuration, which the test reads back.
/// </remarks>
public sealed class ReviewGatesCouncilCommandTests : IDisposable
{
    private const string GateSkipped = "KW-REVIEW-026";
    private const string InvalidInput = "KW-REVIEW-028";
    private const string GateFailed = "KW-REVIEW-022";
    private const string GateNotStarted = "KW-REVIEW-033";
    private const string ChangedSetUnknown = "KW-REVIEW-034";
    private const int ClientInputExitCode = 2;
    private const string StartedMarkerKey = "council.started";
    private const string MissingProgram = "kw-task3-no-such-program";
    private readonly TempDirectory _temp = new();
    private readonly string _repo;

    public ReviewGatesCouncilCommandTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(_repo);

        MustGit("init", "--quiet");
        WriteRepositoryFile("src/a.cs", "// a\n");
        WriteRepositoryFile("notes/readme.md", "# readme\n");
        MustGit("add", "-A");
        Commit("initial");
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    // -----------------------------------------------------------------------------------------
    // Include-based selection: a council gate runs when its include matches the changed set.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AGateWhoseIncludeMatchesNoChangedFileIsSkippedAndNotReportedPassed()
    {
        // Without this skip, every council gate runs on every change, however small.
        WriteConfiguration("""
                           review:
                             gates:
                               - id: markdown-lint
                                 include: ["**/*.md"]
                                 run: [git, --version]
                           """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--base", "HEAD", "--format", "json"));

        AssertExitCode(0, run);
        AssertDiagnostic(run.Output, GateSkipped, "INFO");
        using JsonDocument report = ReadReport();
        AssertNotReportedPassed(report, "markdown-lint");
    }

    [Fact]
    public void AGateWhoseIncludeMatchesAChangedFileRuns()
    {
        WriteConfiguration("""
                           review:
                             gates:
                               - id: csharp-lint
                                 include: ["**/*.cs"]
                                 run: [git, --version]
                           """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--base", "HEAD"));

        AssertExitCode(0, run);
        Assert.DoesNotContain(GateSkipped, run.Output, StringComparison.Ordinal);
        using JsonDocument report = ReadReport();
        Assert.Equal(["csharp-lint"], GateIds(report));
        Assert.True(GatePassed(report, "csharp-lint"));
    }

    [Fact]
    public void AGateWithNoIncludeRunsEvenWhenNothingChanged()
    {
        // A gate with no include is not scoped by the changed set, so it runs on every change.
        WriteConfiguration("""
                           review:
                             gates:
                               - id: always-on
                                 run: [git, --version]
                           """);

        CliRun run = Cli(GatesCommand());

        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.Equal(["always-on"], GateIds(report));
        Assert.True(GatePassed(report, "always-on"));
    }

    [Fact]
    public void AGateForAnotherOperatingSystemIsSkippedAndNotReportedPassed()
    {
        WriteConfiguration($"""
                            review:
                              gates:
                                - id: other-os-probe
                                  os: {OtherOs}
                                  run: [git, --version]
                            """);

        CliRun run = Cli(GatesCommand("--format", "json"));

        AssertExitCode(0, run);
        AssertDiagnostic(run.Output, GateSkipped, "INFO");
        using JsonDocument report = ReadReport();
        AssertNotReportedPassed(report, "other-os-probe");
    }

    // -----------------------------------------------------------------------------------------
    // Unknown changed set: an unresolvable base runs every include gate and says why.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AnUnresolvableBaseRunsEveryIncludeGateAndSaysWhyNoneWasSkipped()
    {
        // Fail closed: a gate dropped on a guess would hide the evidence it exists to give.
        WriteConfiguration("""
                           review:
                             gates:
                               - id: markdown-lint
                                 include: ["**/*.md"]
                                 run: [git, --version]
                           """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--base", "no-such-ref", "--format", "json"));

        AssertExitCode(0, run);
        AssertDiagnostic(run.Output, ChangedSetUnknown, "INFO");
        AssertRendered(run, "gates with include ran unscoped");
        Assert.DoesNotContain(GateSkipped, run.Output, StringComparison.Ordinal);
        using JsonDocument report = ReadReport();
        Assert.Equal(["markdown-lint"], GateIds(report));
        Assert.True(GatePassed(report, "markdown-lint"));
    }

    // -----------------------------------------------------------------------------------------
    // Agent-only options: --paths and --base scope council gates; --task and --fix still need --agent.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void PathsAreAcceptedWithoutAgentAndScopeTheRun()
    {
        WriteConfiguration("""
                           review:
                             gates:
                               - id: csharp-lint
                                 include: ["**/*.cs"]
                                 run: [git, --version]
                               - id: markdown-lint
                                 include: ["**/*.md"]
                                 run: [git, --version]
                           """);

        CliRun run = Cli(GatesCommand("--paths", "src/a.cs", "--format", "json"));

        AssertExitCode(0, run);
        AssertDiagnostic(run.Output, GateSkipped, "INFO");
        using JsonDocument report = ReadReport();
        Assert.Equal(["csharp-lint"], GateIds(report));
    }

    [Theory]
    [InlineData("--task=t2r")]
    [InlineData("--fix")]
    public void TaskAndFixStillRequireAgent(string flag)
    {
        WriteConfiguration(RepositoryGateConfiguration);

        CliRun run = Cli(GatesCommand(flag));

        AssertExitCode(ClientInputExitCode, run);
        Assert.Contains("agent", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--paths=../outside.cs")]
    [InlineData("--paths=-rf")]
    [InlineData("--base=--output=x")]
    public void AnUnsafeInputIsRefusedBeforeAnyCouncilGateStarts(string unsafeOption)
    {
        // Refused input reports KW-REVIEW-028 and exits 1, the same as with --agent; no gate may start.
        WriteConfiguration(MustNotRunConfiguration);

        CliRun run = Cli(GatesCommand(unsafeOption));

        AssertExitCode(1, run);
        AssertRendered(run, InvalidInput);
        Assert.NotEqual(0, Git("config", "--local", "--get", StartedMarkerKey).ExitCode);
    }

    // -----------------------------------------------------------------------------------------
    // A gate whose program does not exist: reported as not started, distinct from a failed check.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AGateWhoseProgramIsMissingIsReportedAsNotStartedNotAsFailed()
    {
        WriteConfiguration($"""
                            review:
                              gates:
                                - id: missing-tool
                                  blocking: true
                                  run: [{MissingProgram}]
                            """);

        CliRun run = Cli(GatesCommand("--format", "json"));

        AssertExitCode(1, run);
        AssertDiagnostic(run.Output, GateNotStarted, "ERROR");
        AssertRendered(run, MissingProgram);
        Assert.DoesNotContain(GateFailed, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingProgramInANonBlockingGateIsAWarningAndDoesNotFailTheRun()
    {
        WriteConfiguration($"""
                            review:
                              gates:
                                - id: missing-tool
                                  blocking: false
                                  run: [{MissingProgram}]
                            """);

        CliRun run = Cli(GatesCommand("--format", "json"));

        AssertExitCode(0, run);
        AssertDiagnostic(run.Output, GateNotStarted, "WARNING");
        Assert.DoesNotContain(GateFailed, run.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // Configuration fixtures and harness.
    // -----------------------------------------------------------------------------------------

    /// <summary>A probe that must never start; starting it sets a marker that the test reads back.</summary>
    private static string MustNotRunConfiguration => $"""
                                                      review:
                                                        gates:
                                                          - id: must-not-run
                                                            include: ["**/*.cs"]
                                                            run: [git, config, --local, {StartedMarkerKey}, yes]
                                                      """;

    /// <summary>The repository-wide <c>review.gates</c> list, with one ungated probe.</summary>
    private static string RepositoryGateConfiguration => """
                                                         review:
                                                           gates:
                                                             - id: repository-wide
                                                               run: [git, --version]
                                                         """;

    private static string OtherOs => OperatingSystem.IsWindows() ? "unix" : "windows";

    private string ReportPath => Path.Combine(_temp.Path, "gates.json");

    /// <summary>
    /// The <c>review gates</c> invocation without <c>--agent</c>, always writing the report to a path
    /// inside the temporary root so the test can read it back.
    /// </summary>
    private string[] GatesCommand(params string[] options)
    {
        List<string> arguments = ["review", "gates", _repo];
        arguments.AddRange(options);
        arguments.AddRange(["--out", ReportPath]);
        return [.. arguments];
    }

    /// <summary>
    /// The CLI boundary: a strict-parsing <see cref="CommandApp"/> with the review branch registered,
    /// driven with console output captured and the process-wide console lock held.
    /// </summary>
    private static CliRun Cli(string[] arguments)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => CreateApp().Run(arguments));
        return new CliRun(execution.Result, execution.Output);
    }

    private static CommandApp CreateApp()
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

    private JsonDocument ReadReport()
    {
        return JsonDocument.Parse(File.ReadAllText(ReportPath));
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

    /// <summary>A skipped gate may be absent from the report; if it is present it must not read as passed.</summary>
    private static void AssertNotReportedPassed(JsonDocument report, string id)
    {
        if (GateIds(report).Contains(id))
        {
            Assert.False(GatePassed(report, id), $"Gate '{id}' was reported passed, but it should have been skipped.");
        }
    }

    private static void AssertExitCode(int expected, CliRun run)
    {
        Assert.True(
            run.ExitCode == expected,
            $"Expected exit code {expected} but was {run.ExitCode}. Output:{Environment.NewLine}{run.Output}");
    }

    private static void AssertRendered(CliRun run, string text)
    {
        Assert.True(
            run.Output.Contains(text, StringComparison.Ordinal),
            $"Expected '{text}' in the output. Output:{Environment.NewLine}{run.Output}");
    }

    /// <summary>Asserts a diagnostic with the given code and severity in the <c>--format json</c> payload.</summary>
    private static void AssertDiagnostic(string output, string code, string severity)
    {
        char[] opening = ['{', '['];
        char[] closing = ['}', ']'];
        int first = output.IndexOfAny(opening);
        int last = output.LastIndexOfAny(closing);
        Assert.True(first >= 0 && last > first, $"No JSON payload in the output:{Environment.NewLine}{output}");

        using JsonDocument document = JsonDocument.Parse(output[first..(last + 1)]);
        Assert.True(
            ContainsDiagnostic(document.RootElement, code, severity),
            $"Expected {code} at severity {severity}. Output:{Environment.NewLine}{output}");
    }

    private static bool ContainsDiagnostic(JsonElement element, string code, string severity)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (ContainsDiagnostic(item, code, severity))
                    return true;
            }

            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty("code", out JsonElement foundCode)
            && foundCode.GetString() == code
            && element.TryGetProperty("severity", out JsonElement foundSeverity)
            && foundSeverity.GetString() == severity)
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (ContainsDiagnostic(property.Value, code, severity))
                return true;
        }

        return false;
    }

    /// <summary>Writes the configuration and commits it, so the working tree stays clean for the scenario.</summary>
    private void WriteConfiguration(string yaml)
    {
        WriteRepositoryFile(".kyber-weave/kyber-weave.yml", yaml);
        MustGit("add", "-A");
        Commit("configure gates");
    }

    private void WriteRepositoryFile(string relativePath, string content)
    {
        string path = Path.Combine(_repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Changes a tracked file, leaving it modified and unstaged.</summary>
    private void ChangeTracked(string relativePath)
    {
        WriteRepositoryFile(relativePath, "// changed\n");
    }

    private void Commit(string message)
    {
        MustGit(
            "-c", "user.name=task3",
            "-c", "user.email=task3@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--allow-empty", "-m", message);
    }

    private void MustGit(params string[] arguments)
    {
        (int exitCode, string output) = Git(arguments);
        Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {output}");
    }

    private (int ExitCode, string Output) Git(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = _repo,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        return (result.ExitCode, result.StandardOutput);
    }

    private sealed record CliRun(int ExitCode, string Output);
}

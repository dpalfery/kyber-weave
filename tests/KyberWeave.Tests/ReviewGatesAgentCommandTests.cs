using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Processes;
using Spectre.Console.Cli;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the per-agent task-gate contract of <c>kyber-weave review gates --agent</c>, driven through a
/// real <see cref="CommandApp"/> against a temporary git repository that carries its own
/// <c>.kyber-weave/kyber-weave.yml</c>.
/// </summary>
/// <remarks>
/// The only program any gate runs is <c>git</c>, which every host the suite targets already has, so
/// the same assertions hold on macOS and Linux. Assertions read the boundary a caller sees: the exit
/// code, the rule identifiers rendered to the console, and the gate report written by <c>--out</c>.
/// A gate that must not start proves it by leaving a mark in the repository's own git configuration,
/// which the test reads back; a gate that ran cannot hide behind a missing diagnostic.
/// </remarks>
public sealed class ReviewGatesAgentCommandTests : IDisposable
{
    private const string ReportSchema = "kyber-weave.review-gates/v1";
    private const string NoGatesDeclared = "KW-REVIEW-020";
    private const string GateSkipped = "KW-REVIEW-026";
    private const string NoGatesForAgent = "KW-REVIEW-027";
    private const string InvalidInput = "KW-REVIEW-028";
    private const string FixFailed = "KW-REVIEW-029";
    private const int ClientInputExitCode = 2;
    private const string StartedMarkerKey = "t2r.started";
    private const string FixedMarkerKey = "t2r.fixed";
    private readonly TempDirectory _temp = new();
    private readonly string _repo;

    public ReviewGatesAgentCommandTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(_repo);

        MustGit("init", "--quiet");
        WriteRepositoryFile("src/a.cs", "// a\n");
        WriteRepositoryFile("src/gone.cs", "// gone\n");
        WriteRepositoryFile("notes/readme.md", "# readme\n");
        MustGit("add", "-A");
        Commit("initial");
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    // -----------------------------------------------------------------------------------------
    // Agent selection: --agent runs that agent's task gates and nothing else.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AnAgentRunsOnlyItsOwnTaskGatesAndNotTheRepositoryReviewGates()
    {
        WriteConfiguration("""
                           review:
                             gates:
                               - id: repository-wide
                                 run: [git, --version]
                             task-gates:
                               csharp-dev:
                                 - id: csharp-probe
                                   include: ["**/*.cs"]
                                   run: [git, --version]
                           """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.Equal(["csharp-probe"], GateIds(report));
        Assert.Equal(ReportSchema, report.RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public void AnAgentWithNoTaskGatesIsToldSoAndRunsNothing()
    {
        WriteConfiguration("""
                           review:
                             gates:
                               - id: repository-wide
                                 run: [git, --version]
                             task-gates:
                               csharp-dev:
                                 - id: csharp-probe
                                   include: ["**/*.cs"]
                                   run: [git, --version]
                           """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "docs-writer"));

        AssertExitCode(0, run);
        AssertRendered(run, NoGatesForAgent);
        using JsonDocument report = ReadReport();
        Assert.Empty(GateIds(report));
    }

    // -----------------------------------------------------------------------------------------
    // {changed} expansion: one argv entry per matching path, from --paths.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void EachMatchingPathBecomesItsOwnArgvEntry()
    {
        WriteConfiguration(TrackedProbeConfiguration);

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", "--paths", "src/a.cs", "--paths", "src/gone.cs"));

        // Joined into one argument, the two tracked names would not match a single file and git would fail.
        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.True(GatePassed(report, "tracked-probe"));
    }

    [Fact]
    public void AnUntrackedMatchingPathFailsTheProbeAndBlocks()
    {
        WriteConfiguration(TrackedProbeConfiguration);
        AddUntracked("src/new.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", "--paths", "src/a.cs", "--paths", "src/new.cs"));

        AssertExitCode(1, run);
        using JsonDocument report = ReadReport();
        Assert.False(GatePassed(report, "tracked-probe"));
    }

    [Fact]
    public void PathsOutsideIncludeAreDroppedBeforeTheyReachTheArgv()
    {
        WriteConfiguration(TrackedProbeConfiguration);
        AddUntracked("notes/todo.md");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", "--paths", "src/a.cs", "--paths", "notes/todo.md"));

        // notes/todo.md is untracked; if the include filter let it through, the probe would fail.
        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.True(GatePassed(report, "tracked-probe"));
    }

    // -----------------------------------------------------------------------------------------
    // Default changed set: git diff against --base (default HEAD) plus untracked files.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void WithoutPathsTheChangedSetIncludesUntrackedFiles()
    {
        WriteConfiguration(TrackedProbeConfiguration);
        ChangeTracked("src/a.cs");
        AddUntracked("src/new.cs");
        AddUntracked("notes/todo.md");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(1, run);
        using JsonDocument report = ReadReport();
        Assert.False(GatePassed(report, "tracked-probe"));
    }

    [Fact]
    public void WithoutPathsAModifiedTrackedFileAloneRunsTheGate()
    {
        WriteConfiguration(TrackedProbeConfiguration);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.Equal(["tracked-probe"], GateIds(report));
    }

    [Fact]
    public void DeletedFilesAreLeftOutOfTheChangedSet()
    {
        WriteConfiguration(HashObjectProbeConfiguration);
        File.Delete(Path.Combine(_repo, "src", "gone.cs"));

        // hash-object fails on a missing file, so the probe would block if the deletion were included.
        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(0, run);
        AssertRendered(run, GateSkipped);
        using JsonDocument report = ReadReport();
        Assert.Empty(GateIds(report));
    }

    [Fact]
    public void WithoutBaseTheDiffIsAgainstHeadSoCommittedWorkIsNotChanged()
    {
        WriteConfiguration(TrackedProbeConfiguration);
        CommitFile("src/later.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(0, run);
        AssertRendered(run, GateSkipped);
        using JsonDocument report = ReadReport();
        Assert.Empty(GateIds(report));
    }

    [Fact]
    public void ABaseRefWidensTheDiffToCommitsSinceThatRef()
    {
        WriteConfiguration(TrackedProbeConfiguration);
        CommitFile("src/later.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", "--base", "HEAD~1"));

        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.Equal(["tracked-probe"], GateIds(report));
    }

    // -----------------------------------------------------------------------------------------
    // Skip conditions: no matching file, or an os that does not match this host.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AGateWithNoMatchingFileIsSkippedWithDiagnosticAndExitsZero()
    {
        WriteConfiguration(TrackedProbeConfiguration);
        AddUntracked("notes/todo.md");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(0, run);
        AssertRendered(run, GateSkipped);
        using JsonDocument report = ReadReport();
        Assert.Empty(GateIds(report));
    }

    [Fact]
    public void AGateForAnotherOperatingSystemIsSkippedWithDiagnosticAndExitsZero()
    {
        WriteConfiguration($"""
                            review:
                              task-gates:
                                csharp-dev:
                                  - id: other-os-probe
                                    os: {OtherOs}
                                    include: ["**/*.cs"]
                                    run: [git, --version]
                            """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(0, run);
        AssertRendered(run, GateSkipped);
        using JsonDocument report = ReadReport();
        Assert.Empty(GateIds(report));
    }

    [Fact]
    public void AGateForThisOperatingSystemRuns()
    {
        WriteConfiguration($"""
                            review:
                              task-gates:
                                csharp-dev:
                                  - id: this-os-probe
                                    os: {CurrentOs}
                                    include: ["**/*.cs"]
                                    run: [git, --version]
                            """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.Equal(["this-os-probe"], GateIds(report));
    }

    // -----------------------------------------------------------------------------------------
    // Unsafe input: refused with KW-REVIEW-028 before any gate process starts.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("absolute")]
    [InlineData("escaping")]
    [InlineData("leading-dash")]
    public void AnUnsafePathIsRefusedBeforeAnyGateStarts(string kind)
    {
        WriteConfiguration(MustNotRunConfiguration);
        ChangeTracked("src/a.cs");
        string pathOption = kind switch
        {
            "absolute" => $"--paths={Path.Combine(_repo, "src", "a.cs")}",
            "escaping" => "--paths=../outside.cs",
            _ => "--paths=-rf"
        };

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", pathOption));

        AssertExitCode(1, run);
        AssertRendered(run, InvalidInput);
        Assert.False(MarkerWasSet());
    }

    /// <summary>
    /// <c>--base</c> is a git ref. A value that starts with a dash would be read by git as an option,
    /// so it is refused before the agent is looked up and before any gate starts.
    /// </summary>
    [Fact]
    public void ALeadingDashBaseRefIsRefusedBeforeAnyGateStarts()
    {
        WriteConfiguration(MustNotRunConfiguration);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", "--base=--output=x"));

        AssertExitCode(1, run);
        AssertRendered(run, InvalidInput);
        Assert.False(MarkerWasSet());
    }

    /// <summary>
    /// On unix a backslash is an ordinary file-name character, so <c>a\b.cs</c> names one file. The path
    /// must reach argv as written; rewriting it to <c>a/b.cs</c> would point the gate at a different file.
    /// The probe passes only when the file exists under the name it was given.
    /// </summary>
    [Fact]
    public void ABackslashInAChangedFileNameReachesArgvAsWritten()
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("A backslash is a path separator on Windows, so this file name cannot exist there.");

        WriteRepositoryFile(@"a\b.cs", "// backslash\n");
        WriteConfiguration(HashObjectProbeConfiguration);

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", @"--paths=a\b.cs"));

        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.True(GatePassed(report, "hash-probe"));
    }

    /// <summary>
    /// A changed path whose file, or a directory above it, is a symbolic link leading outside the
    /// repository is refused before any gate starts. The gate would otherwise read a file the
    /// repository does not contain.
    /// </summary>
    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    public void ASymlinkLeadingOutsideTheRepositoryIsRefusedBeforeAnyGateStarts(string kind)
    {
        if (!CanCreateSymbolicLinks())
            throw SkipException.ForSkip("This host cannot create symbolic links.");

        string outside = Path.Combine(_temp.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "escape.cs"), "// outside\n");
        WriteConfiguration(MustNotRunConfiguration);

        string changed;
        if (kind == "file")
        {
            changed = "src/link.cs";
            File.CreateSymbolicLink(Path.Combine(_repo, "src", "link.cs"), Path.Combine(outside, "escape.cs"));
        }
        else
        {
            changed = "linked/escape.cs";
            Directory.CreateSymbolicLink(Path.Combine(_repo, "linked"), outside);
        }

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", $"--paths={changed}"));

        AssertExitCode(1, run);
        AssertRendered(run, InvalidInput);
        Assert.False(MarkerWasSet());
    }

    /// <summary>Whether this host lets the test process create a symbolic link, probed in the temporary root.</summary>
    private bool CanCreateSymbolicLinks()
    {
        string probe = Path.Combine(_temp.Path, "symlink-probe");
        try
        {
            File.CreateSymbolicLink(probe, Path.Combine(_temp.Path, "symlink-target"));
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void ATaskPlaceholderWithoutTaskFlagIsRefusedBeforeAnyGateStarts()
    {
        WriteConfiguration(TaskPlaceholderConfiguration);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(1, run);
        AssertRendered(run, InvalidInput);
        Assert.False(MarkerWasSet());
    }

    [Theory]
    [InlineData("Has Space")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("-leading")]
    [InlineData("UPPER")]
    public void AnInvalidTaskSlugIsRefusedBeforeAnyGateStarts(string slug)
    {
        WriteConfiguration(TaskPlaceholderConfiguration);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", $"--task={slug}"));

        AssertExitCode(1, run);
        AssertRendered(run, InvalidInput);
        Assert.False(MarkerWasSet());
    }

    [Fact]
    public void AValidTaskSlugIsSubstitutedIntoEveryPlaceholder()
    {
        WriteRepositoryFile(".agent_scratchpad/t2r-probe/marker.txt", "marker\n");
        WriteConfiguration(ScratchpadProbeConfiguration);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", "--task", "t2r-probe"));

        // The probe names the substituted path; an unsubstituted placeholder would not be tracked.
        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.Equal(["scratchpad-probe"], GateIds(report));
    }

    // -----------------------------------------------------------------------------------------
    // --fix: the fix step runs before the verify step, and a failed fix never decides alone.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void FixRunsBeforeRunSoTheVerifyStepSeesItsEffect()
    {
        WriteConfiguration(FixConfiguration);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", "--fix"));

        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.True(GatePassed(report, "formatted"));
    }

    [Fact]
    public void WithoutFixTheFixStepIsNotRunAndTheVerifyStepDecides()
    {
        WriteConfiguration(FixConfiguration);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(1, run);
        Assert.False(FixWasApplied());
        using JsonDocument report = ReadReport();
        Assert.False(GatePassed(report, "formatted"));
    }

    [Fact]
    public void AFailingFixIsAWarningAndTheVerifyStepStillDecides()
    {
        WriteConfiguration("""
                           review:
                             task-gates:
                               csharp-dev:
                                 - id: formatted
                                   include: ["**/*.cs"]
                                   fix: [git, --no-such-option]
                                   run: [git, --version]
                           """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", "--fix", "--format", "json"));

        AssertExitCode(0, run);
        AssertDiagnostic(run.Output, FixFailed, "WARNING");
    }

    [Fact]
    public void AFailingFixDoesNotPassAVerifyThatStillFails()
    {
        WriteConfiguration("""
                           review:
                             task-gates:
                               csharp-dev:
                                 - id: formatted
                                   include: ["**/*.cs"]
                                   fix: [git, --no-such-option]
                                   run: [git, config, --local, --get, t2r.missing]
                           """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev", "--fix"));

        AssertExitCode(1, run);
        AssertRendered(run, FixFailed);
    }

    // -----------------------------------------------------------------------------------------
    // Verdict of the run: blocking failures exit 1, and the report carries the schema.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ABlockingFailureExitsOneAndTheReportCarriesTheCurrentSchema()
    {
        WriteConfiguration("""
                           review:
                             task-gates:
                               csharp-dev:
                                 - id: verify
                                   include: ["**/*.cs"]
                                   run: [git, config, --local, --get, t2r.missing]
                           """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(1, run);
        using JsonDocument report = ReadReport();
        Assert.Equal(ReportSchema, report.RootElement.GetProperty("schema").GetString());
        JsonElement gate = GateElement(report, "verify");
        Assert.True(gate.GetProperty("blocking").GetBoolean());
        Assert.False(gate.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void ANonBlockingFailureIsReportedAndDoesNotFailTheRun()
    {
        WriteConfiguration("""
                           review:
                             task-gates:
                               csharp-dev:
                                 - id: advisory
                                   blocking: false
                                   include: ["**/*.cs"]
                                   run: [git, config, --local, --get, t2r.missing]
                           """);
        ChangeTracked("src/a.cs");

        CliRun run = Cli(GatesCommand("--agent", "csharp-dev"));

        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.False(GatePassed(report, "advisory"));
    }

    // -----------------------------------------------------------------------------------------
    // Client input: the agent-scoped flags without --agent are refused with exit 2.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("--paths=src/a.cs")]
    [InlineData("--base=HEAD")]
    [InlineData("--task=t2r")]
    [InlineData("--fix")]
    public void AnAgentOnlyFlagWithoutAgentIsAClientInputError(string flag)
    {
        WriteConfiguration(RepositoryGateConfiguration);

        CliRun run = Cli(GatesCommand(flag));

        AssertExitCode(ClientInputExitCode, run);
        Assert.Contains("agent", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------------------------
    // Without --agent, today's behaviour is unchanged.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void WithoutAgentARepositoryWithNoGatesIsReportedAsHavingNoEvidence()
    {
        CliRun run = Cli(GatesCommand());

        // The empty case returns before any report is written, so the rule id on the console is the evidence.
        AssertExitCode(0, run);
        AssertRendered(run, NoGatesDeclared);
    }

    [Fact]
    public void WithoutAgentTheRepositoryReviewGatesRunAsBefore()
    {
        WriteConfiguration(RepositoryGateConfiguration);

        CliRun run = Cli(GatesCommand());

        AssertExitCode(0, run);
        using JsonDocument report = ReadReport();
        Assert.Equal(["repository-wide"], GateIds(report));
    }

    // -----------------------------------------------------------------------------------------
    // Configuration fixtures. Each is a complete kyber-weave.yml for one scenario.
    // -----------------------------------------------------------------------------------------

    /// <summary>A probe that passes only when every changed path is tracked by git.</summary>
    private static string TrackedProbeConfiguration => """
                                                       review:
                                                         task-gates:
                                                           csharp-dev:
                                                             - id: tracked-probe
                                                               include: ["**/*.cs"]
                                                               run: [git, ls-files, --error-unmatch, "{changed}"]
                                                       """;

    /// <summary>A probe that fails on any missing file, so it blocks if a deleted path is passed in.</summary>
    private static string HashObjectProbeConfiguration => """
                                                          review:
                                                            task-gates:
                                                              csharp-dev:
                                                                - id: hash-probe
                                                                  include: ["**/*.cs"]
                                                                  run: [git, hash-object, "{changed}"]
                                                          """;

    /// <summary>A gate that must never start in the unsafe-input tests; starting it sets a marker.</summary>
    private static string MustNotRunConfiguration => $"""
                                                      review:
                                                        task-gates:
                                                          csharp-dev:
                                                            - id: must-not-run
                                                              include: ["**/*.cs"]
                                                              run: [git, config, --local, {StartedMarkerKey}, yes]
                                                      """;

    /// <summary>A gate whose argv carries a <c>{task}</c> placeholder.</summary>
    private static string TaskPlaceholderConfiguration => """
                                                          review:
                                                            task-gates:
                                                              csharp-dev:
                                                                - id: task-placeholder
                                                                  include: ["**/*.cs"]
                                                                  run: [git, config, --local, t2r.started, "{task}"]
                                                          """;

    /// <summary>A probe that passes only when <c>{task}</c> was substituted into its path.</summary>
    private static string ScratchpadProbeConfiguration => """
                                                          review:
                                                            task-gates:
                                                              csharp-dev:
                                                                - id: scratchpad-probe
                                                                  include: ["**/*.cs"]
                                                                  run: [git, ls-files, --error-unmatch, ".agent_scratchpad/{task}/marker.txt"]
                                                          """;

    /// <summary>A fix that marks the repository, and a verify that passes only once the mark exists.</summary>
    private static string FixConfiguration => $"""
                                               review:
                                                 task-gates:
                                                   csharp-dev:
                                                     - id: formatted
                                                       include: ["**/*.cs"]
                                                       fix: [git, config, --local, {FixedMarkerKey}, yes]
                                                       run: [git, config, --local, --get, {FixedMarkerKey}]
                                               """;

    /// <summary>The repository-wide <c>review.gates</c> list, with no task gates.</summary>
    private static string RepositoryGateConfiguration => """
                                                         review:
                                                           gates:
                                                             - id: repository-wide
                                                               run: [git, --version]
                                                         """;

    private static string CurrentOs => OperatingSystem.IsWindows() ? "windows" : "unix";

    private static string OtherOs => OperatingSystem.IsWindows() ? "unix" : "windows";

    // -----------------------------------------------------------------------------------------
    // Harness: a real CommandApp, the repository, and the report.
    // -----------------------------------------------------------------------------------------

    private string ReportPath => Path.Combine(_temp.Path, "gates.json");

    /// <summary>
    /// The <c>review gates</c> invocation against the temporary repository, always writing the
    /// report to a path inside the temporary root so the test can read it back.
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

    private static JsonElement GateElement(JsonDocument report, string id)
    {
        return report.RootElement.GetProperty("gates").EnumerateArray()
            .Single(gate => gate.GetProperty("id").GetString() == id);
    }

    private static bool GatePassed(JsonDocument report, string id)
    {
        return GateElement(report, id).GetProperty("passed").GetBoolean();
    }

    private static void AssertExitCode(int expected, CliRun run)
    {
        Assert.True(
            run.ExitCode == expected,
            $"Expected exit code {expected} but was {run.ExitCode}. Output:{Environment.NewLine}{run.Output}");
    }

    private static void AssertRendered(CliRun run, string code)
    {
        Assert.True(
            run.Output.Contains(code, StringComparison.Ordinal),
            $"Expected {code} in the output. Output:{Environment.NewLine}{run.Output}");
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

    private bool MarkerWasSet()
    {
        return Git("config", "--local", "--get", StartedMarkerKey).ExitCode == 0;
    }

    private bool FixWasApplied()
    {
        return Git("config", "--local", "--get", FixedMarkerKey).ExitCode == 0;
    }

    // -----------------------------------------------------------------------------------------
    // Repository fixtures. Each file operation is made under the temporary root only.
    // -----------------------------------------------------------------------------------------

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

    /// <summary>Adds a file git does not track.</summary>
    private void AddUntracked(string relativePath)
    {
        WriteRepositoryFile(relativePath, "// untracked\n");
    }

    /// <summary>Adds a file and commits it, so it is part of history but not of the working diff.</summary>
    private void CommitFile(string relativePath)
    {
        WriteRepositoryFile(relativePath, "// committed\n");
        MustGit("add", "-A");
        Commit($"add {relativePath}");
    }

    private void Commit(string message)
    {
        MustGit(
            "-c", "user.name=t2r",
            "-c", "user.email=t2r@example.invalid",
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

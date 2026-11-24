using System.Text.Json;
using KyberWeave.Cli.Commands.Run;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.RunStart;
using KyberWeave.Core.Squad.Briefs;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What <c>run start</c> prints and returns, over the shared console and environment fakes and stubbed services:
/// the report, the messages and the JSON document reach the consoles the command was given, and nothing here reads
/// the host's console or a repository.
/// </summary>
public sealed class RunStartCommandConsoleTests
{
    private const string Repository = "/kw/work/repo";

    private readonly TestConsole _markup = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _standard = new();
    private readonly StubRunners _runners = new() { Root = Repository };
    private readonly StubStarts _starts = new();
    private readonly StubBriefs _briefs = new();
    private readonly StubConfigs _configs = new();

    public RunStartCommandConsoleTests() => _markup.Profile.Width = 400;

    [Fact]
    public void Execute_ANonConventionalType_RefusesOnTheConsoleWithExitCodeTwo()
    {
        int exit = Run(new RunStartSettings { Harness = "claude", Type = "bogus", Slug = "x" });

        Assert.Equal(2, exit);
        Assert.StartsWith("kyber-weave run start: error: ", Output(), StringComparison.Ordinal);
        Assert.Empty(_starts.Requests);
    }

    [Fact]
    public void Execute_PathWithoutBrief_RefusesOnTheConsoleWithExitCodeTwo()
    {
        int exit = Run(Valid(new RunStartSettings { Path = "docs" }));

        Assert.Equal(2, exit);
        Assert.Contains("--path and --autonomy only apply with --brief.", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_TheAutonomyTheRunResolvesToIsHandedToTheStart()
    {
        _briefs.ResolvedAutonomy = "autonomous";

        Run(Valid(new RunStartSettings { Brief = true, Autonomy = "autonomous" }));

        Assert.Equal("autonomous", Assert.Single(_starts.Requests).Autonomy);
        Assert.Equal("autonomous", _briefs.GivenAutonomy);
    }

    [Fact]
    public void Execute_ARunWithNoAutonomySetIsStartedWithNone()
    {
        Run(Valid(new RunStartSettings()));

        Assert.Null(Assert.Single(_starts.Requests).Autonomy);
    }

    [Fact]
    public void Execute_OutsideARepository_FailsNamingTheCurrentDirectoryFromTheEnvironment()
    {
        _runners.Root = null;

        int exit = Run(Valid(new RunStartSettings()));

        Assert.Equal(1, exit);
        Assert.Contains("'/kw/work' is not inside a git repository", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_AConfigurationThatDoesNotLoad_WritesItsDiagnosticAndReturnsOne()
    {
        _configs.Failure = new Diagnostic("KW-CONFIG-001", Severity.Error, "unreadable", "kyber-weave.yml", Hint: "fix it");

        int exit = Run(Valid(new RunStartSettings()));

        Assert.Equal(1, exit);
        Assert.Equal("KW-CONFIG-001 unreadable\n  → fix it\n", Output());
    }

    [Fact]
    public void Execute_AFailedStart_PrintsTheServiceMessagesInOrderAndMapsTheStatusToTheExitCode()
    {
        _starts.Outcome = new RunStartOutcome(
            RunStartStatus.CodeGraphMissing,
            [new RunStartMessage(RunStartMessageKind.Error, "no codegraph"), new RunStartMessage(RunStartMessageKind.Remedy, "install it")],
            null);

        int exit = Run(Valid(new RunStartSettings()));

        Assert.Equal(3, exit);
        Assert.Equal("kyber-weave run start: error: no codegraph\n→ install it\n", Output());
    }

    [Fact]
    public void Execute_AFailedStartUnderJson_SendsTheMessagesToStandardError()
    {
        _starts.Outcome = new RunStartOutcome(
            RunStartStatus.ToolFailure,
            [new RunStartMessage(RunStartMessageKind.Error, "fetch failed", StandardErrorWhenJson: true)],
            null);

        int exit = Run(Valid(new RunStartSettings { Json = true }));

        Assert.Equal(1, exit);
        Assert.Equal("kyber-weave run start: error: fetch failed" + Environment.NewLine, _standard.ErrorOutput);
        Assert.Empty(Output());
    }

    [Fact]
    public void Execute_ASuccessfulStart_PrintsTheReportToTheConsole()
    {
        _starts.Outcome = Started(null);

        int exit = Run(Valid(new RunStartSettings { FailingVersion = "1.2.3" }));

        Assert.Equal(0, exit);
        Assert.Equal(
            "kyber-weave run start ready\n" +
            "  base      main (origin/main)\n" +
            "  branch    claude/fix/x\n" +
            "  where     in place\n" +
            "  index     fresh\n" +
            "  revision  abc123\n" +
            "  versions\n" +
            "    failing  1.2.3\n" +
            "    HEAD     1.0.0\n" +
            "    main     0.9.0\n" +
            "  server    origin: 1 ahead, 0 behind against main, for HEAD\n",
            Output());
        Assert.Empty(_standard.Output);
    }

    [Fact]
    public void Execute_AResumedStart_ListsTheCompletedTasks()
    {
        _starts.Outcome = Started("claude/fix/x");

        _ = Run(Valid(new RunStartSettings { Resume = "claude/fix/x" }));

        Assert.Contains("kyber-weave run start resumed", Output(), StringComparison.Ordinal);
        Assert.Contains("  completed 1\n    1.1\n      done it\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_JsonStart_WritesOneDocumentToStandardOutputAndNothingToTheConsole()
    {
        _starts.Outcome = Started(null);

        int exit = Run(Valid(new RunStartSettings { Json = true }));

        Assert.Equal(0, exit);
        Assert.Empty(Output());
        using JsonDocument document = JsonDocument.Parse(_standard.Output);
        Assert.Equal("claude/fix/x", document.RootElement.GetProperty("branch").GetString());
        Assert.Equal("in-place", document.RootElement.GetProperty("location").GetString());
    }

    [Fact]
    public void Execute_ABriefThatCannotBeBuilt_WarnsOnTheConsoleAndStillReturnsReady()
    {
        _starts.Outcome = Started(null);
        _briefs.BuildError = "no facts";

        int exit = Run(Valid(new RunStartSettings { Brief = true }));

        Assert.Equal(0, exit);
        Assert.Contains("warn the conductor's brief could not be built (no facts)", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ABriefThatCannotBeBuiltUnderJson_WarnsOnStandardError()
    {
        _starts.Outcome = Started(null);
        _briefs.BuildError = "no facts";

        _ = Run(Valid(new RunStartSettings { Brief = true, Json = true }));

        Assert.Contains("warn: the conductor's brief could not be built (no facts)", _standard.ErrorOutput, StringComparison.Ordinal);
    }

    private static RunStartSettings Valid(RunStartSettings settings)
    {
        settings.Harness = "claude";
        settings.Type = "fix";
        settings.Slug = "x";
        return settings;
    }

    private static RunStartOutcome Started(string? resumed) => new(
        RunStartStatus.Started,
        [],
        new RunStartFacts(
            "main",
            "origin/main",
            "claude/fix/x",
            null,
            RunStartLocation.InPlace,
            "abc123",
            resumed,
            "fresh",
            resumed is null ? [] : [new RunStartCompletedTask("1.1", "done it")],
            null,
            "1.0.0",
            "0.9.0",
            "https://example.test/repo.git",
            "abc123",
            "abc123",
            1,
            0,
            Repository));

    private string Output() => _markup.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private int Run(RunStartSettings settings)
    {
        FakeProcessEnvironment environment = new(currentDirectory: "/kw/work");
        RunStartCommand command = new(_runners, _starts, _briefs, _configs, environment, _markup, _standard);
        return command.Execute(null!, settings);
    }

    private sealed class StubRunners : IRunStartRunnerFactory
    {
        public string? Root { get; set; }

        public IRunStartRunner Open(string repositoryRoot) => new StubRunner(Root);
    }

    private sealed class StubRunner(string? root) : IRunStartRunner
    {
        public string RepositoryRoot => root ?? string.Empty;

        public string? DetectRepositoryRoot() => root;

        public GitOutcome Git(params string[] arguments) => throw new NotSupportedException();

        public GitOutcome CheckoutStatus() => throw new NotSupportedException();

        public bool? CheckoutIsClean() => throw new NotSupportedException();

        public GitOutcome WorktreeList() => throw new NotSupportedException();

        public bool? AnotherWorktreeIsAttached() => throw new NotSupportedException();

        public string? OriginHeadSymbolicRef() => throw new NotSupportedException();

        public string? ResolveRevision(string revision) => throw new NotSupportedException();

        public bool HasLocalBranch(string branch) => throw new NotSupportedException();

        public bool HasRemoteBranch(string branch) => throw new NotSupportedException();

        public string? Describe(string revision) => throw new NotSupportedException();

        public (int? Ahead, int? Behind) Divergence(string left, string right) => throw new NotSupportedException();

        public GitOutcome CommitMessages(string baseRef, string tip) => throw new NotSupportedException();

        public string? OriginAddress() => throw new NotSupportedException();

        public bool CodeGraphBinaryIsAvailable() => throw new NotSupportedException();

        public GitOutcome Fetch() => throw new NotSupportedException();

        public GitOutcome CreateBranchInPlace(string branch, string startRef) => throw new NotSupportedException();

        public GitOutcome SwitchToBranch(string branch) => throw new NotSupportedException();

        public string WorktreePathFor(string branch, DateTimeOffset startedAt) => throw new NotSupportedException();

        public GitOutcome EnsureWorktreeFolderExcluded() => throw new NotSupportedException();

        public void ReleaseWorktreeClaim(string worktreePath) => throw new NotSupportedException();

        public GitOutcome CreateBranchInWorktree(string branch, string startRef, string worktreePath) =>
            throw new NotSupportedException();

        public GitOutcome AddWorktreeForBranch(string branch, string worktreePath, string? startRef = null) =>
            throw new NotSupportedException();
    }

    private sealed class StubStarts : IRunStartService
    {
        public List<RunStartRequest> Requests { get; } = [];

        public RunStartOutcome Outcome { get; set; } = new(RunStartStatus.Started, [], null);

        public RunStartOutcome Start(RunStartRequest request)
        {
            Requests.Add(request);
            return Outcome;
        }
    }

    private sealed class StubBriefs : ISquadBriefBuilder
    {
        public string? BuildError { get; set; }

        public string? ResolvedAutonomy { get; set; }

        public string? GivenAutonomy { get; private set; }

        public string? ResolveAutonomy(string repositoryRoot, string? given, KyberWeaveConfig config)
        {
            GivenAutonomy = given;
            return ResolvedAutonomy;
        }

        public bool TryBuild(SquadBriefRequest request, out BriefResult? result, out string refusal)
        {
            result = null;
            refusal = BuildError ?? string.Empty;
            return false;
        }

        public bool AcceptsFacts(string? path, string? autonomy, out string refusal)
        {
            refusal = string.Empty;
            return true;
        }
    }

    private sealed class StubConfigs : IConfigLoadService
    {
        public Diagnostic? Failure { get; set; }

        public bool TryLoad(
            string repoRoot,
            string? configPath,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out string? resolvedConfigPath)
        {
            config = KyberWeaveConfigDefaults.Product;
            resolvedConfigPath = null;
            if (Failure is null)
                return true;

            report.Add(Failure);
            return false;
        }

        public bool TryLoadWithDocsRoots(
            string repoRoot,
            string? configPath,
            IReadOnlyList<string> docsRoots,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out OntologyConfig ontology) => throw new NotSupportedException();
    }
}

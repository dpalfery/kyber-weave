using System.Text.Json;
using KyberWeave.Core.RunStart;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
///     The surface of <c>kyber-weave run start</c>: the exit code each outcome produces, the facts
///     <c>--json</c> reports, and what a defect run prints for its versions.
/// </summary>
/// <remarks>
///     <para>
///         Exit codes are a contract here rather than a convenience. <c>0</c> ready, <c>1</c> a tool failed
///         under <c>stop-on-tool-failure</c>, <c>2</c> refused input, <c>3</c> the CodeGraph binary is
///         missing. A caller such as the conductor branches on those numbers, so a wrong one silently sends
///         a run down the wrong path instead of failing.
///     </para>
///     <para>
///         The cases drive the real CLI as a child process in a real repository, through the same
///         <see cref="RunStartProbe" /> the planner suite uses, because the command type does not exist yet.
///         Asserting the exit code and the printed report pins the same contract without naming a type that
///         would not compile.
///     </para>
/// </remarks>
public sealed class RunStartCommandTests : IDisposable
{
    private readonly RunStartProbe _probe = RunStartProbe.Create();

    public void Dispose()
    {
        _probe.Dispose();
    }

    /// <summary>A well-formed request against a clean checkout is ready, and says so with <c>0</c>.</summary>
    [Fact]
    public void ExitsZeroOnAReadyRun()
    {
        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(result.ExitCode == 0, $"A ready run must exit 0, not {result.ExitCode}. {result.Output}");
    }

    /// <summary>
    ///     A harness token outside the catalogue is refused with <c>2</c> before anything is fetched or
    ///     branched: the caller supplied input the command cannot act on.
    /// </summary>
    [Fact]
    public void RefusesAnUnknownHarnessTokenWithExitTwo()
    {
        ProbeResult result = _probe.RunStart("--harness", "gpt5", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(result.ExitCode == 2,
            $"An unknown harness token must exit 2, not {result.ExitCode}. {result.Output}");
        Assert.False(
            _probe.BranchExists("gpt5/fix/tighten-the-gate"),
            "A refused request must create no branch.");
    }

    /// <summary>A type outside the conventional-commit set is refused with <c>2</c> for the same reason.</summary>
    [Fact]
    public void RefusesATypeThatIsNotAConventionalCommitTypeWithExitTwo()
    {
        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "feature", "--slug", "tighten-the-gate");

        Assert.True(result.ExitCode == 2,
            $"A non-conventional type must exit 2, not {result.ExitCode}. {result.Output}");
        Assert.False(
            _probe.BranchExists("claude/feature/tighten-the-gate"),
            "A refused request must create no branch.");
    }

    /// <summary>
    ///     A missing CodeGraph binary is its own exit code, <c>3</c>, so the caller can tell "install
    ///     something" apart from "something broke".
    /// </summary>
    /// <remarks>
    ///     The machine running the suite decides whether this case can run: if a real CodeGraph binary is
    ///     still reachable on the PATH the probe builds, the case is skipped rather than reported as a
    ///     product failure, because there is no missing binary to observe.
    /// </remarks>
    [Fact]
    public void ExitsThreeWhenTheCodeGraphBinaryIsMissing()
    {
        if (_probe.CodegraphResolvesWithoutItsStub())
            throw SkipException.ForSkip(
                "A real codegraph executable is reachable on every PATH entry this case can build, " +
                "so the missing-binary outcome cannot be produced here.");

        ProbeResult result = _probe.RunStartWithoutCodegraph(
            "--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(
            result.ExitCode == 3,
            $"A missing CodeGraph binary must exit 3, not {result.ExitCode}. {result.Output}");
    }

    /// <summary>
    ///     <c>--json</c> reports the facts a caller needs to route the work: the base it compared
    ///     against, the branch it created, where that branch lives, the revision it starts from, and the
    ///     versions it compared.
    /// </summary>
    [Fact]
    public void PrintsTheRunFactsAsJson()
    {
        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate",
            "--json");

        Assert.True(result.ExitCode == 0, $"A ready run must exit 0, not {result.ExitCode}. {result.Output}");

        using JsonDocument document = ParseJson(result.StandardOutput);
        string[] required = ["base", "branch", "worktree", "revision", "versions"];
        string[] missing = required
            .Where(key => !document.RootElement.TryGetProperty(key, out _))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"--json must report {string.Join(", ", required)}. Missing: {string.Join(", ", missing)}. " +
            $"It printed: {result.StandardOutput}");
        Assert.Equal("claude/fix/tighten-the-gate", document.RootElement.GetProperty("branch").GetString());
    }

    /// <summary>
    ///     When divergence between HEAD and the base cannot be determined, 'aligned' in JSON is null
    ///     rather than false.
    /// </summary>
    [Fact]
    public void JsonLeavesAlignedNullWhenDivergenceCannotBeDetermined()
    {
        _probe.Git("checkout", "--orphan", "unborn-branch");
        _probe.Git("rm", "-rf", ".");

        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate",
            "--json");

        Assert.True(result.ExitCode == 0, $"Run must succeed: {result.Output}");
        using JsonDocument document = ParseJson(result.StandardOutput);
        JsonElement alignment = document.RootElement.GetProperty("serverAlignment");
        Assert.Equal(JsonValueKind.Null, alignment.GetProperty("aligned").ValueKind);
    }

    /// <summary>
    ///     When the git status probe fails, the command fails and surfaces the status error rather than
    ///     treating the failure as a dirty checkout and attempting worktree creation.
    /// </summary>
    [Fact]
    public void FailsAndSurfacesErrorWhenCheckoutStatusProbeFails()
    {
        _probe.Git("config", "core.excludesfile", _probe.Root);

        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("git status failed:", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     When the git worktree list probe fails, the command fails and surfaces the worktree error rather
    ///     than treating the failure as absent worktrees and creating the branch in place.
    /// </summary>
    [Fact]
    public void FailsAndSurfacesErrorWhenWorktreeListProbeFails()
    {
        _probe.WriteGitFailOn("worktree", "fatal: simulated worktree list error");

        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("git worktree list failed:", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A defect run compares the failing build's version with <c>HEAD</c> and with the remote base,
    ///     and prints all three, because the point of the comparison is to show an operator that the bug
    ///     they hit is not the commit they are looking at.
    /// </summary>
    /// <remarks>
    ///     The assertion is on the three labels, not on the two resolved values: a repository that cannot
    ///     say what version <c>HEAD</c> is still owes the operator the column, marked unknown.
    /// </remarks>
    [Fact]
    public void ComparesTheFailingVersionWithHeadAndTheRemoteBase()
    {
        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate",
            "--failing-version", "0.1.2");

        Assert.True(result.ExitCode == 0, $"A ready run must exit 0, not {result.ExitCode}. {result.Output}");
        Assert.Contains("0.1.2", result.Output, StringComparison.Ordinal);
        Assert.Contains("HEAD", result.Output, StringComparison.Ordinal);
        Assert.Contains("main", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The verb documents itself, so an operator reaching for it can find its required arguments
    ///     without reading the source.
    /// </summary>
    [Fact]
    public void DocumentsItsRequiredArgumentsInHelp()
    {
        ProbeResult result = _probe.RunCli("run", "start", "--help");

        Assert.True(result.ExitCode == 0, $"'run start --help' must exit 0, not {result.ExitCode}. {result.Output}");
        foreach (string flag in new[] { "--harness", "--type", "--slug" })
            Assert.Contains(flag, result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     HasMultipleWorktrees correctly parses git worktree porcelain output.
    /// </summary>
    [Theory]
    [InlineData("worktree /path/to/main\nHEAD abc\nbranch refs/heads/main\n\n", false)]
    [InlineData("worktree /path/to/main\nHEAD abc\nbranch refs/heads/main\n\nworktree /path/to/worker\nHEAD def\nbranch refs/heads/worker\n\n", true)]
    [InlineData("", false)]
    public void HasMultipleWorktreesParsesPorcelain(string porcelain, bool expected)
    {
        Assert.Equal(expected, RunStartRunner.HasMultipleWorktrees(porcelain));
    }

    /// <summary>
    ///     Reads the reported facts, tolerating the banner a human-readable run may print around them:
    ///     the JSON object is the first <c>{</c> in the output and the last matched brace.
    /// </summary>
    private static JsonDocument ParseJson(string output)
    {
        int start = output.IndexOf('{', StringComparison.Ordinal);
        int end = output.LastIndexOf('}');
        Assert.True(
            start >= 0 && end > start,
            $"--json must print a JSON object. It printed: {output}");

        string json = output[start..(end + 1)];
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new XunitException($"--json printed something that is not JSON ({ex.Message}): {json}");
        }
    }
}

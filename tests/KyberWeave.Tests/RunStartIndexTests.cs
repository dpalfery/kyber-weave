using System.Text.Json;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins that <c>kyber-weave run start</c> indexes the tree its branch lands in: the new worktree when one is
///     made, otherwise the checkout itself.
/// </summary>
/// <remarks>
///     <para>
///         The cases drive the real CLI in a real repository, through <see cref="RunStartProbe" />, with a CodeGraph
///         stub that logs the directory and arguments of every call. The worktree is governed only when its host
///         configuration and docs folder are committed, which is how a real repository carries them.
///     </para>
///     <para>
///         The <c>--json</c> cases parse standard output strictly: the spec makes it one JSON document, so any
///         banner or warning text printed there is a failure.
///     </para>
/// </remarks>
public sealed class RunStartIndexTests : IDisposable
{
    private const string Governed = "onboarding: done\n";
    private const string FailingIndex = "case \"$1\" in init|sync|index) echo 'boom' >&2; exit 1 ;; esac\nexit 0";
    private const string WorkingIndex = "exit 0";

    private readonly RunStartProbe _probe = RunStartProbe.Create();

    public void Dispose()
    {
        _probe.Dispose();
    }

    [Fact]
    public void ARunThatCreatesAWorktreeInitialisesTheIndexInsideThatWorktree()
    {
        _probe.PublishGovernance(Governed);
        _probe.AppendLine("README.md", "uncommitted work");
        _probe.ScriptCodegraph(WorkingIndex);

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate", "--json");

        Assert.True(result.ExitCode == 0, $"A ready index must let the run start. {result.Output}");
        using JsonDocument document = ParseStrictJson(result.StandardOutput);
        string worktree = document.RootElement.GetProperty("worktree").GetString()!;
        CodegraphCall init = Assert.Single(ReadCodegraphCalls(), call => HasVerb(call, "init"));
        Assert.Equal(Path.GetFileName(worktree), Path.GetFileName(init.WorkingDirectory));
        Assert.Equal("created", document.RootElement.GetProperty("index").GetString());
    }

    [Fact]
    public void AFailedIndexInANewWorktreeStopsTheRunAndNamesTheDoctorFixForThatTree()
    {
        _probe.PublishGovernance(Governed);
        _probe.AppendLine("README.md", "uncommitted work");
        _probe.ScriptCodegraph(FailingIndex);

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(result.ExitCode == 1, $"A failed index must stop the run with exit 1, not {result.ExitCode}. {result.Output}");
        string tree = Assert.Single(Directory.GetDirectories(WorktreesFolder()));
        string message = Normalise(result.Output);
        Assert.Contains("kyber-weave doctor --ensure", message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(tree), message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedIndexWarnsAndContinuesWhenStopOnToolFailureIsFalse()
    {
        _probe.PublishGovernance("stop-on-tool-failure: false\nonboarding: done\n");
        _probe.AppendLine("README.md", "uncommitted work");
        _probe.ScriptCodegraph(FailingIndex);

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate", "--json");

        Assert.True(result.ExitCode == 0, $"stop-on-tool-failure: false must let the run continue. {result.Output}");
        using JsonDocument document = ParseStrictJson(result.StandardOutput);
        Assert.Equal("warning", document.RootElement.GetProperty("index").GetString());
        Assert.Contains("kyber-weave doctor --ensure", Normalise(result.Output), StringComparison.Ordinal);
        Assert.True(
            _probe.BranchExists("claude/fix/tighten-the-gate"),
            "A run that continued past a failed index must still create its branch.");
    }

    [Fact]
    public void AGovernedCheckoutThatAlreadyHasAnIndexSyncsItAndReportsReady()
    {
        _probe.PublishGovernance(Governed);
        _probe.IgnoreCodegraphIndex();
        Directory.CreateDirectory(Path.Combine(_probe.Root, ".codegraph"));
        _probe.ScriptCodegraph(WorkingIndex);

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate", "--json");

        Assert.True(result.ExitCode == 0, $"A ready index must let the run start. {result.Output}");
        using JsonDocument document = ParseStrictJson(result.StandardOutput);
        Assert.Equal("ready", document.RootElement.GetProperty("index").GetString());
        CodegraphCall sync = Assert.Single(ReadCodegraphCalls(), call => HasVerb(call, "sync"));
        Assert.Equal(Path.GetFileName(_probe.Root), Path.GetFileName(sync.WorkingDirectory));
    }

    [Fact]
    public void AnUngovernedRepositoryStartsNoIndexChildAndReportsNotGoverned()
    {
        _probe.AppendLine("README.md", "uncommitted work");
        _probe.ScriptCodegraph(WorkingIndex);

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate", "--json");

        Assert.True(result.ExitCode == 0, $"An ungoverned run must still start. {result.Output}");
        using JsonDocument document = ParseStrictJson(result.StandardOutput);
        Assert.Equal("not-governed", document.RootElement.GetProperty("index").GetString());
        Assert.DoesNotContain(ReadCodegraphCalls(), call => HasVerb(call, "init") || HasVerb(call, "sync") || HasVerb(call, "index"));
    }

    private string WorktreesFolder() =>
        Path.Combine(Path.GetDirectoryName(_probe.Root)!, Path.GetFileName(_probe.Root) + ".worktrees");

    /// <summary>Every CodeGraph call the stub logged, oldest first, or none when the stub was never called.</summary>
    private IReadOnlyList<CodegraphCall> ReadCodegraphCalls()
    {
        if (!File.Exists(_probe.CodegraphCallLog))
            return [];

        return File.ReadAllLines(_probe.CodegraphCallLog)
            .Select(line => line.Split('|', 2))
            .Select(parts => new CodegraphCall(parts[0], parts[1]))
            .ToArray();
    }

    private static bool HasVerb(CodegraphCall call, string verb) =>
        call.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(verb, StringComparer.Ordinal);

    private static string Normalise(string output) =>
        string.Join(' ', output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static JsonDocument ParseStrictJson(string standardOutput) => JsonDocument.Parse(standardOutput);

    /// <summary>One CodeGraph call the stub logged: the directory it ran in and its arguments.</summary>
    private sealed record CodegraphCall(string WorkingDirectory, string Arguments);
}

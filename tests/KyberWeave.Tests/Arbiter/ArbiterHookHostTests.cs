using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 4.1: the <c>kyber-weave-arbiter hook</c> host. The host reads the
/// harness event on stdin, writes the harness decision document on stdout and nothing
/// else (logging goes to a separate stderr writer), and exits 0. Anything that goes
/// wrong becomes that harness's block carrying <c>KW-ARB-HOOK-001</c>, never a pass.
/// Fast-path events return before host configuration is loaded.
/// RED: the Arbiter project and the hook host do not exist yet.
/// Fixture payloads live under <c>Fixtures/arbiter-hooks/claude/</c>, built from [F1]
/// of the task packet.
/// </summary>
public sealed class ArbiterHookHostTests
{
    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public bool ThrowOnPost { get; set; }

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            PreResult;

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config)
        {
            if (ThrowOnPost)
            {
                throw new InvalidOperationException("provider is down");
            }

            return PostResult;
        }
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static KyberWeaveConfig DocsRootConfig() =>
        new()
        {
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoots(["docs"]),
            Arbiter = new ArbiterConfig { Enabled = true },
        };

    private static KyberWeaveConfig DisabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = false } };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "claude", name));

    private static HookCommand CreateCommand(
        ScriptedEngine engine,
        Func<string, KyberWeaveConfig>? loader = null,
        Func<string>? ids = null) =>
        new(
            HarnessAdapterRegistry.CreateDefault(engine),
            loader ?? (_ => EnabledConfig()),
            ids ?? (() => "decision-test-1"));

    private static (int Exit, string Stdout, string Log) Run(
        HookCommand command, string? caller, string stdin)
    {
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run("claude", caller, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    [Fact]
    public void Hook_NonDispatchTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        HookCommand command = CreateCommand(
            new ScriptedEngine(),
            root => { loads++; return EnabledConfig(); });

        (int exit, string stdout, _) = Run(command, "csharp-dev", Fixture("pre-edit-passthrough.json"));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(0, loads);
    }

    [Fact]
    public void Hook_AgentWithoutSubagentType_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        HookCommand command = CreateCommand(
            new ScriptedEngine(),
            root => { loads++; return EnabledConfig(); });

        (int exit, string stdout, _) = Run(command, "conductor", Fixture("pre-agent-no-subagent.json"));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(0, loads);
    }

    [Fact]
    public async Task Hook_ThroughArgv_HookCommandLine_PassesThrough()
    {
        using StringReader stdin = new(Fixture("pre-agent-no-subagent.json"));
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", "claude", "--caller", "conductor"], stdin, stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Theory]
    [InlineData("{oops")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    public void Hook_MalformedStdin_BlocksFailClosed(string stdin)
    {
        HookCommand command = CreateCommand(new ScriptedEngine());

        (int exit, string stdout, _) = Run(command, "conductor", stdin);

        // Fail closed: exit 0 carries the block on stdout, never a pass.
        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        string reason = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_MissingConfigOnClassifiedEvent_Blocks()
    {
        HookCommand command = CreateCommand(
            new ScriptedEngine(),
            _ => throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: no .kyber-weave/kyber-weave.yml found."));

        (int exit, string stdout, _) = Run(command, "conductor", Fixture("pre-agent-dispatch.json"));

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        string reason = doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_EngineCrashOnReturnEvent_BlocksWithTopLevelBlock()
    {
        ScriptedEngine engine = new() { ThrowOnPost = true };
        HookCommand command = CreateCommand(engine);

        (int exit, string stdout, _) = Run(command, "conductor", Fixture("post-completed.json"));

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        string reason = doc.RootElement.GetProperty("reason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_DisabledArbiter_AllowsAndLogs()
    {
        HookCommand command = CreateCommand(
            new ScriptedEngine(), _ => DisabledConfig());

        (int exit, string stdout, string log) =
            Run(command, "conductor", Fixture("pre-agent-dispatch.json"));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("disabled", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hook_StdoutHoldsOnlyTheDecision()
    {
        // The fixture repo keeps planning documents under docs/, so the host config
        // declares that docs root: the read then denies and stdout carries the block.
        HookCommand command = CreateCommand(new ScriptedEngine(), _ => DocsRootConfig());

        (int exit, string stdout, string log) =
            Run(command, null, Fixture("pre-read-denied.json"));

        Assert.Equal(0, exit);

        // The whole stdout must be one JSON decision document: no log lines mixed in.
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(
            stdout.TrimStart().StartsWith('{'),
            "Stdout must start with the decision document.");
        Assert.DoesNotContain("hookSpecificOutput", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hook_UnknownHarness_ExitsNonZeroWithEmptyStdout()
    {
        using StringReader stdin = new(Fixture("pre-agent-no-subagent.json"));
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", "nope"], stdin, stdout, stderr);

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task Hook_MissingHarnessFlag_ExitsNonZeroWithEmptyStdout()
    {
        using StringReader stdin = new(Fixture("pre-agent-no-subagent.json"));
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(["hook"], stdin, stdout, stderr);

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task Version_PrintsNameAndSemverAndNothingElse()
    {
        using StringReader stdin = new(string.Empty);
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(["--version"], stdin, stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Matches(@"^kyber-weave-arbiter \d+\.\d+\.\d+\r?\n$", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }
}

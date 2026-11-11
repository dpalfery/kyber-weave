using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Which hook sources count as covering a dispatch when the user-level Claude hook decides whether
/// to evaluate: the project Claude hook, the agent file for the payload's agent type, and every
/// effective skill. A source that does not carry the dispatch hook under the event's own container
/// must not make the user-level hook wait or defer.
/// </summary>
public sealed class ArbiterGlobalHookCoverageTests
{
    private const string ToolUseId = "toolu-global-coverage-1";
    private const string DispatchMatcher = "^(Agent|Task)$";
    private const string ReadMatcher = "^(Read|Grep|Glob|Bash)$";

    private static readonly string AgentFile = Path.Combine(".claude", "agents", "csharp-dev.md");
    private static readonly string ConductorAgentFile = Path.Combine(".claude", "agents", "conductor.md");
    private static readonly string ConductorSkill = Path.Combine(".claude", "skills", "conductor", "SKILL.md");

    [Fact]
    public async Task GlobalScope_WhenAgentFileDispatchHookClaimedTheDispatch_DefersNamingTheAgentFile()
    {
        // The agent file's own PreToolUse dispatch hook is a candidate for csharp-dev dispatches.
        // A project claim already exists, so the user-level hook must defer and name that file
        // as the candidate that claimed it. Without the candidate it would not know to defer.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(
            AgentFile,
            ArbiterGlobalHookScenario.HookDocument(
                "csharp-dev",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", DispatchMatcher)));
        ArbiterGlobalHookScenario.WriteClaim(scenario.ProjectRoot, ToolUseId, "pre", "project");
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, "csharp-dev", "csharp-dev", "Implement the plan parser.");

        HookRun run = await scenario.RunGlobal(payload);

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Contains(AgentFile, run.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(ArbiterGlobalHookScenario.DecisionsPath(scenario.ProjectRoot)),
            "A deferred dispatch must not be evaluated by the user-level hook, so no decision may be written.");
    }

    [Fact]
    public async Task GlobalScope_WhenAgentFileHookMatchesOnlyReadTools_EvaluatesWithoutWaiting()
    {
        // The same agent file, but its only PreToolUse entry matches Read tools. It does not cover
        // Agent or Task, so it is not a candidate and the user-level hook evaluates at once.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(
            AgentFile,
            ArbiterGlobalHookScenario.HookDocument(
                "csharp-dev",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", ReadMatcher)));
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, "csharp-dev", "csharp-dev", "Implement the plan parser.");

        Stopwatch clock = Stopwatch.StartNew();
        HookRun run = await scenario.RunGlobal(payload);
        clock.Stop();

        Assert.Contains(ArbiterGlobalHookScenario.EvaluatingLog, run.Stderr, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1000),
            $"A non-candidate agent file must not make the user-level hook wait. Elapsed {clock.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task GlobalScope_WhenMainThreadDispatchHasOnlyAnAgentFileHook_EvaluatesWithoutWaiting()
    {
        // A main-thread dispatch has no agent type, so no agent file is effective even when
        // .claude/agents/conductor.md carries the dispatch hook. Nothing can claim it, so no wait.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(
            ConductorAgentFile,
            ArbiterGlobalHookScenario.HookDocument(
                "conductor",
                ArbiterGlobalHookScenario.ConductorCommand,
                ("PreToolUse", DispatchMatcher)));
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);

        Stopwatch clock = Stopwatch.StartNew();
        HookRun run = await scenario.RunGlobal(payload);
        clock.Stop();

        Assert.Contains(ArbiterGlobalHookScenario.EvaluatingLog, run.Stderr, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1000),
            $"An agent file that is not effective for a main-thread dispatch must not make the hook wait. Elapsed {clock.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task GlobalScope_WhenPersonalSkillShadowsProjectHookSkill_EvaluatesWithoutWaiting()
    {
        // The personal skill of the same name shadows the project skill, and it carries no hook.
        // The project skill's dispatch hook is therefore not effective, so nothing can claim.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(
            ConductorSkill,
            ArbiterGlobalHookScenario.HookDocument(
                "conductor",
                ArbiterGlobalHookScenario.ConductorCommand,
                ("PreToolUse", DispatchMatcher),
                ("PostToolUse", DispatchMatcher)));
        scenario.WriteHomeFile(ConductorSkill, ArbiterGlobalHookScenario.PlainSkillDocument("conductor"));
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);

        Stopwatch clock = Stopwatch.StartNew();
        HookRun run = await scenario.RunGlobal(payload);
        clock.Stop();

        Assert.Contains(ArbiterGlobalHookScenario.EvaluatingLog, run.Stderr, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1000),
            $"A project skill shadowed by a personal skill must not make the hook wait. Elapsed {clock.ElapsedMilliseconds} ms.");
    }

    [Theory]
    [InlineData("../../etc/x")]
    [InlineData("plugin:agent")]
    public async Task GlobalScope_WhenAgentTypeIsNotAValidAgentFileName_EvaluatesWithoutReadingAgentFile(string agentType)
    {
        // A traversal name would resolve to etc/x.md beside .claude. The decoy there carries the
        // dispatch hook, so reading it would defer. An invalid name must use no agent file at all.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string decoy = Path.Combine("etc", "x.md");
        scenario.WriteProjectFile(
            decoy,
            ArbiterGlobalHookScenario.HookDocument(
                "x",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", DispatchMatcher)));
        scenario.WriteHomeFile(
            decoy,
            ArbiterGlobalHookScenario.HookDocument(
                "x",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", DispatchMatcher)));
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, agentType, "csharp-dev", "Implement the plan parser.");

        HookRun run = await scenario.RunGlobal(payload);

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.EvaluatingLog, run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterGlobalHookScenario.FailClosedCode, run.Stdout + run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlobalScope_WhenHarnessHasNoPerDispatchProbe_DefersNamingTheHarness()
    {
        // Cursor has no per-dispatch probe for the user-level hook. The project's own Cursor hooks
        // decide, so the user-level hook defers and names the harness, and allows nothing itself.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.CursorTaskDispatch(scenario.ProjectRoot);

        HookRun run = await scenario.RunGlobal(payload, "cursor");

        Assert.Equal(0, run.Exit);
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout),
            $"A deferred Cursor dispatch must emit no decision document. Output: {run.Stdout}");
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("cursor", run.Stderr, StringComparison.Ordinal);
    }
}

/// <summary>
/// A project root, a twin root (the same project again, for the project-scope baseline) and a home
/// directory, all under the system temp folder and deleted on dispose. Payloads are built from the
/// Claude and Cursor fixtures with the fields a row needs edited.
/// </summary>
internal sealed class ArbiterGlobalHookScenario : IDisposable
{
    public const string Harness = "claude";
    public const string DispatchMatcher = "^(Agent|Task)$";
    public const string ConductorCommand = "kyber-weave-arbiter hook --harness claude --caller conductor";
    public const string CsharpDevCommand = "kyber-weave-arbiter hook --harness claude --caller csharp-dev";
    public const string EvaluatingLog = "evaluating under the project configuration";
    public const string DeferredLog = "deferred-to-project";
    public const string NotTwiceLog = "not evaluated twice";
    public const string FailClosedCode = "KW-ARB-HOOK-001";
    public const string UnmarkedPrompt = "Plan the parser.";

    private ArbiterGlobalHookScenario(string projectRoot, string twinRoot, string home)
    {
        ProjectRoot = projectRoot;
        TwinRoot = twinRoot;
        Home = home;
    }

    public string ProjectRoot { get; }

    public string TwinRoot { get; }

    public string Home { get; }

    /// <summary>Creates the three roots, each project root holding the same configuration.</summary>
    public static ArbiterGlobalHookScenario Create(bool projectEnabled = true)
    {
        ArbiterGlobalHookScenario scenario = new(CreateTempDirectory(), CreateTempDirectory(), CreateTempDirectory());
        string yaml = projectEnabled ? "arbiter:\n  enabled: true\n" : "arbiter:\n  enabled: false\n";
        foreach (string root in new[] { scenario.ProjectRoot, scenario.TwinRoot })
        {
            string configDirectory = Path.Combine(root, ".kyber-weave");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(Path.Combine(configDirectory, "kyber-weave.yml"), yaml);
        }

        return scenario;
    }

    /// <summary>Writes a file under both the project root and the twin root, so both hold the same tree.</summary>
    public void WriteProjectFile(string relativePath, string content)
    {
        WriteFile(ProjectRoot, relativePath, content);
        WriteFile(TwinRoot, relativePath, content);
    }

    public void WriteHomeFile(string relativePath, string content) => WriteFile(Home, relativePath, content);

    /// <summary>Runs the user-level hook against this scenario's home.</summary>
    public Task<HookRun> RunGlobal(string payload, string harness = Harness) =>
        Invoke(["hook", "--harness", harness, "--scope", "global", "--home", Home], payload);

    /// <summary>Runs the project-scope hook: no <c>--scope</c> and no <c>--home</c>.</summary>
    public static Task<HookRun> RunProject(string payload, string? caller = null)
    {
        string[] args = caller is null
            ? ["hook", "--harness", Harness]
            : ["hook", "--harness", Harness, "--caller", caller];
        return Invoke(args, payload);
    }

    /// <summary>
    /// Masks the <c>DECISION_ID</c> a deny document carries. Each evaluation mints a new id, so two
    /// runs of the same dispatch differ only there; every other byte must still match the twin.
    /// </summary>
    public static string WithoutDecisionId(string stdout) =>
        Regex.Replace(stdout, "DECISION_ID: [0-9A-Za-z-]+", "DECISION_ID: <minted>", RegexOptions.None, TimeSpan.FromSeconds(5));

    public static string DecisionsPath(string root) => Path.Combine(root, "artifacts", "arbiter", "decisions.jsonl");

    /// <summary>The number of decision lines written under the root, zero when the ledger is absent.</summary>
    public static int DecisionLines(string root)
    {
        string path = DecisionsPath(root);
        return File.Exists(path) ? File.ReadAllLines(path).Count(line => line.Length > 0) : 0;
    }

    public static string ClaimPath(string root, string toolUseId, string phase) =>
        Path.Combine(root, "artifacts", "arbiter", "claims", $"{toolUseId}.{phase}");

    public static void WriteClaim(string root, string toolUseId, string phase, string source)
    {
        string path = ClaimPath(root, toolUseId, phase);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source + "\n");
    }

    public static bool ClaimsFolderExists(string root) =>
        Directory.Exists(Path.Combine(root, "artifacts", "arbiter", "claims"));

    /// <summary>A PreToolUse Agent dispatch. A null agent type is the main thread.</summary>
    public static string Dispatch(string root, string toolUseId, string? agentType, string subagentType, string prompt) =>
        Fixture(root, Path.Combine("claude", "pre-agent-dispatch.json"), payload =>
            ApplyDispatch(payload, toolUseId, agentType, subagentType, prompt));

    /// <summary>The PostToolUse completion of a dispatch. Pass the same tool use id as the pre event.</summary>
    public static string PostDispatch(string root, string toolUseId, string? agentType, string subagentType, string prompt) =>
        Fixture(root, Path.Combine("claude", "post-completed.json"), payload =>
            ApplyDispatch(payload, toolUseId, agentType, subagentType, prompt));

    public static string ReadPayload(string root, string toolUseId) =>
        Fixture(root, Path.Combine("claude", "pre-read-allowed.json"), payload =>
            payload["tool_use_id"] = toolUseId);

    public static string CursorTaskDispatch(string root) =>
        Fixture(root, Path.Combine("cursor", "pre-task-dispatch.json"), _ => { });

    /// <summary>An agent or skill file whose frontmatter carries the given hook entries.</summary>
    public static string HookDocument(string name, string command, params (string Event, string Matcher)[] entries)
    {
        StringBuilder document = new();
        document.Append("---\n");
        document.Append("name: ").Append(name).Append('\n');
        document.Append("description: Fixture for the user-level Arbiter hook.\n");
        document.Append("hooks:\n");
        foreach (IGrouping<string, (string Event, string Matcher)> group in entries.GroupBy(entry => entry.Event, StringComparer.Ordinal))
        {
            document.Append("  ").Append(group.Key).Append(":\n");
            foreach ((string Event, string Matcher) entry in group)
            {
                document.Append("    - matcher: \"").Append(entry.Matcher).Append("\"\n");
                document.Append("      hooks:\n");
                document.Append("        - type: command\n");
                document.Append("          command: \"").Append(command).Append("\"\n");
            }
        }

        document.Append("---\n\nFixture body.\n");
        return document.ToString();
    }

    /// <summary>A skill file with frontmatter and no hooks.</summary>
    public static string PlainSkillDocument(string name) =>
        $"---\nname: {name}\ndescription: Personal fixture without hooks.\n---\n\nFixture body.\n";

    public void Dispose()
    {
        foreach (string root in new[] { ProjectRoot, TwinRoot, Home })
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<HookRun> Invoke(string[] args, string payload)
    {
        using StringReader stdin = new(payload);
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = await Composition.DispatchAsync(args, stdin, stdout, stderr);
        return new HookRun(exit, stdout.ToString(), stderr.ToString());
    }

    private static string Fixture(string root, string relativeFixture, Action<JsonObject> edit)
    {
        string fixturePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "tests",
            "KyberWeave.Tests",
            "Fixtures",
            "arbiter-hooks",
            relativeFixture);

        JsonObject payload = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        payload["cwd"] = root;
        payload["transcript_path"] = Path.Combine(root, "transcript.jsonl");
        edit(payload);
        return payload.ToJsonString();
    }

    private static void ApplyDispatch(JsonObject payload, string toolUseId, string? agentType, string subagentType, string prompt)
    {
        if (agentType is null)
        {
            payload.Remove("agent_type");
        }
        else
        {
            payload["agent_type"] = agentType;
        }

        payload["tool_use_id"] = toolUseId;
        JsonObject toolInput = payload["tool_input"]!.AsObject();
        toolInput["subagent_type"] = subagentType;
        toolInput["prompt"] = prompt;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kw-arbiter-global-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>The captured result of one hook invocation.</summary>
internal sealed record HookRun(int Exit, string Stdout, string Stderr);

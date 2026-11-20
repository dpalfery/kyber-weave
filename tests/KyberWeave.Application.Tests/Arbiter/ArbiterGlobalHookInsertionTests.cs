using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The user-level hook at global scope for a harness whose project hook it can probe. When the project
/// enables the Arbiter but carries no hook of its own for that harness, the user-level hook inserts the
/// task prompt itself: deferring would leave the worker with only the header lines. When the project does
/// carry one, it keeps deferring and lets that hook decide.
/// </summary>
public sealed class ArbiterGlobalHookInsertionTests
{
    private const string TaskPrompt = "<task>\nImplement the parser.\n</task>\n\n<left-out>\nNothing was left out.\n</left-out>";
    private const string TaskDispatch =
        "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement the plan parser.";
    private const string AntigravityNoHookLog =
        "Arbiter global scope: no project antigravity hook; inserting the task prompt without evaluation.";
    private const string OpenCodeNoHookLog =
        "Arbiter global scope: no project opencode hook; inserting the task prompt without evaluation.";
    private const string FactoryNoHookLog =
        "Arbiter global scope: no project factory hook; inserting the task prompt without evaluation.";
    private const string CursorNoHookLog =
        "Arbiter global scope: no project cursor hook; inserting the task prompt without evaluation.";
    private const string PiNoHookLog =
        "Arbiter global scope: no project pi hook; inserting the task prompt without evaluation.";

    [Fact]
    public void GlobalScope_AntigravityWithoutAProjectHook_InsertsTheTaskPrompt()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Uncover("antigravity");
        string payload = AntigravityDispatch(scenario.ProjectRoot, TaskDispatch);

        HookRun run = RunGlobal(scenario.Project, "antigravity", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(AntigravityNoHookLog, run.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(run.Stdout);
        JsonElement overwrite = document.RootElement.GetProperty("overwrite");
        Assert.StartsWith(TaskPrompt, overwrite.GetProperty("Prompt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_WhenTheProjectHookFileCannotBeRead_InsertsAndLogsTheFailure()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Answer = (_, _) => throw new UnauthorizedAccessException("denied");
        string payload = AntigravityDispatch(scenario.ProjectRoot, TaskDispatch);

        HookRun run = RunGlobal(scenario.Project, "antigravity", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains("could not be read (denied)", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-ARB-HOOK-001", run.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(run.Stdout);
        Assert.StartsWith(
            TaskPrompt,
            document.RootElement.GetProperty("overwrite").GetProperty("Prompt").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_AntigravityWithAProjectHook_DefersAndWritesThePlainAllow()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Cover("antigravity");
        string payload = AntigravityDispatch(scenario.ProjectRoot, TaskDispatch);

        HookRun run = RunGlobal(scenario.Project, "antigravity", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("antigravity", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("{\"decision\":\"allow\"}", run.Stdout);
        Assert.DoesNotContain(AntigravityNoHookLog, run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_OpenCodeWithoutAProjectHook_InsertsTheTaskPrompt()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Uncover("opencode");
        string payload = OpenCodeDispatch(scenario.ProjectRoot, TaskDispatch);

        HookRun run = RunGlobal(scenario.Project, "opencode", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(OpenCodeNoHookLog, run.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(run.Stdout);
        JsonElement root = document.RootElement;
        Assert.Equal("allow", root.GetProperty("decision").GetString());
        Assert.StartsWith(
            TaskPrompt,
            root.GetProperty("args").GetProperty("prompt").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_OpenCodeWithAProjectHook_DefersAndWritesNoDocument()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Cover("opencode");
        string payload = OpenCodeDispatch(scenario.ProjectRoot, TaskDispatch);

        HookRun run = RunGlobal(scenario.Project, "opencode", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("opencode", run.Stderr, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout),
            $"A deferred OpenCode dispatch must emit no decision document. Output: {run.Stdout}");
        Assert.DoesNotContain(OpenCodeNoHookLog, run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_FactoryWithoutAProjectHook_InsertsTheTaskPrompt()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Uncover("factory");
        string payload = FactoryDispatch(scenario.ProjectRoot, TaskDispatch);

        HookRun run = RunGlobal(scenario.Project, "factory", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(FactoryNoHookLog, run.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(run.Stdout);
        JsonElement updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.StartsWith(TaskPrompt, updated.GetProperty("prompt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_FactoryWithAProjectHook_DefersAndWritesNoDocument()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Cover("factory");
        string payload = FactoryDispatch(scenario.ProjectRoot, TaskDispatch);

        HookRun run = RunGlobal(scenario.Project, "factory", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("factory", run.Stderr, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout),
            $"A deferred Factory dispatch must emit no decision document. Output: {run.Stdout}");
        Assert.DoesNotContain(FactoryNoHookLog, run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_CursorWithoutAProjectHook_InsertsTheTaskPrompt()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Uncover("cursor");
        string payload = ArbiterGlobalHookScenario.CursorTaskDispatch(scenario.ProjectRoot);

        HookRun run = RunGlobal(scenario.Project, "cursor", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(CursorNoHookLog, run.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(run.Stdout);
        JsonElement updated = document.RootElement.GetProperty("updated_input");
        Assert.StartsWith(TaskPrompt, updated.GetProperty("prompt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_CursorWithAProjectHook_DefersAndWritesNoDocument()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Cover("cursor");
        string payload = ArbiterGlobalHookScenario.CursorTaskDispatch(scenario.ProjectRoot);

        HookRun run = RunGlobal(scenario.Project, "cursor", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("cursor", run.Stderr, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout),
            $"A deferred Cursor dispatch must emit no decision document. Output: {run.Stdout}");
        Assert.DoesNotContain(CursorNoHookLog, run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_PiWithoutAProjectHook_InsertsTheTaskPrompt()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Uncover("pi");
        string payload = PiDispatch(scenario.ProjectRoot, TaskDispatch);

        HookRun run = RunGlobal(scenario.Project, "pi", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(PiNoHookLog, run.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(run.Stdout);
        JsonElement root = document.RootElement;
        Assert.Equal("allow", root.GetProperty("decision").GetString());
        Assert.StartsWith(
            TaskPrompt,
            root.GetProperty("args").GetProperty("prompt").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_PiWithAProjectHook_DefersAndWritesNoDocument()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.ProjectHooks.Cover("pi");
        string payload = PiDispatch(scenario.ProjectRoot, TaskDispatch);

        HookRun run = RunGlobal(scenario.Project, "pi", payload, new ScriptedPrompts());

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("pi", run.Stderr, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout),
            $"A deferred Pi dispatch must emit no decision document. Output: {run.Stdout}");
        Assert.DoesNotContain(PiNoHookLog, run.Stderr, StringComparison.Ordinal);
    }

    private static HookRun RunGlobal(
        InMemoryArbiterHost host,
        string harness,
        string payload,
        ITaskPromptServiceFactory prompts)
    {
        HookCommand command = host.HookCommand(host.Registry(host.Engine()), prompts: prompts);
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = command.Run(harness, null, payload, stdout, stderr, HookScope.Global, host.HomeDirectory);
        return new HookRun(exit, stdout.ToString(), stderr.ToString());
    }

    private static string AntigravityDispatch(string root, string prompt) =>
        ArbiterGlobalHookScenario.Fixture(root, "antigravity/real-pre-conductor-marked.json", payload =>
        {
            payload["workspacePaths"] = new JsonArray(root);
            payload["toolCall"]!["args"]!["Prompt"] = prompt;
        });

    private static string OpenCodeDispatch(string root, string prompt)
    {
        JsonObject envelope = new()
        {
            ["schema"] = PluginEnvelope.SchemaV1,
            ["harness"] = "opencode",
            ["phase"] = "before",
            ["tool"] = "task",
            ["call-id"] = "call-1",
            ["session"] = "ses-1",
            ["cwd"] = root,
            ["args"] = new JsonObject
            {
                ["description"] = "Implement T3",
                ["prompt"] = prompt,
                ["subagent_type"] = "dev-medium",
            },
        };
        return envelope.ToJsonString();
    }

    private static string FactoryDispatch(string root, string prompt)
    {
        JsonObject payload = new()
        {
            ["session_id"] = "session-factory-1",
            ["transcript_path"] = Path.Combine(root, ".factory", "transcripts", "session-factory-1.jsonl"),
            ["cwd"] = root,
            ["permission_mode"] = "default",
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = "Task",
            ["tool_input"] = new JsonObject
            {
                ["prompt"] = prompt,
                ["subagent_type"] = "dev-medium",
                ["description"] = "Implement T3",
            },
        };
        return payload.ToJsonString();
    }

    private static string PiDispatch(string root, string prompt)
    {
        JsonObject envelope = new()
        {
            ["schema"] = PluginEnvelope.SchemaV1,
            ["harness"] = "pi",
            ["phase"] = "before",
            ["tool"] = "Agent",
            ["call-id"] = "call-1",
            ["session"] = "ses-1",
            ["cwd"] = root,
            ["args"] = new JsonObject
            {
                ["description"] = "Implement T3",
                ["prompt"] = prompt,
                ["subagent_type"] = "dev-medium",
            },
        };
        return envelope.ToJsonString();
    }

    /// <summary>The prompt service a test answers, and the requests it was asked for.</summary>
    private sealed class ScriptedPrompts : ITaskPromptServiceFactory, ITaskPromptService
    {
        public List<TaskPromptRequest> Requests { get; } = [];

        public ITaskPromptService ForRepository(string repositoryRoot, KyberWeaveConfig config) => this;

        public TaskPromptResult Build(TaskPromptRequest request, KyberWeaveConfig config)
        {
            Requests.Add(request);
            return new TaskPromptResult(true, TaskPrompt, "csharp-dev", "medium", "plan", request.Technology, [], null);
        }
    }
}

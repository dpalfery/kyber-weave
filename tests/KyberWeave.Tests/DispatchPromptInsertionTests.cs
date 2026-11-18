using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;

using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The dispatch hook replaces a dispatch's task-id line with the whole task prompt, so a worker starts with
/// everything. It does so with the Arbiter on, after the rules judged the short dispatch, and with it off, where
/// prompt insertion is the only hook action. A dispatch that cannot be expanded is stopped, never started short.
/// </summary>
/// <remarks>
/// The adapters are driven with the recorded payload shapes the per-harness tests use, against a prompt service the
/// test answers, so the assertions are about the field each harness documents and about what the hook asks for.
/// </remarks>
public sealed class DispatchPromptInsertionTests : IDisposable
{
    private const string TaskPrompt =
        "<task>\nImplement the parser.\n</task>\n\n<left-out>\nNothing was left out.\n</left-out>";

    private const string TaskDispatch =
        "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement the plan parser.";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>The prompt service a test answers, and the requests it was asked for.</summary>
    private sealed class ScriptedPrompts : ITaskPromptServiceFactory, ITaskPromptService
    {
        public List<TaskPromptRequest> Requests { get; } = [];

        public string Prompt { get; init; } = TaskPrompt;

        public string? Refusal { get; init; }

        public ITaskPromptService ForRepository(string repositoryRoot, KyberWeaveConfig config) => this;

        public TaskPromptResult Build(TaskPromptRequest request, KyberWeaveConfig config)
        {
            Requests.Add(request);
            return Refusal is null
                ? new TaskPromptResult(true, Prompt, "csharp-dev", "medium", "plan", request.Technology, [], null)
                : TaskPromptResult.NotBuilt(Refusal);
        }
    }

    /// <summary>The decision engine every adapter is built over; it allows and records the prompt it judged.</summary>
    private sealed class RecordingEngine : IHookDecisionEngine
    {
        public string? SeenPrompt { get; private set; }

        public string? SeenTarget { get; private set; }

        public HookOutcome PreResult { get; init; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config)
        {
            SeenPrompt = prompt;
            SeenTarget = target;
            return PreResult;
        }

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);
    }

    /// <summary>An engine that asks the adapter to write a model, as the model-targeting path does.</summary>
    private sealed class ModelAskingEngine : IContextualHookDecisionEngine
    {
        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null) =>
            DecidePreDispatch(harness, caller, target, prompt, config, context, toolCallId, sessionId, dispatchModel: null);

        public HookOutcome DecidePreDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId,
            string? sessionId,
            string? dispatchModel = null) =>
            context.ConfirmModelWrite?.Invoke("sonnet") == true
                ? new HookOutcome(HookOutcomeKind.Allow, ModelToken: "sonnet")
                : new HookOutcome(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            string toolOutput,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null) =>
            new(HookOutcomeKind.Allow);
    }

    private static KyberWeaveConfig EnabledConfig() => new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static KyberWeaveConfig DisabledConfig() => new() { Arbiter = new ArbiterConfig { Enabled = false } };

    private static HookContext Context(ScriptedPrompts prompts) =>
        new(
            "/repo",
            () => "decision-test-1",
            TextWriter.Null,
            BuildDispatchPrompt: query =>
            {
                TaskPromptResult result = prompts.Build(
                    new TaskPromptRequest(
                        query.TaskId is null ? "docs/plans/plan.md" : "/repo",
                        query.TaskId,
                        query.PlanFile,
                        Agent: null,
                        query.PriorResultsPath,
                        Technology: query.Technology,
                        query.FailedAttempts),
                    EnabledConfig());
                return new DispatchPromptExpansion(result.Built, result.Prompt, result.Refusal);
            });

    private static IHarnessHookAdapter Adapter(IHookDecisionEngine engine, string token)
    {
        HarnessAdapterRegistry registry = ArbiterTestServices.Registry(engine);
        Assert.True(registry.TryGet(token, out IHarnessHookAdapter? adapter), $"No adapter for '{token}'.");
        return adapter!;
    }

    /// <summary>The dispatch prompt each rewrite harness received, read from the field that harness documents.</summary>
    private static string InsertedPrompt(string harness, string stdout) =>
        TryInsertedPrompt(harness, stdout)
        ?? throw new InvalidOperationException($"The document for '{harness}' carries no inserted prompt: {stdout}");

    /// <summary>The inserted prompt in the field the harness documents, or null when the document carries none.</summary>
    private static string? TryInsertedPrompt(string harness, string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        JsonElement root = document.RootElement;
        if (harness == "antigravity")
        {
            return root.TryGetProperty("overwrite", out JsonElement overwrite)
                && overwrite.TryGetProperty("Subagents", out JsonElement subagents)
                && subagents.ValueKind == JsonValueKind.Array
                && subagents.GetArrayLength() > 0
                && subagents[0].TryGetProperty("Prompt", out JsonElement prompt)
                && prompt.ValueKind == JsonValueKind.String
                    ? prompt.GetString()
                    : null;
        }

        string[] path = harness switch
        {
            "claude" or "copilot-vscode" or "factory" or "devin" =>
                ["hookSpecificOutput", "updatedInput", "prompt"],
            "copilot-cli" => ["modifiedArgs", "prompt"],
            "codex" => ["hookSpecificOutput", "updatedInput", "message"],
            "cursor" => ["updated_input", "prompt"],
            _ => ["args", "prompt"],
        };

        JsonElement current = root;
        foreach (string name in path)
        {
            if (!current.TryGetProperty(name, out JsonElement next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    /// <summary>One recorded dispatch payload per harness, carrying <paramref name="prompt"/>.</summary>
    private static string Payload(string harness, string prompt) => harness switch
    {
        "claude" => new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["cwd"] = "/repo",
            ["agent_type"] = "conductor",
            ["tool_name"] = "Agent",
            ["tool_input"] = new JsonObject { ["prompt"] = prompt, ["subagent_type"] = "csharp-dev" },
            ["tool_use_id"] = "toolu-dispatch-1",
        }.ToJsonString(),
        "copilot-vscode" => new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["cwd"] = "/repo",
            ["tool_name"] = "runSubagent",
            ["tool_input"] = new JsonObject { ["prompt"] = prompt, ["agentName"] = "csharp-dev" },
            ["tool_use_id"] = "toolu-copilot-vs-dispatch-1",
        }.ToJsonString(),
        "copilot-cli" => new JsonObject
        {
            ["cwd"] = "/repo",
            ["toolName"] = "task",
            ["toolArgs"] = new JsonObject { ["prompt"] = prompt, ["agent_type"] = "csharp-dev" },
        }.ToJsonString(),
        "codex" => new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["cwd"] = "/repo",
            ["tool_name"] = "spawn_agent",
            ["tool_input"] = new JsonObject { ["message"] = prompt, ["agent_type"] = "csharp-dev" },
            ["tool_use_id"] = "toolu-codex-dispatch-1",
        }.ToJsonString(),
        "cursor" => new JsonObject
        {
            ["hook_event_name"] = "preToolUse",
            ["cwd"] = "/repo",
            ["tool_name"] = "Task",
            ["tool_input"] = new JsonObject { ["prompt"] = prompt, ["subagent_type"] = "csharp-dev" },
            ["tool_use_id"] = "toolu-cursor-dispatch-1",
        }.ToJsonString(),
        "antigravity" => new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["conversationId"] = "conv-1",
            ["stepIdx"] = 4,
            ["workspacePaths"] = new JsonArray("/repo"),
            ["toolCall"] = new JsonObject
            {
                ["name"] = "invoke_subagent",
                ["args"] = new JsonObject
                {
                    ["Subagents"] = new JsonArray(
                        new JsonObject { ["TypeName"] = "csharp-dev", ["Prompt"] = prompt, ["Model"] = "keep" }),
                },
            },
        }.ToJsonString(),
        "factory" => new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["cwd"] = "/repo",
            ["tool_name"] = "Task",
            ["tool_input"] = new JsonObject { ["prompt"] = prompt, ["subagent_type"] = "csharp-dev" },
            ["session_id"] = "session-factory-1",
        }.ToJsonString(),
        "devin" => new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["cwd"] = "/repo",
            ["tool_name"] = "run_subagent",
            ["tool_input"] = new JsonObject { ["prompt"] = prompt, ["profile"] = "csharp-dev" },
            ["session_id"] = "session-devin-1",
        }.ToJsonString(),
        _ => new JsonObject
        {
            ["schema"] = PluginEnvelope.SchemaV1,
            ["harness"] = harness,
            ["phase"] = "before",
            ["tool"] = harness == "pi" ? "Agent" : "task",
            ["cwd"] = "/repo",
            ["args"] = new JsonObject { ["prompt"] = prompt, ["subagent_type"] = "csharp-dev" },
        }.ToJsonString(),
    };

    /// <summary>Every harness whose dispatch hook rewrites the call, as the delivery table records it.</summary>
    public static TheoryData<string> RewriteHarnesses => new()
    {
        "claude",
        "copilot-vscode",
        "copilot-cli",
        "codex",
        "cursor",
        "opencode",
        "kilo",
        "pi",
        "antigravity",
        "factory",
        "devin",
    };

    /// <summary>
    /// Every served harness has a path to the prompt: one listed as rewriting must insert it in its adapter, and one
    /// not listed takes the prompt file instead. A harness listed without an insertion would leave the worker with
    /// the short dispatch and no prompt at all.
    /// </summary>
    /// <summary>
    /// The reverse of the registry check: every token the insertion records as rewriting must be served by an
    /// adapter that actually inserts the prompt, including the aliases no adapter serves.
    /// </summary>
    [Fact]
    public void EveryRecordedRewriteHarnessIsServedByAnAdapterThatInsertsThePrompt()
    {
        HarnessAdapterRegistry registry = ArbiterTestServices.Registry(new RecordingEngine());
        string expected = TaskPrompt + "\n\nImplement the plan parser.";

        foreach (string token in DispatchPromptInsertion.RewriteHarnessTokens)
        {
            Assert.True(
                registry.TryGet(token, out IHarnessHookAdapter? adapter),
                $"Harness '{token}' is recorded as rewriting the dispatch, but no adapter serves it.");
            string payload = Payload(token, TaskDispatch);
            string stdout = adapter!.Handle(json(payload), payload, "conductor", EnabledConfig(), Context(new ScriptedPrompts()));

            Assert.Equal(expected, TryInsertedPrompt(token, stdout));
        }
    }

    [Fact]
    public void EveryServedHarnessEitherRewritesTheDispatchOrTakesThePromptFile()
    {
        HarnessAdapterRegistry registry = ArbiterTestServices.Registry(new RecordingEngine());
        string expected = TaskPrompt + "\n\nImplement the plan parser.";

        foreach (string token in registry.Tokens.Order(StringComparer.Ordinal))
        {
            Assert.True(registry.TryGet(token, out IHarnessHookAdapter? adapter), $"No adapter for '{token}'.");
            string payload = Payload(token, TaskDispatch);
            string stdout = adapter!.Handle(json(payload), payload, "conductor", EnabledConfig(), Context(new ScriptedPrompts()));
            string? inserted = stdout.Length == 0 ? null : TryInsertedPrompt(token, stdout);

            bool rewrites = DispatchPromptInsertion.CanRewriteDispatch(token);
            Assert.True(
                rewrites ? string.Equals(inserted, expected, StringComparison.Ordinal) : inserted is null,
                rewrites
                    ? $"Harness '{token}' is listed as rewriting the dispatch but its adapter inserted no prompt: {stdout}"
                    : $"Harness '{token}' is not listed as rewriting, so the prompt file must serve it, but its adapter inserted a prompt.");
        }
    }

    [Theory]
    [MemberData(nameof(RewriteHarnesses))]
    public void EachRewriteHarnessCarriesTheFullPromptInTheFieldItDocuments(string harness)
    {
        RecordingEngine engine = new();
        IHarnessHookAdapter adapter = Adapter(engine, harness);
        string payload = Payload(harness, TaskDispatch);

        string stdout = adapter.Handle(json(payload), payload, "conductor", EnabledConfig(), Context(new ScriptedPrompts()));

        Assert.Equal(TaskPrompt + "\n\nImplement the plan parser.", InsertedPrompt(harness, stdout));
    }

    private static JsonElement json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>
    /// A rule's denial is the decision, even when the prompt could not be built as well: reporting only the
    /// repair command would hide the policy refusal behind a documentation error on those two harnesses.
    /// </summary>
    [Theory]
    [InlineData("factory")]
    [InlineData("devin")]
    public void ARuleDenialOutranksAPromptThatCannotBeBuilt(string harness)
    {
        RecordingEngine engine = new()
        {
            PreResult = new HookOutcome(HookOutcomeKind.Deny, "denied by the roster rule"),
        };
        ScriptedPrompts prompts = new() { Refusal = "No prompt: the task file is stale." };
        IHarnessHookAdapter adapter = Adapter(engine, harness);
        string payload = Payload(harness, TaskDispatch);

        string stdout = adapter.Handle(json(payload), payload, "conductor", EnabledConfig(), Context(prompts));

        using JsonDocument document = JsonDocument.Parse(stdout);
        string reason = harness == "devin"
            ? document.RootElement.GetProperty("reason").GetString()!
            : document.RootElement.GetProperty("hookSpecificOutput")
                .GetProperty("permissionDecisionReason").GetString()!;
        Assert.Contains("denied by the roster rule", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("stale", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A return event carries the prompt the harness executed, so an expanded prompt there is proof the hook
    /// inserted it. A pre event's prompt is the conductor's text: typing the tags must never claim an insertion.
    /// </summary>
    [Fact]
    public void OnlyAReturnEventCarriesAProvenPromptInsertion()
    {
        Assert.False(new ArbiterEvent { Phase = "pre", Prompt = TaskPrompt }.PromptInserted());
        Assert.True(new ArbiterEvent { Phase = "post", Prompt = TaskPrompt }.PromptInserted());
        Assert.True(new ArbiterEvent { Phase = "return", Prompt = TaskPrompt }.PromptInserted());
        Assert.False(new ArbiterEvent { Phase = "post", Prompt = TaskDispatch }.PromptInserted());
    }

    /// <summary>
    /// A rule that also writes the model returns the allow path that writes the model; the prompt inserted for
    /// the allowed dispatch must still reach the worker.
    /// </summary>
    [Fact]
    public void AModelWriteOnTheAllowPathKeepsTheInsertedPrompt()
    {
        (int exit, string stdout) = Run(
            Host(new ModelAskingEngine(), EnabledConfig(), new ScriptedPrompts()),
            ClaudeHookAdapter.Token,
            Payload("claude", TaskDispatch));

        Assert.Equal(0, exit);
        using JsonDocument document = JsonDocument.Parse(stdout);
        JsonElement updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.Equal("sonnet", updated.GetProperty("model").GetString());
        Assert.Equal(TaskPrompt + "\n\nImplement the plan parser.", updated.GetProperty("prompt").GetString());
    }

    /// <summary>
    /// Every allow path delivers the inserted prompt: a note, a rewritten dispatch and a pass-through would otherwise
    /// start the worker with the short dispatch and no prompt.
    /// </summary>
    [Theory]
    [InlineData("claude", HookOutcomeKind.AllowWithNote)]
    [InlineData("claude", HookOutcomeKind.AllowWithRewrite)]
    [InlineData("claude", HookOutcomeKind.PassThrough)]
    [InlineData("copilot-vscode", HookOutcomeKind.AllowWithNote)]
    [InlineData("copilot-vscode", HookOutcomeKind.AllowWithRewrite)]
    [InlineData("copilot-vscode", HookOutcomeKind.PassThrough)]
    [InlineData("copilot-cli", HookOutcomeKind.AllowWithNote)]
    [InlineData("copilot-cli", HookOutcomeKind.AllowWithRewrite)]
    [InlineData("copilot-cli", HookOutcomeKind.PassThrough)]
    [InlineData("codex", HookOutcomeKind.AllowWithNote)]
    [InlineData("codex", HookOutcomeKind.AllowWithRewrite)]
    [InlineData("codex", HookOutcomeKind.PassThrough)]
    [InlineData("cursor", HookOutcomeKind.AllowWithNote)]
    [InlineData("cursor", HookOutcomeKind.AllowWithRewrite)]
    [InlineData("cursor", HookOutcomeKind.PassThrough)]
    [InlineData("opencode", HookOutcomeKind.AllowWithNote)]
    [InlineData("opencode", HookOutcomeKind.AllowWithRewrite)]
    [InlineData("opencode", HookOutcomeKind.PassThrough)]
    [InlineData("antigravity", HookOutcomeKind.AllowWithNote)]
    [InlineData("antigravity", HookOutcomeKind.AllowWithRewrite)]
    [InlineData("antigravity", HookOutcomeKind.PassThrough)]
    [InlineData("factory", HookOutcomeKind.AllowWithNote)]
    [InlineData("factory", HookOutcomeKind.AllowWithRewrite)]
    [InlineData("factory", HookOutcomeKind.PassThrough)]
    [InlineData("devin", HookOutcomeKind.AllowWithNote)]
    [InlineData("devin", HookOutcomeKind.AllowWithRewrite)]
    [InlineData("devin", HookOutcomeKind.PassThrough)]
    public void EveryAllowPathDeliversTheInsertedPrompt(string harness, HookOutcomeKind kind)
    {
        RecordingEngine engine = new()
        {
            PreResult = new HookOutcome(kind, Reason: "note from the rule", RewrittenInputJson: "{\"marker\":\"kept\"}"),
        };
        IHarnessHookAdapter adapter = Adapter(engine, harness);
        string payload = Payload(harness, TaskDispatch);

        string stdout = adapter.Handle(json(payload), payload, "conductor", EnabledConfig(), Context(new ScriptedPrompts()));

        Assert.Equal(TaskPrompt + "\n\nImplement the plan parser.", InsertedPrompt(harness, stdout));
    }

    [Fact]
    public void ARetrysFailureReportAndCountersSurviveIntoThePromptAndTheWorker()
    {
        const string dispatch =
            "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\nFAILED_ATTEMPTS: 2\nPRIOR_RESULTS: results.md\n\n" +
            "## Failure from attempt 2\n\nThe gate failed on `dotnet build`.";
        ScriptedPrompts prompts = new();
        IHarnessHookAdapter adapter = Adapter(new RecordingEngine(), ClaudeHookAdapter.Token);

        string stdout = adapter.Handle(json(Payload("claude", dispatch)), Payload("claude", dispatch), "conductor",
            EnabledConfig(), Context(prompts));

        TaskPromptRequest request = Assert.Single(prompts.Requests);
        Assert.Equal("T3", request.TaskId);
        Assert.Equal("docs/plans/plan.md", request.Plan);
        Assert.Equal("results.md", request.PriorResultsPath);

        string sent = InsertedPrompt("claude", stdout);
        Assert.StartsWith(TaskPrompt, sent, StringComparison.Ordinal);
        Assert.Contains("## Failure from attempt 2", sent, StringComparison.Ordinal);
        Assert.Contains("The gate failed on `dotnet build`.", sent, StringComparison.Ordinal);
    }

    [Fact]
    public void ADispatchWithATechnologyAndNoTaskGetsThatTechnologysParts()
    {
        const string dispatch = "TECHNOLOGY: csharp\n\nResearch the caching options for the new endpoint.";
        ScriptedPrompts prompts = new();
        IHarnessHookAdapter adapter = Adapter(new RecordingEngine(), ClaudeHookAdapter.Token);

        string stdout = adapter.Handle(json(Payload("claude", dispatch)), Payload("claude", dispatch), "conductor",
            EnabledConfig(), Context(prompts));

        TaskPromptRequest request = Assert.Single(prompts.Requests);
        Assert.Null(request.TaskId);
        Assert.Equal("csharp", request.Technology);
        Assert.Contains("Research the caching options for the new endpoint.", InsertedPrompt("claude", stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExpandedPromptIsNotExpandedASecondTime()
    {
        ScriptedPrompts prompts = new();
        RecordingEngine engine = new();
        IHarnessHookAdapter adapter = Adapter(engine, ClaudeHookAdapter.Token);
        string payload = Payload("claude", TaskPrompt);

        string stdout = adapter.Handle(json(payload), payload, "conductor", DisabledConfig(), Context(prompts));

        // Nothing is asked of the prompt service, and a document the adapter still writes carries the prompt as it
        // arrived rather than a second copy of it.
        Assert.Empty(prompts.Requests);
        if (stdout.Length > 0)
        {
            Assert.Equal(TaskPrompt, InsertedPrompt("claude", stdout));
        }
    }

    private HookCommand Host(IHookDecisionEngine engine, KyberWeaveConfig config, ScriptedPrompts prompts)
    {
        HarnessAdapterRegistry registry = ArbiterTestServices.Registry(engine);
        return new HookCommand(
            registry,
            new FixedConfig(config),
            ArbiterTestServices.Get<IDispatchClaimStore>(),
            ArbiterTestServices.Get<IClaudeHookCoverage>(),
            ArbiterTestServices.Get<IProcessEnvironment>(),
            new FakeIdGenerator(),
            () => "decision-test-1",
            prompts);
    }

    private static (int Exit, string Stdout) Run(HookCommand command, string harness, string payload)
    {
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run(harness, "conductor", payload, stdout, log);
        return (exit, stdout.ToString());
    }

    [Fact]
    public void WithTheArbiterOffTheHostStillInsertsThePrompt()
    {
        ScriptedPrompts prompts = new();
        (int exit, string stdout) = Run(
            Host(new RecordingEngine(), DisabledConfig(), prompts), ClaudeHookAdapter.Token, Payload("claude", TaskDispatch));

        Assert.Equal(0, exit);
        Assert.Equal(TaskPrompt + "\n\nImplement the plan parser.", InsertedPrompt("claude", stdout));
        Assert.Single(prompts.Requests);
    }

    [Fact]
    public void WithTheArbiterOnTheRulesJudgeTheShortDispatchBeforeThePromptIsInserted()
    {
        RecordingEngine engine = new();
        ScriptedPrompts prompts = new();

        (int exit, string stdout) = Run(
            Host(engine, EnabledConfig(), prompts), ClaudeHookAdapter.Token, Payload("claude", TaskDispatch));

        Assert.Equal(0, exit);
        Assert.Equal(TaskDispatch, engine.SeenPrompt);
        Assert.StartsWith(TaskPrompt, InsertedPrompt("claude", stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void APromptThatCannotBeBuiltStopsTheDispatchAndNamesTheRepairCommand()
    {
        const string refusal =
            "No prompt: the task file for docs/plans/plan.md is missing or does not match the plan. " +
            "Run 'kyber-weave docs tasks docs/plans/plan.md --write', then build the prompt again.";
        ScriptedPrompts prompts = new() { Refusal = refusal };

        (int exit, string stdout) = Run(
            Host(new RecordingEngine(), DisabledConfig(), prompts), ClaudeHookAdapter.Token, Payload("claude", TaskDispatch));

        Assert.Equal(0, exit);
        using JsonDocument document = JsonDocument.Parse(stdout);
        JsonElement output = document.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Contains("kyber-weave docs tasks docs/plans/plan.md --write", output.GetProperty("permissionDecisionReason").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("updatedInput", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the closed task-dispatch headers count, in their canonical spelling, with a task id the task files use.
    /// An ordinary dispatch that starts with a capitalized word is not a task dispatch and is left untouched.
    /// </summary>
    [Theory]
    [InlineData("Task: find usages of X")]
    [InlineData("task: T3")]
    [InlineData("TASK: find usages of X")]
    [InlineData("STATUS: blocked")]
    public void ADispatchThatIsNotATaskDispatchPassesThroughUntouched(string dispatch)
    {
        ScriptedPrompts prompts = new();

        (int exit, string stdout) = Run(
            Host(new RecordingEngine(), DisabledConfig(), prompts), ClaudeHookAdapter.Token, Payload("claude", dispatch));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(prompts.Requests);
    }

    [Fact]
    public void ARetryFailureLineUnderTheHeadersSurvivesInsteadOfBeingReadAsAHeader()
    {
        const string dispatch =
            "TASK: T3\nPLAN_FILE: docs/plans/plan.md\nFAILED_ATTEMPTS: 2\nError: build failed\n\nFix the build.";
        ScriptedPrompts prompts = new();

        (int exit, string stdout) = Run(
            Host(new RecordingEngine(), DisabledConfig(), prompts), ClaudeHookAdapter.Token, Payload("claude", dispatch));

        Assert.Equal(0, exit);
        TaskPromptRequest request = Assert.Single(prompts.Requests);
        Assert.Equal("T3", request.TaskId);
        Assert.Equal(2, request.FailedAttempts);
        string sent = InsertedPrompt("claude", stdout);
        Assert.EndsWith("Error: build failed\n\nFix the build.", sent, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshHostWithTheArbiterOffInstallsTheDispatchHookOnTheDispatchersOnly()
    {
        SquadSource source = SquadSourceLoader.Load(ArbiterSquadFixturePath());
        IReadOnlyList<SquadArbiterHook> hooks = ArbiterHookWiring.BuildHooks(
            source, new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: 5), SquadDeploymentScope.Project);

        Assert.NotEmpty(hooks);
        Assert.Contains(hooks, hook => hook.Caller == ArbiterSquadFixture.Conductor);
        Assert.DoesNotContain(hooks, hook => hook.Caller == ArbiterSquadFixture.CsharpDev);
        Assert.All(hooks, hook => Assert.Contains("kyber-weave-arbiter hook", hook.CommandLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARenderWithTheArbiterOffWritesTheDispatchHookToTheDispatchersAgentFile()
    {
        string source = ArbiterSquadFixturePath();
        SquadRenderResult result = await new ClaudeRenderer().RenderAsync(new SquadRenderRequest(
            SourceDirectory: source,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: 5)).Prepared());

        Assert.True(result.Success, string.Join("; ", result.Errors));
        string conductor = FileText(result, $".claude/agents/{ArbiterSquadFixture.Conductor}.md");
        string worker = FileText(result, $".claude/agents/{ArbiterSquadFixture.CsharpDev}.md");

        Assert.Contains("kyber-weave-arbiter hook", conductor, StringComparison.Ordinal);
        Assert.DoesNotContain("kyber-weave-arbiter", worker, StringComparison.Ordinal);
    }

    private static string FileText(SquadRenderResult result, string relativePath) =>
        System.Text.Encoding.UTF8.GetString(
            Assert.Single(result.Files, file => file.RelativePath == relativePath).Content.Span);

    /// <summary>
    /// A cancellation is the hook's caller stopping the work, not a prompt that could not be built. Denying the
    /// dispatch would report a failure that never happened and swallow the cancellation the host is waiting on.
    /// </summary>
    [Fact]
    public void ACancelledPromptBuildPropagatesTheCancellationRatherThanDenyingTheDispatch()
    {
        using TempDirectory repo = new();
        HookContext context = new(
            RepoRoot: repo.Path,
            NewDecisionId: () => "decision-1",
            Log: TextWriter.Null,
            BuildDispatchPrompt: _ => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => DispatchPromptInsertion.Expand(TaskDispatch, context));
    }

    private static string ArbiterSquadFixturePath() => SharedFixture.Path;

    private static readonly ArbiterSquadFixture SharedFixture = ArbiterSquadFixture.Create();

    [Fact]
    public void TheDoctorReportsAHarnessWhoseDeploymentLacksTheDispatchHook()
    {
        using TempDirectory home = new();
        string repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".kyber-weave"));
        Directory.CreateDirectory(Path.Combine(repo, ".github", "hooks"));
        File.WriteAllText(
            Path.Combine(repo, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n  enabled: false\n");
        File.WriteAllText(
            Path.Combine(repo, ".github", "hooks", "kyber-arbiter.json"),
            "{ \"hooks\": {} }");

        using ArbiterTestHost host = ArbiterTestHost.Create(services => services.AddSingleton<IProcessEnvironment>(
            new FakeProcessEnvironment(() => home.Path)));
        ISquadStateStore store = host.Get<ISquadStateStore>();
        SquadReceipt receipt = new(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Project,
            repo,
            DateTimeOffset.UtcNow,
            [],
            [new SquadOwnedFile(".github/hooks/kyber-arbiter.json", new string('a', 64), "copilot", Adopted: false)]);
        string receiptPath = store.ResolveReceiptPath(repo, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, store.SerializeReceipt(receipt));

        ArbiterDoctorReport report = host.Get<IArbiterDoctorService>().Inspect(repo, null, new KyberWeave.Core.Diagnostics.DiagnosticReport());

        HarnessGatingRow copilot = Assert.Single(report.Gating.Rows, row => row.Token == "copilot");
        Assert.True(copilot.Ungated);
        Assert.Equal("kyber-weave squad update", copilot.FixCommand);
        Assert.Contains("dispatch hook", copilot.State, StringComparison.Ordinal);
        Assert.Contains("Trust:", copilot.State, StringComparison.Ordinal);
        Assert.Contains(report.Lines, line => line.Message.Contains("dispatch hook", StringComparison.Ordinal));
    }

    /// <summary>
    /// The plan-read exemption follows the hook's own insertion record. A conductor that types the prompt tags
    /// itself still gets the rule, because the text it controls proves nothing.
    /// </summary>
    [Fact]
    public void ThePlanReadExemptionFollowsTheHooksInsertionNotThePromptsText()
    {
        KyberWeaveConfig config = new()
        {
            Arbiter = ArbiterConfigDefaults.Product,
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };
        const string planRead =
            "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nRead `docs/plans/plan.md` and implement T3.";

        ArbiterFactSet shortFacts = TriggerFactBuilder.Build("delegate", "csharp-dev", planRead, "conductor", "harness", config);
        ArbiterFactSet typedFacts = TriggerFactBuilder.Build(
            "delegate", "csharp-dev", TaskPrompt + "\n\n" + planRead, "conductor", "harness", config);
        ArbiterFactSet insertedFacts = TriggerFactBuilder.Build(
            "delegate", "csharp-dev", TaskPrompt + "\n\n" + planRead, "conductor", "harness", config,
            promptInserted: true);

        Assert.True(shortFacts.TryGet("delegation.planning-paths", out ArbiterFact? shortPaths));
        Assert.True(typedFacts.TryGet("delegation.planning-paths", out ArbiterFact? typedPaths));
        Assert.True(insertedFacts.TryGet("delegation.planning-paths", out ArbiterFact? insertedPaths));
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<string>>(shortPaths!.Value!));
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<string>>(typedPaths!.Value!));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(insertedPaths!.Value!));
    }

    /// <summary>A host configuration loader that answers one configuration for every root.</summary>
    private sealed class FixedConfig(KyberWeaveConfig config) : IArbiterHostConfig
    {
        public KyberWeaveConfig Load(string repoRoot, HookScope scope = HookScope.Project, string? homeDirectory = null) => config;

        public KyberWeaveConfig? LoadProject(string repoRoot) => config;

        public string ResolveHomeDirectory(string? homeDirectory) => homeDirectory ?? "/home/test";
    }
}

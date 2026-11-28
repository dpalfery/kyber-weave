using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fixtures;
using Xunit;
using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 16.5: the Factory harness adapter. Covers the dispatch
/// gate on <c>PreToolUse</c> with <c>tool_name == "Task"</c>, the target from
/// <c>tool_input.subagent_type</c> and the prompt from <c>tool_input.prompt</c>,
/// pairing by <c>pair-digest</c> (Factory documents no call id, so the adapter
/// passes a null call id and the ledger pairs the return by digest), the
/// Claude-format answer shapes (<c>hookSpecificOutput.permissionDecision</c>
/// deny with <c>permissionDecisionReason</c>, <c>allow</c> with the complete
/// <c>updatedInput</c>, a plain pass writing nothing), post-dispatch outcomes
/// delivered as <c>hookSpecificOutput.additionalContext</c>, and the internal
/// error writing the deny shape.
/// Fixture payloads live under <c>Fixtures/arbiter-hooks/factory/</c>, built
/// from [F11] of the task packet.
/// RED: <c>FactoryHookAdapter</c> does not exist yet.
/// </summary>
public sealed class FactoryHookAdapterTests
{
    private static readonly string TranscriptDirectory =
        Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "factory"));

    private readonly InMemoryFileSystem _files = new();

    private const string TranscriptFixtureName = "transcript-background-task.jsonl";

    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public string? SeenPreTarget;

        public string? SeenPrePrompt;

        public bool ThrowOnPre;

        public bool ThrowOnPost;

        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config)
        {
            SeenPreTarget = target;
            SeenPrePrompt = prompt;
            if (ThrowOnPre)
            {
                throw new InvalidOperationException("provider is down");
            }

            return PreResult;
        }

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config)
        {
            if (ThrowOnPost)
            {
                throw new InvalidOperationException("provider is down");
            }

            return PostResult;
        }
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(caller, target, prompt, config));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string caller,
            string? target,
            string toolOutput,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(caller, target, toolOutput, config));
    }

    private sealed class ContextualEngine : IContextualHookDecisionEngine
    {
        public string? SeenPreHarness;

        public string? SeenPreTarget;

        public string? SeenPrePrompt;

        public string? SeenPreToolCallId;

        public string? SeenPreSessionId;

        public string? SeenPostToolCallId;

        public string? SeenPostSessionId;

        public string? SeenPostTarget;

        public string? SeenPostPrompt;

        public string? SeenPostOutput;

        public int PostCalls;

        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            PreResult;

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            PostResult;

        public HookOutcome DecidePreDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null)
        {
            SeenPreHarness = harness;
            SeenPreTarget = target;
            SeenPrePrompt = prompt;
            SeenPreToolCallId = toolCallId;
            SeenPreSessionId = sessionId;
            return PreResult;
        }

        public HookOutcome DecidePostDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            string toolOutput,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null)
        {
            PostCalls++;
            SeenPostTarget = target;
            SeenPostPrompt = prompt;
            SeenPostOutput = toolOutput;
            SeenPostToolCallId = toolCallId;
            SeenPostSessionId = sessionId;
            return PostResult;
        }
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(caller, target, prompt, config));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string caller,
            string? target,
            string toolOutput,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(caller, target, toolOutput, config));
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId,
            string? sessionId,
            string? dispatchModel,
            string? deliveredPrompt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(
                harness, caller, target, prompt, config, context, toolCallId, sessionId));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string harness,
            string caller,
            string? target,
            string prompt,
            string toolOutput,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(
                harness, caller, target, prompt, toolOutput, config, context, toolCallId, sessionId));
    }

    /// <summary>Records what a stop event asks recovery to close, and decides nothing else.</summary>
    private sealed class RecoveryEngine : IContextualHookDecisionEngine, ILostReturnRecovery
    {
        public string? RecoveredHarness { get; private set; }

        public string? RecoveredSession { get; private set; }

        public IReadOnlyList<string> RecoveredNames { get; private set; } = [];

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
            string? sessionId = null) => new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            string toolOutput,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null) => new(HookOutcomeKind.Allow);

        public int RecoverLostReturn(
            string harness,
            string? sessionId,
            IReadOnlyList<string> agentNames,
            HookContext context,
            string? agentId = null,
            string? stopEvent = null)
        {
            RecoveredHarness = harness;
            RecoveredSession = sessionId;
            RecoveredNames = agentNames;
            return 0;
        }
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(caller, target, prompt, config));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string caller,
            string? target,
            string toolOutput,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(caller, target, toolOutput, config));
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId,
            string? sessionId,
            string? dispatchModel,
            string? deliveredPrompt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(
                harness, caller, target, prompt, config, context, toolCallId, sessionId));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string harness,
            string caller,
            string? target,
            string prompt,
            string toolOutput,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(
                harness, caller, target, prompt, toolOutput, config, context, toolCallId, sessionId));
        /// <inheritdoc/>
        public Task<int> RecoverLostReturnAsync(
            string harness,
            string? sessionId,
            IReadOnlyList<string> agentNames,
            HookContext context,
            string? agentId = null,
            string? stopEvent = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RecoverLostReturn(harness, sessionId, agentNames, context, agentId, stopEvent));
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };


    private static string Fixture(string name) =>
        ArbiterHookFixtures.Factory(name);

    private (int Exit, string Stdout, string Log) Run(
        string stdin,
        string? caller,
        IHookDecisionEngine engine,
        Func<string, KyberWeaveConfig>? loader = null)
    {
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine, new FileFactoryTranscriptSource(_files)),
            loader ?? (_ => EnabledConfig()),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run(FactoryHookAdapter.Token, caller, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    [Fact]
    public void PreDispatch_Deny_WritesDenyShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", specific.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", specific.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, specific.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void PreDispatch_Allow_SpecialistTarget_StripsCompleteInput()
    {
        (int exit, string stdout, _) = Run(Fixture("pre-task-dispatch.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", specific.GetProperty("permissionDecision").GetString());

        // updatedInput is the complete tool_input: every original field survives.
        JsonElement updated = specific.GetProperty("updatedInput");
        Assert.Equal("dev-medium", updated.GetProperty("subagent_type").GetString());
        Assert.Equal("Implement T16.5", updated.GetProperty("description").GetString());
        string prompt = updated.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal("Implement the adapter.", prompt);
        Assert.DoesNotContain("KYBER-ARBITER", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_Allow_NonSpecialistTarget_WritesNothing()
    {
        string stdin = Fixture("pre-task-dispatch.json").Replace(
            "\"subagent_type\": \"dev-medium\"", "\"subagent_type\": \"docs-dev\"", StringComparison.Ordinal);

        (int exit, string stdout, _) = Run(stdin, null, new ScriptedEngine());

        Assert.Equal(0, exit);

        // A plain pass writes nothing: an explicit allow would auto-approve the
        // call, and the empty stdout on exit 0 is the documented proceed.
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void PreDispatch_AllowWithRewrite_WritesRewrittenInput()
    {
        const string rewritten = """{"prompt":"Hello.","subagent_type":"dev-medium"}""";
        ScriptedEngine engine = new()
        {
            PreResult = new HookOutcome(HookOutcomeKind.AllowWithRewrite, RewrittenInputJson: rewritten),
        };

        (int exit, string stdout, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement updated = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.Equal("Hello.", updated.GetProperty("prompt").GetString());
    }

    [Fact]
    public void PreDispatch_TargetAndPrompt_ComeFromSubagentTypeAndPrompt()
    {
        ContextualEngine engine = new();

        (int exit, _, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(FactoryHookAdapter.Token, engine.SeenPreHarness);
        Assert.Equal("dev-medium", engine.SeenPreTarget);
        Assert.StartsWith("KYBER-ARBITER: true", engine.SeenPrePrompt, StringComparison.Ordinal);
        Assert.Contains("Implement the adapter.", engine.SeenPrePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_MissingTarget_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ContextualEngine engine = new();
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        // An event with no sub-agent target passes through untouched (design 1.7):
        // empty stdout, exit 0, and no configuration load.
        int exit = command.Run(FactoryHookAdapter.Token, null, Fixture("pre-task-no-target.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
        Assert.Null(engine.SeenPreTarget);
    }

    [Fact]
    public void PreDispatch_NonDispatchTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ContextualEngine engine = new();
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        // Plan reads stay advisory on Factory: the Read guard does not apply, and
        // a non-Task tool passes through before configuration loads.
        int exit = command.Run(FactoryHookAdapter.Token, null, Fixture("pre-non-dispatch.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public void PreDispatch_LowercaseTaskTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ContextualEngine engine = new();
        string stdin = Fixture("pre-task-dispatch.json").Replace(
            "\"tool_name\": \"Task\"", "\"tool_name\": \"task\"", StringComparison.Ordinal);
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        // The dispatch tool matches exactly: lowercase "task" is not a dispatch.
        int exit = command.Run(FactoryHookAdapter.Token, null, stdin, stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public void PostDispatch_Block_WritesAdditionalContext()
    {
        const string note =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, note) };

        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PostToolUse", specific.GetProperty("hookEventName").GetString());
        Assert.Equal(note, specific.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void PostDispatch_Deny_AlsoWritesAdditionalContext()
    {
        const string note =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.Deny, note) };

        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal(
            note,
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void PostDispatch_Annotation_WritesAdditionalContext()
    {
        const string note = "The dispatch returned without its report section.";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostAnnotation, note) };

        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal(
            note,
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void PostDispatch_Allow_WritesNothing()
    {
        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void BackgroundLaunch_RecordsNoReturn()
    {
        ContextualEngine engine = new();

        (int exit, string stdout, _) = Run(Fixture("post-task-launched.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(0, engine.PostCalls);
    }

    private static string LaunchReporting(JsonNode response)
    {
        JsonObject root = JsonNode.Parse(Fixture("post-task-launched.json"))!.AsObject();
        root["tool_response"] = response;
        return root.ToJsonString();
    }

    private static string LaunchText() =>
        JsonNode.Parse(Fixture("post-task-launched.json"))!["tool_response"]!.GetValue<string>();

    [Theory]
    [InlineData("  \n")]
    [InlineData("\uFEFF")]
    [InlineData("\u2003\u00A0")]
    [InlineData(" \uFEFF ")]
    public void BackgroundLaunch_AfterLeadingNoise_RecordsNoReturn(string noise)
    {
        ContextualEngine engine = new();

        Run(LaunchReporting(JsonValue.Create(noise + LaunchText())), null, engine);

        Assert.Equal(0, engine.PostCalls);
    }

    [Fact]
    public void BackgroundLaunch_InsideAJsonEnvelope_RecordsNoReturn()
    {
        ContextualEngine engine = new();
        JsonObject envelope = new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = LaunchText() }),
        };

        Run(LaunchReporting(envelope), null, engine);

        Assert.Equal(0, engine.PostCalls);
    }

    [Fact]
    public void ForegroundReport_ThatQuotesTheLaunchNotice_IsRecorded()
    {
        ContextualEngine engine = new();
        JsonObject envelope = new()
        {
            ["note"] = LaunchText(),
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = "All done." },
                new JsonObject { ["type"] = "text", ["text"] = LaunchText() }),
        };

        Run(LaunchReporting(envelope), null, engine);

        Assert.Equal(1, engine.PostCalls);
    }

    private string TemporaryTranscript(string content, string extension = ".jsonl")
    {
        string path = Path.Combine(TranscriptDirectory, "transcript-" + _files.OperationCount + extension);
        _files.CreateDirectory(TranscriptDirectory);
        _files.WriteAllText(path, content);
        return path;
    }

    private string TwoLaunches(string firstTask, string secondTask)
    {
        string fixture = ArbiterHookFixtures.Factory(TranscriptFixtureName);
        string first = fixture.Replace("TASK: T9", "TASK: " + firstTask, StringComparison.Ordinal);
        string second = fixture
            .Replace("TASK: T9", "TASK: " + secondTask, StringComparison.Ordinal)
            .Replace("toolu_bdrk_01JedzB4Bo7BUfu42oAMKSWs", "toolu_second", StringComparison.Ordinal)
            .Replace("Repeat this line", "Second attempt", StringComparison.Ordinal);
        return TemporaryTranscript(first.TrimEnd() + "\n" + second);
    }

    [Fact]
    public void SubagentStop_WhenLaunchesNameDifferentTasks_RecordsNothing()
    {
        string transcript = TwoLaunches("T8", "T9");
        try
        {
            ContextualEngine engine = new();

            Run(SubagentStop(transcript), null, engine);

            Assert.Equal(0, engine.PostCalls);
        }
        finally
        {
            _files.DeleteFile(transcript);
        }
    }

    [Fact]
    public void SubagentStop_WhenHeaderlessLaunchesDiffer_RecordsNothing()
    {
        string withHeaders = TwoLaunches("T8", "T9");
        string transcript = TemporaryTranscript(
            _files.ReadAllText(withHeaders)
                .Replace("KYBER-ARBITER: true\\nPLAN_FILE: docs/plans/x.md\\nTASK: T8\\n\\n", string.Empty, StringComparison.Ordinal)
                .Replace("KYBER-ARBITER: true\\nPLAN_FILE: docs/plans/x.md\\nTASK: T9\\n\\n", string.Empty, StringComparison.Ordinal));
        try
        {
            Assert.DoesNotContain("KYBER-ARBITER", _files.ReadAllText(transcript), StringComparison.Ordinal);
            ContextualEngine engine = new();

            Run(SubagentStop(transcript), null, engine);

            Assert.Equal(0, engine.PostCalls);
        }
        finally
        {
            _files.DeleteFile(transcript);
            _files.DeleteFile(withHeaders);
        }
    }

    [Fact]
    public void SubagentStop_WhenATaskWasRetried_TakesTheLatestLaunch()
    {
        string transcript = TwoLaunches("T9", "T9");
        try
        {
            ContextualEngine engine = new();

            Run(SubagentStop(transcript), null, engine);

            Assert.Equal(1, engine.PostCalls);
            Assert.Contains("Second attempt", engine.SeenPostPrompt, StringComparison.Ordinal);
        }
        finally
        {
            _files.DeleteFile(transcript);
        }
    }

    [Fact]
    public void SubagentStop_WithAnEmptyResult_ReadsTheError()
    {
        ContextualEngine engine = new();
        JsonObject root = JsonNode.Parse(SubagentStop(TranscriptFixture()))!.AsObject();
        root["task_result"] = string.Empty;
        root["task_error"] = "RED_EVIDENCE: from-error";

        Run(root.ToJsonString(), null, engine);

        Assert.Equal("RED_EVIDENCE: from-error", engine.SeenPostOutput);
    }

    [Fact]
    public void SubagentStop_OnAFileThatIsNotATranscript_RecordsNothing()
    {
        string notTranscript = TemporaryTranscript(ArbiterHookFixtures.Factory(TranscriptFixtureName), ".txt");
        try
        {
            ContextualEngine engine = new();

            Run(SubagentStop(notTranscript), null, engine);

            Assert.Equal(0, engine.PostCalls);
        }
        finally
        {
            _files.DeleteFile(notTranscript);
        }
    }

    [Theory]
    [InlineData("task_name")]
    [InlineData("transcript_path")]
    public void SubagentStop_WithAWrongTypeField_AllowsAndLogs(string field)
    {
        ContextualEngine engine = new();
        JsonObject root = JsonNode.Parse(SubagentStop(TranscriptFixture()))!.AsObject();
        root[field] = 5;

        (int exit, string stdout, string log) = Run(root.ToJsonString(), null, engine);

        // ADR 0072: a fault the host cannot decide on allows, with the code in the log.
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("task_name")]
    [InlineData("transcript_path")]
    public void SubagentStop_MissingWhatItNeeds_PassesThroughWithoutConfig(string field)
    {
        ContextualEngine engine = new();
        JsonObject root = JsonNode.Parse(SubagentStop(TranscriptFixture()))!.AsObject();
        root.Remove(field);

        (int exit, string stdout, _) = Run(
            root.ToJsonString(),
            null,
            engine,
            _ => throw new InvalidOperationException("config must not load"));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(0, engine.PostCalls);
    }

    private static string SubagentStop(string transcriptPath, string taskName = "capture run")
    {
        JsonObject root = JsonNode.Parse(Fixture("subagent-stop.json"))!.AsObject();
        root["transcript_path"] = transcriptPath;
        root["task_name"] = taskName;
        return root.ToJsonString();
    }

    private string TranscriptFixture() => TemporaryTranscript(ArbiterHookFixtures.Factory(TranscriptFixtureName));

    [Fact]
    public void SubagentStop_RecordsTheReportAgainstItsDispatch()
    {
        ContextualEngine engine = new();

        (int exit, string stdout, _) = Run(SubagentStop(TranscriptFixture()), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(1, engine.PostCalls);
        Assert.Equal("echoer", engine.SeenPostTarget);
        Assert.Contains("TASK: T9", engine.SeenPostPrompt, StringComparison.Ordinal);
        Assert.Equal("RED_EVIDENCE: capture-line", engine.SeenPostOutput);
        Assert.Null(engine.SeenPostToolCallId);
    }

    [Fact]
    public void SubagentStop_StripsHeadersForASpecialist_AsItsDispatchRan()
    {
        string transcript = TemporaryTranscript(
            ArbiterHookFixtures.Factory(TranscriptFixtureName).Replace("\"echoer\"", "\"dev-medium\"", StringComparison.Ordinal));
        try
        {
            ContextualEngine engine = new();

            Run(SubagentStop(transcript), null, engine);

            Assert.Equal("dev-medium", engine.SeenPostTarget);
            Assert.DoesNotContain("KYBER-ARBITER", engine.SeenPostPrompt, StringComparison.Ordinal);
        }
        finally
        {
            _files.DeleteFile(transcript);
        }
    }

    [Theory]
    [InlineData("no such description")]
    public void SubagentStop_WithNoMatchingLaunch_RecordsNothing(string taskName)
    {
        ContextualEngine engine = new();

        (int exit, string stdout, _) = Run(SubagentStop(TranscriptFixture(), taskName), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(0, engine.PostCalls);
    }

    [Fact]
    public void SubagentStop_WithNoLaunch_RecoversByTheTranscriptsTargetAgent()
    {
        // task_name is a description, not an agent. The dispatch's target is what a stop event can identify, and
        // the transcript's Task call is where the dispatch path reads it from too.
        string transcript = TemporaryTranscript(FirstTranscriptLine());
        try
        {
            RecoveryEngine engine = new();

            Run(SubagentStop(transcript), null, engine);

            Assert.Equal("factory", engine.RecoveredHarness);
            Assert.Equal("session-factory-capture", engine.RecoveredSession);
            Assert.Equal(["echoer"], engine.RecoveredNames);
        }
        finally
        {
            _files.DeleteFile(transcript);
        }
    }

    [Fact]
    public void SubagentStop_WithADescriptionNoTaskUses_RecoversNothing()
    {
        string transcript = TemporaryTranscript(FirstTranscriptLine());
        try
        {
            RecoveryEngine engine = new();

            Run(SubagentStop(transcript, "no such description"), null, engine);

            Assert.Empty(engine.RecoveredNames);
        }
        finally
        {
            _files.DeleteFile(transcript);
        }
    }

    /// <summary>The transcript's dispatch line alone: the Task call, with no launch result after it.</summary>
    private static string FirstTranscriptLine()
    {
        string transcript = ArbiterHookFixtures.Factory(TranscriptFixtureName);
        return transcript[..transcript.IndexOf('\n', StringComparison.Ordinal)] + "\n";
    }

    [Fact]
    public void SubagentStop_WithAnUnreadableTranscript_RecordsNothing()
    {
        ContextualEngine engine = new();

        (int exit, _, _) = Run(SubagentStop("/nonexistent/transcript.jsonl"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(0, engine.PostCalls);
    }

    [Fact]
    public void Pairing_NoCallId_PassesNullCallIdForDigestPairing()
    {
        ContextualEngine engine = new();

        // Factory documents no call id ([F11]), so the adapter fabricates none:
        // the engine receives a null call id for both phases and the ledger
        // pairs the return to its dispatch by pair-digest. The documented
        // session_id rides along for the ledger's scoping.
        (int preExit, _, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);
        (int postExit, _, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, preExit);
        Assert.Equal(0, postExit);
        Assert.Null(engine.SeenPreToolCallId);
        Assert.Null(engine.SeenPostToolCallId);
        Assert.Equal("session-factory-1", engine.SeenPreSessionId);
        Assert.Equal("session-factory-2", engine.SeenPostSessionId);
        Assert.Equal(1, engine.PostCalls);
    }

    [Fact]
    public void InternalError_AllowsAndLogs()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };

        (int exit, string stdout, string log) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        // ADR 0072: a fault the host cannot decide on allows, with the code in the log.
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalError_OnReturn_AlsoAllowsAndLogs()
    {
        ScriptedEngine engine = new() { ThrowOnPost = true };

        (int exit, string stdout, string log) = Run(Fixture("post-task-completed.json"), null, engine);

        // ADR 0072: a fault the host cannot decide on allows, with the code in the log.
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapters_AreRegisteredForFactoryToken()
    {
        ScriptedEngine engine = new();
        HarnessAdapterRegistry registry = ArbiterTestServices.Registry(engine);

        Assert.True(registry.TryGet(FactoryHookAdapter.Token, out IHarnessHookAdapter? adapter));
        Assert.IsType<FactoryHookAdapter>(adapter);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The Antigravity adapter against payloads captured from the real <c>agy</c> CLI on 2026-11-18: camelCase event name,
/// one flat <c>args</c> object, no caller. A dispatch without the <c>KYBER-ARBITER: true</c> marker is the standard
/// agent's and passes through with an explicit allow; every allow on a pre-tool event is <c>{"decision":"allow"}</c>.
/// </summary>
public sealed class AntigravityRealPayloadTests : IDisposable
{
    private const string Allow = "{\"decision\":\"allow\"}";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public AntigravityRealPayloadTests()
    {
        _host.Git.CommonDirectory = Path.Combine(_host.RepoRoot, ".git");
        _ = _host.WriteRepoConfig(
            "ontology:\n  docs-root: docs\narbiter:\n  enabled: true\n  provider:\n    kind: none\n");
    }

    public void Dispose() => _host.Dispose();

    private sealed class RecordingEngine : IContextualHookDecisionEngine
    {
        public List<(string? Target, string Prompt, string? CallId)> Pre { get; } = [];

        public List<(string? Target, string? ToolOutput, string? CallId)> Post { get; } = [];

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(
            string harness, string caller, string? target, string prompt, KyberWeaveConfig config,
            HookContext context, string? toolCallId = null, string? sessionId = null)
        {
            Pre.Add((target, prompt, toolCallId));
            return new HookOutcome(HookOutcomeKind.Allow);
        }

        public HookOutcome DecidePostDispatch(
            string harness, string caller, string? target, string prompt, string toolOutput, KyberWeaveConfig config,
            HookContext context, string? toolCallId = null, string? sessionId = null)
        {
            Post.Add((target, toolOutput, toolCallId));
            return new HookOutcome(HookOutcomeKind.Allow);
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

    private static string Fixture(string name) =>
        EmbeddedFixtureText.Read(typeof(AntigravityRealPayloadTests).Assembly, $"arbiter-hooks/antigravity/{name}");

    private (string Stdout, string Log) Run(string stdin, IHookDecisionEngine engine, bool enabled = true)
    {
        KyberWeaveConfig config = new() { Arbiter = new ArbiterConfig { Enabled = enabled } };
        HookCommand command = _host.HookCommand(_host.Registry(engine), _ => config, () => "real-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        Assert.Equal(0, command.Run(AntigravityHookAdapter.Token, null, stdin, stdout, log));
        return (stdout.ToString(), log.ToString());
    }

    private (string Stdout, string Log) RunReal(string stdin)
    {
        ArbiterHookDecisionEngine engine = _host.Engine(
            providerFactory: _ => new NoneProvider(), homeDirectory: () => _host.HomeDirectory);
        HookCommand command = _host.HookCommand(_host.Registry(engine), _host.LoadHostConfig, () => "real-2");
        using StringWriter stdout = new();
        using StringWriter log = new();
        Assert.Equal(0, command.Run(AntigravityHookAdapter.Token, null, stdin, stdout, log, homeDirectory: _host.HomeDirectory));
        return (stdout.ToString(), log.ToString());
    }

    private static string WithRepo(string json, string repo)
    {
        const string Placeholder = "\"/repo\"";
        if (!json.Contains(Placeholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The fixture carries no {Placeholder} placeholder to replace.");
        }

        return json.Replace(Placeholder, JsonSerializer.Serialize(repo), StringComparison.Ordinal);
    }

    private static string Reshape(string json, Action<JsonObject> edit)
    {
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        edit(root);
        return root.ToJsonString();
    }

    [Fact]
    public void DefaultAgentDispatch_PassesThroughWithTheExplicitAllow_AndRecordsNothing()
    {
        string stdin = WithRepo(Fixture("real-pre-default-agent.json"), _host.RepoRoot);

        (string stdout, string log) = RunReal(stdin);

        Assert.Equal(Allow, stdout);
        Assert.Empty(_host.Decisions);
        Assert.DoesNotContain(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultAgentDispatch_IsNotShownToTheEngine()
    {
        RecordingEngine engine = new();

        (string stdout, _) = Run(Fixture("real-pre-default-agent.json"), engine);

        Assert.Equal(Allow, stdout);
        Assert.Empty(engine.Pre);
    }

    [Fact]
    public void MarkedDispatch_IsJudged_WithTheToolCallIdAsTheCallId()
    {
        RecordingEngine engine = new();

        (string stdout, _) = Run(Fixture("real-pre-conductor-marked.json"), engine);

        Assert.Equal(Allow, stdout);
        (string? target, string prompt, string? callId) = Assert.Single(engine.Pre);
        Assert.Equal("research-agent", target);
        Assert.StartsWith("KYBER-ARBITER: true", prompt, StringComparison.Ordinal);
        Assert.Equal("call_12144506349609463889", callId);
    }

    [Fact]
    public void MarkedDispatchToAWriter_WithNoRunStarted_IsDeniedWithTheReason()
    {
        string stdin = WithRepo(
            Reshape(Fixture("real-pre-conductor-marked.json"), root =>
            {
                root["toolCall"]!["args"]!["TypeName"] = "dev-medium";
                root["toolCall"]!["args"]!["Prompt"] = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: 1.1\n\nImplement it.";
            }),
            _host.RepoRoot);

        (string stdout, _) = RunReal(stdin);

        JsonNode answer = JsonNode.Parse(stdout)!;
        Assert.Equal("deny", answer["decision"]!.GetValue<string>());
        Assert.Contains("KW-ARB-RUN-001", answer["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEmpty(_host.Decisions);
    }

    [Fact]
    public void MarkedRealDispatch_IsRecorded()
    {
        _ = RunReal(WithRepo(Fixture("real-pre-conductor-marked.json"), _host.RepoRoot));

        Assert.Contains(_host.Decisions, decision => decision.Target == "research-agent");
    }

    [Fact]
    public void CamelCaseEventName_IsParsed_AndSnakeCaseStillIs()
    {
        RecordingEngine engine = new();
        string snake = Reshape(Fixture("real-pre-conductor-marked.json"), root =>
        {
            root["hook_event_name"] = root["hookEventName"]!.DeepClone();
            root.Remove("hookEventName");
        });

        (string camelOut, string camelLog) = Run(Fixture("real-pre-conductor-marked.json"), engine);
        (string snakeOut, _) = Run(snake, engine);

        Assert.Equal(Allow, camelOut);
        Assert.Equal(Allow, snakeOut);
        Assert.DoesNotContain(HookCommand.FailClosedCode, camelLog, StringComparison.Ordinal);
        Assert.Equal(2, engine.Pre.Count);
    }

    [Fact]
    public void ListArgs_AreStillParsed_AndOnlyMarkedElementsAreJudged()
    {
        RecordingEngine engine = new();
        string stdin = Reshape(Fixture("pre-invoke-subagent-multi.json"), root =>
            root["toolCall"]!["args"]!["Subagents"]![1]!["Prompt"] = "No marker here.");

        (string stdout, _) = Run(stdin, engine);

        Assert.Equal(Allow, stdout);
        Assert.Equal("dev-medium", Assert.Single(engine.Pre).Target);
    }

    [Fact]
    public void ListOfOnlyUnmarkedElements_PassesThrough()
    {
        RecordingEngine engine = new();
        string stdin = Reshape(Fixture("pre-invoke-subagent-multi.json"), root =>
        {
            root["toolCall"]!["args"]!["Subagents"]![0]!["Prompt"] = "No marker here.";
            root["toolCall"]!["args"]!["Subagents"]![1]!["Prompt"] = "Nor here.";
        });

        (string stdout, _) = Run(stdin, engine);

        Assert.Equal(Allow, stdout);
        Assert.Empty(engine.Pre);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void UnparseablePre_IsAnExplicitAllow(string stdin)
    {
        (string stdout, string log) = Run(stdin, new RecordingEngine());

        Assert.Equal(Allow, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkedDispatchWithoutArgs_IsAnExplicitAllowAndLogs()
    {
        string stdin = Reshape(Fixture("real-pre-conductor-marked.json"), root => root["toolCall"]!.AsObject().Remove("args"));

        (string stdout, string log) = Run(stdin, new RecordingEngine());

        Assert.Equal(Allow, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEventName_IsAnExplicitAllowAndLogs()
    {
        string stdin = Reshape(Fixture("real-pre-conductor-marked.json"), root => root["hookEventName"] = "SessionStart");

        (string stdout, string log) = Run(stdin, new RecordingEngine());

        Assert.Equal(Allow, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledArbiter_WritesTheExplicitAllow()
    {
        (string stdout, _) = Run(Fixture("real-pre-conductor-marked.json"), new RecordingEngine(), enabled: false);

        Assert.Equal(Allow, stdout);
    }

    [Fact]
    public void NonDispatchTool_WritesTheExplicitAllow()
    {
        string stdin = Reshape(Fixture("real-pre-default-agent.json"), root => root["toolCall"]!["name"] = "view_file");

        (string stdout, _) = Run(stdin, new RecordingEngine());

        Assert.Equal(Allow, stdout);
    }

    [Fact]
    public void FailOpen_BeforeTheHostRuns_WritesTheExplicitAllow()
    {
        Assert.Equal(Allow, HarnessAdapterRegistry.DisabledAllowDocumentOf("antigravity"));
    }

    [Fact]
    public void FastPath_ForTheDefaultAgent_AnswersTheExplicitAllow()
    {
        Assert.True(HookFastPath.TryPassThrough("antigravity", null, Fixture("real-pre-default-agent.json"), out string? document));
        Assert.Equal(Allow, document);
        Assert.False(HookFastPath.TryPassThrough("antigravity", null, Fixture("real-pre-conductor-marked.json"), out _));
    }

    private static string RealPost(string preFixture, Action<JsonObject>? edit = null) =>
        Reshape(preFixture, root =>
        {
            root["hookEventName"] = "PostToolUse";

            // HandlePost reads the return from the payload, not from the tool call's args.
            root["result"] = "ok";
            edit?.Invoke(root);
        });

    [Fact]
    public void RealPost_ForAMarkedDispatch_IsRecordedAndWritesEmptyObject()
    {
        RecordingEngine engine = new();

        (string stdout, _) = Run(RealPost(Fixture("real-pre-conductor-marked.json")), engine);

        Assert.Equal("{}", stdout);
        (string? target, string? toolOutput, string? callId) = Assert.Single(engine.Post);
        Assert.Equal("research-agent", target);
        Assert.Equal("ok", toolOutput);
        Assert.Equal("call_12144506349609463889", callId);
    }

    [Fact]
    public void RealPost_ForTheDefaultAgent_RecordsNothingAndWritesEmptyObject()
    {
        RecordingEngine engine = new();

        (string stdout, _) = Run(RealPost(Fixture("real-pre-default-agent.json")), engine);

        Assert.Equal("{}", stdout);
        Assert.Empty(engine.Post);
    }

    [Fact]
    public void UnreadablePost_NeverBlocks()
    {
        string stdin = RealPost(
            Fixture("real-pre-conductor-marked.json"),
            root => root["toolCall"]!.AsObject().Remove("args"));

        (string stdout, _) = Run(stdin, new RecordingEngine());

        Assert.Equal("{}", stdout);
    }

    [Fact]
    public void Insertion_WithSubagentsAndAFlatPrompt_RewritesBoth()
    {
        HookContext context = new(
            "/repo",
            () => "decision-1",
            TextWriter.Null,
            BuildDispatchPrompt: _ => new DispatchPromptExpansion(true, "THE FULL PROMPT", null));
        string stdin = Reshape(Fixture("real-pre-conductor-marked.json"), root =>
        {
            root["toolCall"]!["args"]!["Prompt"] = "KYBER-ARBITER: true\nTASK: 1.1\n\nDo it.";
            root["toolCall"]!["args"]!["Subagents"] = new JsonArray(
                new JsonObject
                {
                    ["TypeName"] = "docs-dev",
                    ["Prompt"] = "KYBER-ARBITER: true\nTASK: 1.2\n\nDo that.",
                });
        });

        string? document = new AntigravityHookAdapter(new RecordingEngine())
            .RenderPromptInsertion(JsonDocument.Parse(stdin).RootElement, null, context);

        JsonNode overwrite = JsonNode.Parse(document!)!["overwrite"]!;
        Assert.StartsWith("THE FULL PROMPT", overwrite["Prompt"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith(
            "THE FULL PROMPT",
            overwrite["Subagents"]![0]!["Prompt"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FlatInsertion_OverwritesOnlyThePrompt_AndKeepsTheUserApprovalPrompt()
    {
        HookContext context = new(
            "/repo",
            () => "decision-1",
            TextWriter.Null,
            BuildDispatchPrompt: _ => new DispatchPromptExpansion(true, "THE FULL PROMPT", null));
        string stdin = Reshape(Fixture("real-pre-conductor-marked.json"), root =>
            root["toolCall"]!["args"]!["Prompt"] = "KYBER-ARBITER: true\nTASK: 1.1\n\nDo it.");

        string? document = new AntigravityHookAdapter(new RecordingEngine())
            .RenderPromptInsertion(JsonDocument.Parse(stdin).RootElement, null, context);

        JsonNode answer = JsonNode.Parse(document!)!;
        Assert.Equal("ask", answer["decision"]!.GetValue<string>());
        Assert.StartsWith("THE FULL PROMPT", answer["overwrite"]!["Prompt"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(answer["overwrite"]!["Subagents"]);
    }
}

using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A Copilot CLI turn that starts several workers in one <c>toolCalls</c> batch is one hook event and several
/// dispatches. Each call is decided and recorded on its own, under the call id its return carries, so a parallel
/// worker's return can pair. The payloads are the live ones a conductor run recorded on 2026-11-19.
/// </summary>
public sealed class CopilotParallelDispatchTests
{
    private const string Plan = "6-Docs/plans/2026-11-18-job-status-dashboard.md";
    private const string T1Call = "call_082a8f1c81d84a759d069b95e63f0e1a";
    private const string T2Call = "call_7dc161d793a14c7092d3f7957e42ed07";

    /// <summary>
    /// The Copilot CLI pre event carries a <c>toolCalls</c> batch. Every call is a dispatch: each is evaluated, and
    /// each records its own pre event under its own call id, so a parallel worker's return can pair.
    /// </summary>
    [Fact]
    public void ACliBatchEvaluatesEveryTaskCallWithItsOwnCallId()
    {
        CapturingEngine engine = new();
        using StringWriter stdout = new();
        using StringWriter log = new();
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            _ => new KyberWeaveConfig { Arbiter = new ArbiterConfig { Enabled = true } },
            () => "decision-test-1");

        int exit = command.Run("copilot-cli", null, BatchPre(), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal([T1Call, T2Call], engine.PreCallIds);
        Assert.Equal(["dev-hard", "dev-medium"], engine.PreTargets);
    }

    /// <summary>
    /// A batch whose first call is a <c>view</c> still carries the lens dispatches beside it: reading the first call
    /// alone lost eight of the twelve lens dispatches of the review council that ran in the same session.
    /// </summary>
    [Fact]
    public void ACliBatchWhoseFirstCallIsNotATaskStillEvaluatesTheMarkedDispatchesBesideIt()
    {
        CapturingEngine engine = new();
        using StringWriter stdout = new();
        using StringWriter log = new();
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            _ => new KyberWeaveConfig { Arbiter = new ArbiterConfig { Enabled = true } },
            () => "decision-test-2");

        _ = command.Run("copilot-cli", null, LensBatch(), stdout, log);

        Assert.Equal(["call_lens_a", "call_lens_b"], engine.PreCallIds);
    }

    /// <summary>A background launch remembers its dispatch by the call id the post carries, not by a rewritten prompt.</summary>
    [Fact]
    public void ABackgroundLaunchIsTrackedWithTheCallIdTheReturnCanPairWith()
    {
        CapturingEngine engine = new();
        using StringWriter stdout = new();
        using StringWriter log = new();
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            _ => new KyberWeaveConfig { Arbiter = new ArbiterConfig { Enabled = true } },
            () => "decision-test-3");

        _ = command.Run("copilot-cli", null, BackgroundLaunch(), stdout, log);

        BackgroundDispatch tracked = Assert.Single(engine.Tracked);
        Assert.Equal(T1Call, tracked.CallId);
        Assert.Equal("dev-hard", tracked.Target);
    }

    /// <summary>
    /// The insertion-only path — a hook answering without evaluation, such as an Arbiter that is off — must find the
    /// marked dispatch anywhere in the batch, not only in its first call: a turn that read a file and then dispatched
    /// a task would otherwise hand the worker no prompt and record nothing.
    /// </summary>
    [Fact]
    public void TheInsertionOnlyPathFindsAMarkedDispatchBesideANonTaskFirstCall()
    {
        CopilotHookAdapter adapter = new(new CapturingEngine(), CopilotHookAdapter.CliToken);
        HookContext context = new(
            "/Users/dave/git/repo",
            () => "decision-test-4",
            TextWriter.Null,
            BuildDispatchPrompt: _ => new DispatchPromptExpansion(true, "<task>the packet</task>", null));

        string? insertion = adapter.RenderPromptInsertion(
            JsonDocument.Parse(BatchWithAViewFirst()).RootElement.Clone(), null, context);

        Assert.NotNull(insertion);
        using JsonDocument document = JsonDocument.Parse(insertion);
        JsonElement modifiedArgs = document.RootElement.GetProperty("modifiedArgs");

        // The insertion must replace the marked dispatch's own arguments: a document built from the view call beside
        // it would carry that call's fields and hand the worker a prompt on the wrong call.
        Assert.Equal("dev-medium", modifiedArgs.GetProperty("agent_type").GetString());
        string prompt = modifiedArgs.GetProperty("prompt").GetString()!;
        Assert.StartsWith("<task>the packet</task>", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Task T2.", prompt, StringComparison.Ordinal);
        Assert.False(modifiedArgs.TryGetProperty("path", out _));
    }

    private static string BatchWithAViewFirst() =>
        $$"""
        {
          "sessionId": "87eb1b25-85dc-4dc9-8812-12a8121be761",
          "cwd": "/Users/dave/git/repo",
          "toolCalls": [
            { "id": "call_view_1", "name": "view", "args": { "path": "README.md" } },
            { "id": "{{T2Call}}", "name": "task", "args": { "agent_type": "dev-medium",
              "prompt": "KYBER-ARBITER: true\nPLAN_FILE: {{Plan}}\nTASK: T2\n\nTask T2." } }
          ]
        }
        """;

    private static string BatchPre() =>
        $$"""
        {
          "sessionId": "87eb1b25-85dc-4dc9-8812-12a8121be761",
          "cwd": "/Users/dave/git/repo",
          "toolCalls": [
            { "id": "{{T1Call}}", "name": "task", "args": { "agent_type": "dev-hard", "mode": "background",
              "prompt": "KYBER-ARBITER: true\nPLAN_FILE: {{Plan}}\nTASK: T1\n\nTask T1." } },
            { "id": "{{T2Call}}", "name": "task", "args": { "agent_type": "dev-medium", "mode": "background",
              "prompt": "KYBER-ARBITER: true\nPLAN_FILE: {{Plan}}\nTASK: T2\n\nTask T2." } }
          ]
        }
        """;

    private static string LensBatch() =>
        """
        {
          "sessionId": "87eb1b25-85dc-4dc9-8812-12a8121be761",
          "cwd": "/Users/dave/git/repo",
          "toolCalls": [
            { "id": "call_view_1", "name": "view", "args": { "path": "README.md" } },
            { "id": "call_lens_a", "name": "task", "args": { "agent_type": "review-lens",
              "prompt": "KYBER-ARBITER: true\nLENS: correctness\nREVIEW_RUN: cycle-1\n\nReview it." } },
            { "id": "call_lens_b", "name": "task", "args": { "agent_type": "review-lens",
              "prompt": "KYBER-ARBITER: true\nLENS: security\nREVIEW_RUN: cycle-1\n\nReview it." } }
          ]
        }
        """;

    private static string BackgroundLaunch() =>
        $$"""
        {
          "sessionId": "87eb1b25-85dc-4dc9-8812-12a8121be761",
          "cwd": "/Users/dave/git/repo",
          "toolCallId": "{{T1Call}}",
          "toolName": "task",
          "toolArgs": { "agent_type": "dev-hard", "mode": "background",
            "prompt": "<task>\nTask T1: the packet the hook inserted.\n</task>" },
          "toolResult": { "textResultForLlm": "Agent started in background with agent_id: 9a30af38-0e61-4a61-8785-43b058ab723a" }
        }
        """;

    /// <summary>Answers every dispatch with an allow and records the ids and targets it was handed.</summary>
    private sealed class CapturingEngine : IContextualHookDecisionEngine, IBackgroundDispatchEngine
    {
        public List<string?> PreCallIds { get; } = [];

        public List<string?> PreTargets { get; } = [];

        public List<BackgroundDispatch> Tracked { get; } = [];

        public BackgroundDispatchRecord? FindBackgroundDispatch(string sessionId, string agentId, HookContext context) => null;

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
            DecidePreDispatch(harness, caller, target, prompt, config, context, toolCallId, sessionId, null, null);

        public HookOutcome DecidePreDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId,
            string? sessionId,
            string? dispatchModel,
            string? deliveredPrompt)
        {
            PreCallIds.Add(toolCallId);
            PreTargets.Add(target);
            return new HookOutcome(HookOutcomeKind.Allow);
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
            string? sessionId = null) =>
            new(HookOutcomeKind.Allow);

        public void TrackBackgroundDispatch(BackgroundDispatch dispatch, HookContext context) => Tracked.Add(dispatch);

        public HookOutcome DecideBackgroundReturn(
            string harness,
            string sessionId,
            string agentId,
            string response,
            KyberWeaveConfig config,
            HookContext context) =>
            new(HookOutcomeKind.PassThrough);

        public IReadOnlyList<string> TakePendingNotes(string sessionId, string caller, HookContext context) => [];
    }
}

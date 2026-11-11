using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review-round contract for the Claude adapter's model write: a failed write, or a failed log line around it,
/// ends on the return-false path and the dispatch is still allowed; a cancellation propagates; a logged token
/// is always one line.
/// </summary>
public sealed class ClaudeHookAdapterModelWriteTests
{
    private const string HeaderedPrompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";

    /// <summary>Asks the adapter to write <see cref="Token"/> and allows, with the token only when the write held.</summary>
    private sealed class WritingEngine : IContextualHookDecisionEngine
    {
        public required string Token { get; init; }

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
            string? dispatchModel)
        {
            bool held = context.ConfirmModelWrite?.Invoke(Token) ?? false;
            return held
                ? new HookOutcome(HookOutcomeKind.Allow, ModelToken: Token)
                : new HookOutcome(HookOutcomeKind.Allow);
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
    }

    /// <summary>A log that throws on every line, to prove a logging failure does not escape.</summary>
    private sealed class FailingLog : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("log sink gone");
    }

    /// <summary>A log that throws on the first line containing <see cref="Trigger"/>, and keeps the rest.</summary>
    private sealed class FailOnceLog(string trigger) : TextWriter
    {
        private readonly StringBuilder _kept = new();

        private bool _thrown;

        public override Encoding Encoding => Encoding.UTF8;

        public string Text => _kept.ToString();

        public override void WriteLine(string? value)
        {
            if (!_thrown && value is not null && value.Contains(trigger, StringComparison.Ordinal))
            {
                _thrown = true;
                throw new IOException("log sink gone");
            }

            _ = _kept.Append(value).Append('\n');
        }
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static string DispatchPayload()
    {
        JsonObject input = new()
        {
            ["subagent_type"] = "csharp-dev",
            ["prompt"] = HeaderedPrompt,
            ["description"] = "Implement T2",
        };
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "claude", "pre-agent-dispatch.json")))
            ?? throw new InvalidOperationException("The shipped Claude fixture is not a JSON document.");
        root["tool_input"] = input;
        return root.ToJsonString();
    }

    private static string Handle(string token, TextWriter log, Exception? fault = null)
    {
        string raw = DispatchPayload();
        ClaudeHookAdapter adapter = new(new WritingEngine { Token = token }) { ModelWriteFault = fault };
        using JsonDocument doc = JsonDocument.Parse(raw);
        HookContext context = new(Directory.GetCurrentDirectory(), () => "decision-27", log);
        return adapter.Handle(doc.RootElement, raw, "conductor", EnabledConfig(), context);
    }

    private static JsonElement UpdatedInput(string stdout)
    {
        using JsonDocument doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput").Clone();
    }

    [Fact]
    public void UnlistedWriteFailure_StillAllowsTheDispatchWithoutTheModel()
    {
        using StringWriter log = new();

        string stdout = Handle("sonnet", log, new UnauthorizedAccessException("write refused"));

        JsonElement updated = UpdatedInput(stdout);
        Assert.False(updated.TryGetProperty("model", out _));
        Assert.Equal("Implement T2.", updated.GetProperty("prompt").GetString());
        Assert.Contains("model-write-failed", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("delivered: false", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OperationCanceled_PropagatesAndIsNotTurnedIntoAWriteFailure()
    {
        using StringWriter log = new();

        Assert.Throws<OperationCanceledException>(() => Handle("sonnet", log, new OperationCanceledException()));
    }

    [Fact]
    public void SuccessLogFails_TheModelIsNotWrittenAndTheDispatchIsStillAllowed()
    {
        FailOnceLog log = new("delivered: true");

        string stdout = Handle("sonnet", log);

        JsonElement updated = UpdatedInput(stdout);
        Assert.False(updated.TryGetProperty("model", out _));
        Assert.Equal("Implement T2.", updated.GetProperty("prompt").GetString());
        Assert.Contains("model-write-failed", log.Text, StringComparison.Ordinal);
        Assert.Contains("delivered: false", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLogLineFails_TheDispatchIsStillAllowedWithoutTheModel()
    {
        using FailingLog log = new();

        string stdout = Handle("sonnet", log);

        Assert.False(UpdatedInput(stdout).TryGetProperty("model", out _));
    }

    [Fact]
    public void DerivedAliasLogLine_KeepsTheRawTokenOnOneLine()
    {
        using StringWriter log = new();

        string stdout = Handle("claude-opus-5\nmodel: x; delivered: true", log);

        Assert.Equal("opus", UpdatedInput(stdout).GetProperty("model").GetString());
        Assert.DoesNotContain("\nmodel: x", "\n" + log.ToString(), StringComparison.Ordinal);
        Assert.Contains("model.reason: alias-derived (claude-opus-5 model: x; delivered: true -> opus)", log.ToString(), StringComparison.Ordinal);
    }
}

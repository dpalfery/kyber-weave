using System.Text.Json;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The hook host: one command, two kinds of hook. It chooses between dispatch gating
/// (the harness's dispatch tool) and the planning-path Read guard (implementation
/// specialists) by tool and caller class, through the harness adapter.
/// </summary>
/// <remarks>
/// The host writes the harness's decision document on stdout, and nothing else.
/// Logging goes to the <c>log</c> writer (stderr in production). The exit code is 0:
/// a block rides the decision document, so a failing gate never needs a failing
/// process. One top-level catch turns any exception into that harness's block
/// carrying <c>KW-ARB-HOOK-001</c>; on conductor dispatches the reason is an
/// escalation envelope with <c>ANSWER: error</c>.
/// </remarks>
public sealed class HookCommand
{
    /// <summary>The hook error code every host failure carries.</summary>
    public const string FailClosedCode = "KW-ARB-HOOK-001";

    private readonly HarnessAdapterRegistry _registry;
    private readonly Func<string, KyberWeaveConfig> _configLoader;
    private readonly Func<string> _newDecisionId;

    /// <summary>
    /// Creates a host over an adapter registry and a configuration loader.
    /// The loader is a port so tests can prove the fast path never invokes it.
    /// </summary>
    public HookCommand(
        HarnessAdapterRegistry registry,
        Func<string, KyberWeaveConfig> configLoader,
        Func<string>? newDecisionId = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configLoader);
        _registry = registry;
        _configLoader = configLoader;
        _newDecisionId = newDecisionId ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Runs one hook event. Returns 0 always: blocks ride stdout, and unknown-harness
    /// usage errors (which have no harness block shape) return 2 with stderr diagnostics.
    /// </summary>
    public int Run(
        string harness,
        string? caller,
        string stdin,
        TextWriter stdout,
        TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(log);

        if (!_registry.TryGet(harness, out IHarnessHookAdapter? adapter) || adapter is null)
        {
            log.WriteLine(
                $"Unknown harness '{harness}'. Known harnesses: " +
                $"{string.Join(", ", _registry.Tokens.Order(StringComparer.Ordinal))}. " +
                "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>].");
            return 2;
        }

        try
        {
            JsonElement payload = ParsePayload(stdin);
            if (adapter.IsPassThrough(payload, caller))
            {
                return 0;
            }

            string repoRoot = RepoRootOf(payload);
            KyberWeaveConfig config = _configLoader(repoRoot);
            if (!config.Arbiter.Enabled)
            {
                log.WriteLine("Arbiter is disabled (arbiter.enabled: false): allowing without evaluation.");
                return 0;
            }

            HookContext context = new(repoRoot, _newDecisionId, log);
            string output = adapter.Handle(payload, stdin, caller, config, context);
            if (output.Length > 0)
            {
                stdout.Write(output);
            }

            return 0;
        }
        catch (Exception ex)
        {
            string block;
            try
            {
                block = adapter.RenderFailClosed(OneLine(ex.Message), caller, stdin, _newDecisionId());
            }
            catch (Exception)
            {
                // The block renderer never throws by contract; this is the last resort
                // so a host failure still fails closed when the renderer itself breaks.
                block = """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"KW-ARB-HOOK-001: hook host failed."}}""";
            }

            stdout.Write(block);
            return 0;
        }
    }

    private static JsonElement ParsePayload(string stdin)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdin);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Malformed hook event JSON: {OneLine(ex.Message)}", ex);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidOperationException("Malformed hook event: expected a JSON object on stdin.");
        }

        // The document owns the element's buffers; cloning detaches the payload so the
        // document can be disposed before evaluation. Hook payloads are small.
        JsonElement payload = document.RootElement.Clone();
        document.Dispose();
        return payload;
    }

    private static string RepoRootOf(JsonElement payload)
    {
        if (payload.TryGetProperty("cwd", out JsonElement cwd)
            && cwd.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(cwd.GetString()))
        {
            return cwd.GetString()!;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
}

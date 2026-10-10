using System.Text.Json;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>Which side of the tool call a plugin envelope reports.</summary>
public enum PluginPhase
{
    /// <summary>The <c>before</c> gate: the shim asks whether the call may run.</summary>
    Before,

    /// <summary>The <c>after</c> report: the shim asks whether the result stands.</summary>
    After,
}

/// <summary>
/// The <c>kyber-arbiter.plugin-event/v1</c> envelope a plugin shim writes to the
/// hook host's stdin: <c>{schema, harness, phase, tool, call-id, session, cwd,
/// args, result}</c>, where <c>phase</c> is <c>before</c> or <c>after</c>.
/// </summary>
/// <remarks>
/// Property names are the envelope's wire names, including the hyphenated
/// <c>call-id</c>. The shim also accepts the <c>callId</c>/<c>callID</c> and
/// <c>sessionID</c> spellings when reading (the OpenCode plugin's input uses
/// <c>callID</c>/<c>sessionID</c>), but the host always documents
/// <c>call-id</c>. <c>args</c> and <c>result</c> are carried as cloned elements
/// so the parsed document can be disposed before evaluation.
/// </remarks>
public sealed class PluginEnvelope
{
    /// <summary>The only schema this host answers.</summary>
    public const string SchemaV1 = "kyber-arbiter.plugin-event/v1";

    /// <summary>The envelope schema.</summary>
    public string Schema { get; }

    /// <summary>The harness token the shim claims (for example <c>opencode</c>).</summary>
    public string Harness { get; }

    /// <summary>Which side of the call this envelope reports.</summary>
    public PluginPhase Phase { get; }

    /// <summary>The harness tool id (for example OpenCode's <c>task</c>).</summary>
    public string Tool { get; }

    /// <summary>The harness tool-call id, when the shim sent one.</summary>
    public string? CallId { get; }

    /// <summary>The harness session id, when the shim sent one.</summary>
    public string? Session { get; }

    /// <summary>The repository root the event ran under, when the shim sent one.</summary>
    public string? Cwd { get; }

    /// <summary>The complete tool arguments. <c>Undefined</c> when absent.</summary>
    public JsonElement Args { get; }

    /// <summary>The tool result, for <c>after</c> envelopes. <c>Undefined</c> when absent.</summary>
    public JsonElement Result { get; }

    private PluginEnvelope(
        string schema,
        string harness,
        PluginPhase phase,
        string tool,
        string? callId,
        string? session,
        string? cwd,
        JsonElement args,
        JsonElement result)
    {
        Schema = schema;
        Harness = harness;
        Phase = phase;
        Tool = tool;
        CallId = callId;
        Session = session;
        Cwd = cwd;
        Args = args;
        Result = result;
    }

    /// <summary>
    /// Parses and validates one envelope. Throws <see cref="InvalidOperationException"/>
    /// describing the defect when the payload is malformed: the hook host turns that
    /// into the harness's fail-closed block carrying <c>KW-ARB-HOOK-001</c>.
    /// </summary>
    public static PluginEnvelope Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Malformed plugin hook event: expected a JSON object on stdin.");
        }

        string? schema = GetString(payload, "schema");
        if (!string.Equals(schema, SchemaV1, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Malformed plugin hook event: unexpected schema '{schema ?? "<missing>"}'.");
        }

        string? harness = GetString(payload, "harness");
        if (string.IsNullOrWhiteSpace(harness))
        {
            throw new InvalidOperationException(
                "Malformed plugin hook event: the envelope carries no harness.");
        }

        string? phaseText = GetString(payload, "phase");
        string phaseDisplay = phaseText ?? "<missing>";
        PluginPhase phase;
        if (string.Equals(phaseText, "before", StringComparison.Ordinal))
        {
            phase = PluginPhase.Before;
        }
        else if (string.Equals(phaseText, "after", StringComparison.Ordinal))
        {
            phase = PluginPhase.After;
        }
        else
        {
            throw new InvalidOperationException(
                $"Malformed plugin hook event: unknown phase '{phaseDisplay}'.");
        }

        string? tool = GetString(payload, "tool");
        if (string.IsNullOrWhiteSpace(tool))
        {
            throw new InvalidOperationException(
                "Malformed plugin hook event: the envelope carries no tool.");
        }

        JsonElement args = payload.TryGetProperty("args", out JsonElement rawArgs)
            && rawArgs.ValueKind == JsonValueKind.Object
            ? rawArgs.Clone()
            : default;
        JsonElement result = payload.TryGetProperty("result", out JsonElement rawResult)
            ? rawResult.Clone()
            : default;

        return new PluginEnvelope(
            SchemaV1,
            harness,
            phase,
            tool,
            GetString(payload, "call-id", "callId", "callID"),
            GetString(payload, "session", "sessionId", "sessionID"),
            GetString(payload, "cwd"),
            args,
            result);
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}

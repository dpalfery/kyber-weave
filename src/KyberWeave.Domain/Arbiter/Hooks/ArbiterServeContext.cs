using System.ComponentModel;
using System.Text.Json.Serialization;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Mcp;

/// <summary>
/// The facts a calling agent asserts about one event. The server derives everything else
/// (caller, trigger, rules and ledger state) from these, so the decision is advisory: the
/// agent being gated chose what to assert.
/// </summary>
public sealed record ArbiterFacts
{
    /// <summary>The Squad agent being dispatched, or the lens or refutation target.</summary>
    [JsonPropertyName("target")]
    [Description("The Squad agent the event dispatches to, for example review-lens for a lens fan-out.")]
    public string? Target { get; init; }

    /// <summary>The plan file the dispatch works under, when there is one.</summary>
    [JsonPropertyName("plan-file")]
    [Description("The plan file the dispatch works under, when there is one.")]
    public string? PlanFile { get; init; }

    /// <summary>The plan task the dispatch implements, when there is one.</summary>
    [JsonPropertyName("task")]
    [Description("The plan task the dispatch implements, when there is one.")]
    public string? Task { get; init; }

    /// <summary>The dispatch prompt, without any header block the server adds.</summary>
    [JsonPropertyName("prompt")]
    [Description("The dispatch prompt body.")]
    public string? Prompt { get; init; }

    /// <summary>The lens names of a batched fan-out, one evaluation each, before the fan-out.</summary>
    [JsonPropertyName("lens")]
    [Description("The lens names of a batched review fan-out, one evaluation per lens.")]
    public IReadOnlyList<string>? Lens { get; init; }

    /// <summary>The finding a lens reported, as YAML.</summary>
    [JsonPropertyName("finding")]
    [Description("The finding a lens reported, as YAML.")]
    public string? Finding { get; init; }

    /// <summary>The event phase: pre (before dispatch, the default), or post and return (after it returns).</summary>
    [JsonPropertyName("phase")]
    [Description("The event phase: pre before a dispatch (the default), or post or return after it returns.")]
    public string? Phase { get; init; }

    /// <summary>The harness tool-call id, when the caller has one to pair a return with its dispatch.</summary>
    [JsonPropertyName("call-id")]
    [Description("The tool-call id that pairs a return with its dispatch, when the caller has one.")]
    public string? CallId { get; init; }

    /// <summary>The returned output, on a post or return event.</summary>
    [JsonPropertyName("output")]
    [Description("The returned output, on a post or return event.")]
    public string? Output { get; init; }
}

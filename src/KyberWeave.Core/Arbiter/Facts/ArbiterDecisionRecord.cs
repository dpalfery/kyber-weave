using System.Text.Json.Serialization;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The provider block of a decision record. Step-0-only decisions never reach
/// a model, so they carry <see cref="ArbiterProviderStatuses.ShortCircuited"/>
/// or <see cref="ArbiterProviderStatuses.NotEvaluated"/> and no endpoint.
/// </summary>
/// <param name="Kind">The provider kind.</param>
/// <param name="EndpointOrigin">The endpoint origin, never the full URL.</param>
/// <param name="Model">The model as answered.</param>
/// <param name="Usage">Token usage, when the provider reported it.</param>
/// <param name="LatencyMs">The call latency in milliseconds.</param>
/// <param name="Status"><c>answered</c>, <c>not-evaluated</c>, <c>over-budget</c>, <c>error</c> or <c>short-circuited</c>.</param>
public sealed record ArbiterProviderRecord(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("endpoint-origin")] string? EndpointOrigin,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("usage")] ArbiterProviderUsage? Usage,
    [property: JsonPropertyName("latency-ms")] int? LatencyMs,
    [property: JsonPropertyName("status")] string Status);

/// <summary>Token usage as reported by the provider.</summary>
/// <param name="InputTokens">Input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
public sealed record ArbiterProviderUsage(
    [property: JsonPropertyName("input-tokens")] int InputTokens,
    [property: JsonPropertyName("output-tokens")] int OutputTokens);

/// <summary>Values for the provider <c>status</c> field.</summary>
public static class ArbiterProviderStatuses
{
    /// <summary>The provider answered.</summary>
    public const string Answered = "answered";

    /// <summary>No step-1 call was due.</summary>
    public const string NotEvaluated = "not-evaluated";

    /// <summary>The budget stopped the call.</summary>
    public const string OverBudget = "over-budget";

    /// <summary>The call failed.</summary>
    public const string Error = "error";

    /// <summary>Step 0 was already decisive, so step 1 was skipped.</summary>
    public const string ShortCircuited = "short-circuited";
}

/// <summary>
/// One fact a rule read, logged by name and label only. Fact values are never
/// logged (Req 18.4): the label matches <see cref="ArbiterFactLabel"/>, but is
/// stored as a string so the schema stays serialization-stable.
/// </summary>
/// <param name="Name">The dotted fact name, e.g. <c>plan.task.files</c>.</param>
/// <param name="Label"><c>derived</c> or <c>asserted</c>.</param>
public sealed record ArbiterRuleFactRef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("label")] string Label);

/// <summary>One rule's contribution to a decision.</summary>
/// <param name="Id">The rule id.</param>
/// <param name="Step">The step that answered: 0 or 1.</param>
/// <param name="Answer">The answer name, or <c>undecidable</c>.</param>
/// <param name="Probabilities">Step-1 probabilities, when the rule scored them.</param>
/// <param name="Confidence">Step-1 confidence, when the rule reported one.</param>
/// <param name="Threshold">The threshold the answer cleared, when there is one.</param>
/// <param name="Effect">The answer mapped to its effect.</param>
/// <param name="Facts">The facts the rule read, by name and label.</param>
/// <param name="Evidence">The one-line evidence.</param>
public sealed record ArbiterDecisionRule(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("answer")] string Answer,
    [property: JsonPropertyName("probabilities")] IReadOnlyList<double>? Probabilities,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("threshold")] double? Threshold,
    [property: JsonPropertyName("effect")] string Effect,
    [property: JsonPropertyName("facts")] IReadOnlyList<ArbiterRuleFactRef> Facts,
    [property: JsonPropertyName("evidence")] string? Evidence);

/// <summary>
/// One decision record (<c>decisions.jsonl</c>, schema
/// <c>kyber-arbiter.decision/v1</c>). Fact values are not logged, except
/// paths, ids and the one-line evidence; prompts and code appear only as
/// digests (Req 18.4).
/// </summary>
/// <param name="Id">A sortable unique id.</param>
/// <param name="At">A UTC timestamp.</param>
/// <param name="Source"><c>hook</c> or <c>serve</c>.</param>
/// <param name="Harness">The harness token.</param>
/// <param name="Session">The payload's session id, when it has one.</param>
/// <param name="LedgerId">The ledger event this decides.</param>
/// <param name="Trigger">The trigger that fired.</param>
/// <param name="Caller">The calling agent, when classified.</param>
/// <param name="CallerSource"><c>harness</c>, <c>rendered</c>, <c>header</c>, <c>asserted</c> or <c>none</c>.</param>
/// <param name="Target">The dispatch target, when there is one.</param>
/// <param name="PlanFile">The plan file, when there is one.</param>
/// <param name="PlanDigest">The digest of the plan content the decision used.</param>
/// <param name="Task">The plan task, when there is one.</param>
/// <param name="RuleSet">SHA-256 of the effective rule set.</param>
/// <param name="Rules">Per-rule answers, in evaluation order.</param>
/// <param name="Provider">The provider call, when one was made.</param>
/// <param name="Outcome">The trigger's outcome (the combined effect).</param>
/// <param name="Repeat">The <c>REPEAT</c> count (§1.10).</param>
/// <param name="DurationMs">The evaluation time in milliseconds.</param>
public sealed record ArbiterDecisionRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("harness")] string Harness,
    [property: JsonPropertyName("session")] string? Session,
    [property: JsonPropertyName("ledger-id")] string LedgerId,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("caller")] string? Caller,
    [property: JsonPropertyName("caller-source")] string CallerSource,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("plan-file")] string? PlanFile,
    [property: JsonPropertyName("plan-digest")] string? PlanDigest,
    [property: JsonPropertyName("task")] string? Task,
    [property: JsonPropertyName("rule-set")] string RuleSet,
    [property: JsonPropertyName("rules")] IReadOnlyList<ArbiterDecisionRule> Rules,
    [property: JsonPropertyName("provider")] ArbiterProviderRecord Provider,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("repeat")] int Repeat,
    [property: JsonPropertyName("duration-ms")] int DurationMs)
{
    /// <summary>The decision schema id.</summary>
    public const string SchemaName = "kyber-arbiter.decision/v1";

    /// <summary>The decision schema id.</summary>
    [JsonPropertyName("schema")]
    public string Schema => SchemaName;
}

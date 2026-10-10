using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Providers;

/// <summary>
/// One batched step-1 call per trigger: the provider answers every step-1
/// rule the caller selected from the trigger's rules.
/// </summary>
/// <remarks>
/// Core defines this port and takes implementations as constructor arguments;
/// composition roots decide which provider answers. Tests inject a stub
/// <see cref="HttpMessageHandler"/>, so no test needs a live endpoint.
/// </remarks>
public interface IArbiterProvider
{
    /// <summary>
    /// Asks the provider for every rule with a step-1 question among
    /// <paramref name="rules"/>, in one batched call.
    /// </summary>
    /// <param name="rules">The trigger's selected rules; only enabled rules with an ask are sent.</param>
    /// <param name="facts">The fact set the rules' declared states read from.</param>
    /// <param name="step0Answers">Step-0 answers by rule id, for gating questions on <c>rules.&lt;id&gt;.answer</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ArbiterStep1BatchResult> AskStep1Async(
        IReadOnlyList<ArbiterRule> rules,
        ArbiterFactSet facts,
        IReadOnlyDictionary<string, string>? step0Answers = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Token usage the answering model reported, when it reported any.</summary>
/// <param name="InputTokens">Input tokens consumed.</param>
/// <param name="OutputTokens">Output tokens produced.</param>
public sealed record ArbiterStep1Usage(int InputTokens, int OutputTokens);

/// <summary>One trigger's batched step-1 answers.</summary>
/// <param name="AnswersByRule">The answer per rule id: an answer name, <c>undecidable</c>, or <c>not-evaluated</c>.</param>
/// <param name="Model">The model that answered, when a model answered.</param>
/// <param name="Usage">The usage the response returned, when it returned any.</param>
/// <param name="RequestSent">Whether any request left the machine.</param>
public sealed record ArbiterStep1BatchResult(
    IReadOnlyDictionary<string, string> AnswersByRule,
    string? Model,
    ArbiterStep1Usage? Usage,
    bool RequestSent)
{
    /// <summary>
    /// Marks step-1 rules not evaluated (provider <c>none</c>), which is not
    /// <c>undecidable</c>.
    /// </summary>
    public const string NotEvaluated = "not-evaluated";
}

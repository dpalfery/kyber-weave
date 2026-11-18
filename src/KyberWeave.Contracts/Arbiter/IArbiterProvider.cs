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

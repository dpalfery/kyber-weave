using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Providers;

/// <summary>
/// Provider <c>none</c>: marks step-1 rules not evaluated, which is not
/// <c>undecidable</c>. No request is ever sent.
/// </summary>
public sealed class NoneProvider : IArbiterProvider
{
    /// <inheritdoc />
    public Task<ArbiterStep1BatchResult> AskStep1Async(
        IReadOnlyList<ArbiterRule> rules,
        ArbiterFactSet facts,
        IReadOnlyDictionary<string, string>? step0Answers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(facts);
        Dictionary<string, string> answers = new(StringComparer.Ordinal);
        foreach (ArbiterRule rule in rules)
        {
            if (rule.Ask is not null)
            {
                answers[rule.Id] = ArbiterStep1BatchResult.NotEvaluated;
            }
        }

        return Task.FromResult(new ArbiterStep1BatchResult(answers, null, null, false));
    }
}

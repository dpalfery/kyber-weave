using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>Runs the Arbiter's evaluation flow for one hook, serve or eval event.</summary>
public interface IArbiterEvaluator
{
    /// <summary>Evaluates <paramref name="ev"/> end to end.</summary>
    /// <param name="ev">The classified harness event.</param>
    /// <param name="config">The host configuration, whose <c>arbiter:</c> section holds the rules.</param>
    /// <param name="options">Dry-run and model-write options; the hook defaults when null.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>The outcome and its ledger id. A fault inside the evaluation is an error result that allows, never an exception.</returns>
    Task<ArbiterEvaluationResult> EvaluateAsync(
        ArbiterEvent ev,
        KyberWeaveConfig config,
        ArbiterEvaluatorOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates evaluators over the Arbiter's registered ports and a caller's provider, key resolver and fact sources.</summary>
public interface IArbiterEvaluatorFactory
{
    /// <summary>Creates an evaluator whose ledger and decision log live under <paramref name="arbiterDirectory"/>.</summary>
    /// <param name="provider">The step-1 provider.</param>
    /// <param name="keyResolver">Resolves the provider key for an endpoint without blocking; the key is never logged.</param>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="gitFacts">Git-derived facts.</param>
    /// <param name="planReader">Plan-derived facts.</param>
    /// <param name="clock">The evaluation clock; the registered clock when null.</param>
    /// <param name="modelTargetsDirectory">
    /// The directory holding the harness's model targets table, or null for <paramref name="arbiterDirectory"/>.
    /// A global-scope evaluation keeps per-project state under the user's home but reads the table a user-scope
    /// install deployed beside the harness's hooks, so the two directories differ there.
    /// </param>
    IArbiterEvaluator Create(
        IArbiterProvider provider,
        Func<Uri, CancellationToken, Task<string?>> keyResolver,
        string arbiterDirectory,
        IArbiterGitFacts gitFacts,
        IArbiterPlanReader planReader,
        TimeProvider? clock = null,
        string? modelTargetsDirectory = null);
}

/// <summary>Classifies bot review comments against the code as it is now, through the comment rule.</summary>
public interface ICommentTriage
{
    /// <summary>Classifies one comment and records the decision.</summary>
    /// <param name="request">The comment to classify.</param>
    /// <param name="ct">Cancels the provider call.</param>
    Task<CommentTriageResult> ClassifyAsync(CommentTriageRequest request, CancellationToken ct = default);

    /// <summary>Classifies the rows of a bot's summary comment.</summary>
    /// <param name="request">The summary to classify.</param>
    /// <param name="ct">Cancels the provider calls.</param>
    Task<CommentSummaryResult> ClassifySummaryAsync(CommentSummaryRequest request, CancellationToken ct = default);
}

/// <summary>Creates comment triage services for a repository root and a provider.</summary>
public interface ICommentTriageFactory
{
    /// <summary>Creates the service.</summary>
    /// <param name="provider">The provider that answers the question.</param>
    /// <param name="repoRoot">The repository root the cited path is read under.</param>
    /// <param name="arbiterDirectory">The directory the decision log is written to.</param>
    /// <param name="ruleSet">The rule-set hash the decision records, or null for none.</param>
    /// <param name="botLogins">The logins of review bots whose summaries are classified; null means none.</param>
    /// <param name="rules">The effective rules; null means the shipped rules alone.</param>
    ICommentTriage Create(
        IArbiterProvider provider,
        string repoRoot,
        string arbiterDirectory,
        string? ruleSet = null,
        IReadOnlyList<string>? botLogins = null,
        IReadOnlyList<ArbiterRule>? rules = null);
}

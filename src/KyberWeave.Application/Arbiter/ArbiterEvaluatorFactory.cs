using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter;

/// <summary>Creates evaluators over the Arbiter's registered ports.</summary>
public sealed class ArbiterEvaluatorFactory(
    IArbiterStateFactory state,
    IArbiterModelTargetsReader modelTargets,
    IGateReportFacts gateReports,
    TimeProvider defaultClock,
    IIdGenerator ids) : IArbiterEvaluatorFactory
{
    /// <inheritdoc />
    public IArbiterEvaluator Create(
        IArbiterProvider provider,
        Func<Uri, string?> keyResolver,
        string arbiterDirectory,
        IArbiterGitFacts gitFacts,
        IArbiterPlanReader planReader,
        TimeProvider? clock = null,
        string? modelTargetsDirectory = null) =>
        new ArbiterEvaluator(
            provider,
            keyResolver,
            state.OpenLedger(arbiterDirectory),
            state.OpenDecisionLog(arbiterDirectory),
            gitFacts,
            planReader,
            modelTargets,
            gateReports,
            state,
            clock ?? defaultClock,
            ids,
            modelTargetsDirectory: modelTargetsDirectory);
}

/// <summary>Creates comment triage services over the Arbiter's registered ports.</summary>
public sealed class CommentTriageFactory(
    IFileTextFacts fileText,
    IArbiterStateFactory state,
    TimeProvider clock,
    IIdGenerator ids) : ICommentTriageFactory
{
    /// <inheritdoc />
    public ICommentTriage Create(
        IArbiterProvider provider,
        string repoRoot,
        string arbiterDirectory,
        string? ruleSet = null,
        IReadOnlyList<string>? botLogins = null,
        IReadOnlyList<ArbiterRule>? rules = null) =>
        new CommentTriageService(provider, repoRoot, arbiterDirectory, clock, ids, fileText, state, ruleSet, botLogins, rules);
}

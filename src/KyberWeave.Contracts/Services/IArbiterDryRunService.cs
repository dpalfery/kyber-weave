using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Contracts.Services;

/// <summary>One dry-run evaluator with the transport it owns. Disposing it releases the provider's HTTP handler.</summary>
public interface IArbiterEvaluatorSession : IDisposable
{
    /// <summary>Gets the evaluator to run the dry run through.</summary>
    IArbiterEvaluator Evaluator { get; }
}

/// <summary>Builds the provider and the offline evaluator the <c>arbiter eval</c> dry run and the comment surfaces use.</summary>
public interface IArbiterDryRunService
{
    /// <summary>
    /// Creates the step-1 provider: the configured provider, or the <c>--provider</c> override when one is supplied.
    /// Ownership of the returned provider transfers to the caller.
    /// </summary>
    /// <param name="config">The effective configuration.</param>
    /// <param name="providerOverride">The <c>--provider</c> value, or null.</param>
    /// <param name="report">Receives the diagnostic when the override or endpoint is unusable.</param>
    /// <param name="keyResolver">Receives the resolver the provider asks for its key.</param>
    /// <returns>The provider, or null when the override or endpoint is unusable.</returns>
    IArbiterProvider? CreateProvider(
        KyberWeaveConfig config,
        string? providerOverride,
        DiagnosticReport report,
        out Func<Uri, string?> keyResolver);

    /// <summary>
    /// Creates the dry-run evaluator over the registered ledger, decision log and fact sources. Ownership of the returned
    /// session transfers to the caller, which disposes it after the evaluation.
    /// </summary>
    /// <param name="config">The effective configuration.</param>
    /// <param name="repositoryRoot">The repository the plan facts are read under.</param>
    /// <param name="providerOverride">The <c>--provider</c> value, or null.</param>
    /// <param name="report">Receives the diagnostic when the provider cannot be built.</param>
    /// <returns>The session, or null when the provider cannot be built.</returns>
    IArbiterEvaluatorSession? CreateEvaluator(
        KyberWeaveConfig config,
        string repositoryRoot,
        string? providerOverride,
        DiagnosticReport report);
}

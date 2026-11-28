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

/// <summary>The provider a dry run asks, with the resolver it asks for its key.</summary>
/// <param name="Provider">The provider; ownership transfers to the caller.</param>
/// <param name="KeyResolver">The resolver the provider asks for its key.</param>
public sealed record ArbiterProviderCreation(
    IArbiterProvider Provider,
    Func<Uri, CancellationToken, Task<string?>> KeyResolver);

/// <summary>Builds the provider and the offline evaluator the <c>arbiter eval</c> dry run and the comment surfaces use.</summary>
public interface IArbiterDryRunService
{
    /// <summary>
    /// Creates the step-1 provider without blocking a thread: the configured provider, or the <c>--provider</c> override
    /// when one is supplied. Ownership of the returned provider transfers to the caller.
    /// </summary>
    /// <param name="config">The effective configuration.</param>
    /// <param name="providerOverride">The <c>--provider</c> value, or null.</param>
    /// <param name="report">Receives the diagnostic when the override or endpoint is unusable.</param>
    /// <param name="cancellationToken">Cancels the key resolution.</param>
    /// <returns>The provider and its key resolver, or null when the override or endpoint is unusable.</returns>
    Task<ArbiterProviderCreation?> CreateProviderAsync(
        KyberWeaveConfig config,
        string? providerOverride,
        DiagnosticReport report,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates the dry-run evaluator over the registered ledger, decision log and fact sources, without blocking a
    /// thread. Ownership of the returned session transfers to the caller, which disposes it after the evaluation.
    /// </summary>
    /// <param name="config">The effective configuration.</param>
    /// <param name="repositoryRoot">The repository the plan facts are read under.</param>
    /// <param name="providerOverride">The <c>--provider</c> value, or null.</param>
    /// <param name="report">Receives the diagnostic when the provider cannot be built.</param>
    /// <param name="cancellationToken">Cancels the key resolution.</param>
    /// <returns>The session, or null when the provider cannot be built.</returns>
    Task<IArbiterEvaluatorSession?> CreateEvaluatorAsync(
        KyberWeaveConfig config,
        string repositoryRoot,
        string? providerOverride,
        DiagnosticReport report,
        CancellationToken cancellationToken);
}

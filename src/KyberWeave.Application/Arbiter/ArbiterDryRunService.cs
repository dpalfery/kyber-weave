using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Arbiter;

/// <summary>Builds the provider and the offline evaluator for <c>arbiter eval</c> and the comment surfaces.</summary>
/// <param name="keys">Resolves the provider key.</param>
/// <param name="providers">Creates the HTTP providers.</param>
/// <param name="handlers">Creates the HTTP transport.</param>
/// <param name="evaluators">Creates the evaluator over the registered ledger and decision log.</param>
/// <param name="planFacts">Reads the plan facts.</param>
/// <param name="clock">The clock evaluations stamp records with.</param>
public sealed class ArbiterDryRunService(
    IArbiterKeyService keys,
    IArbiterProviderFactory providers,
    IArbiterHttpHandlers handlers,
    IArbiterEvaluatorFactory evaluators,
    IPlanFacts planFacts,
    TimeProvider clock) : IArbiterDryRunService
{
    /// <inheritdoc />
    public Task<ArbiterProviderCreation?> CreateProviderAsync(
        KyberWeaveConfig config,
        string? providerOverride,
        DiagnosticReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(report);

        return BuildProviderAsync(config, providerOverride, report, cancellationToken);
    }

    private async Task<ArbiterProviderCreation?> BuildProviderAsync(
        KyberWeaveConfig config,
        string? providerOverride,
        DiagnosticReport report,
        CancellationToken cancellationToken)
    {
        Func<Uri, CancellationToken, Task<string?>> keyResolver = keys.CreateKeyResolverAsync(config);

        ArbiterProviderKind kind = config.Arbiter.Provider.Kind;
        if (providerOverride is not null)
        {
            if (string.Equals(providerOverride, "none", StringComparison.OrdinalIgnoreCase))
            {
                kind = ArbiterProviderKind.None;
            }
            else if (string.Equals(providerOverride, "systemone", StringComparison.OrdinalIgnoreCase))
            {
                kind = ArbiterProviderKind.Systemone;
            }
            else
            {
                report.Add(new Diagnostic(
                    RuleValidator.MalformedSection,
                    Severity.Error,
                    $"--provider '{providerOverride}' is not supported.",
                    "--provider",
                    Hint: "Use none or systemone."));
                return null;
            }
        }

        if (kind == ArbiterProviderKind.None)
        {
            return new ArbiterProviderCreation(new NoneProvider(), keyResolver);
        }

        if (!Uri.TryCreate(config.Arbiter.Provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                $"arbiter.provider.endpoint '{config.Arbiter.Provider.Endpoint}' is not an absolute URL.",
                "arbiter.provider",
                Hint: "Use https://api.typesafe.ai/v1, or an http(s) Ollama URL."));
            return null;
        }

        IArbiterProvider provider = providers.CreateSystemOne(
            handlers.Create(),
            endpoint,
            config.Arbiter.Provider.Model,
            config.Arbiter.Provider.TimeoutMs,
            keyResolver);
        return new ArbiterProviderCreation(provider, keyResolver);
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The provider's lifetime transfers to the returned session, which the eval command disposes.")]
    public async Task<IArbiterEvaluatorSession?> CreateEvaluatorAsync(
        KyberWeaveConfig config,
        string repositoryRoot,
        string? providerOverride,
        DiagnosticReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(report);

        ArbiterProviderCreation? creation = await CreateProviderAsync(config, providerOverride, report, cancellationToken)
            .ConfigureAwait(false);
        if (creation is null)
            return null;

        string arbiterDirectory = ArbiterStateDirectory.ForProject(repositoryRoot);
        IArbiterEvaluator evaluator = evaluators.Create(
            creation.Provider,
            creation.KeyResolver,
            arbiterDirectory,
            new ArbiterCliGitFacts(),
            new ArbiterCliPlanReader(planFacts, repositoryRoot),
            clock);
        return new ArbiterEvaluatorSession(evaluator, creation.Provider as IDisposable);
    }

    /// <summary>One dry-run evaluator with its owned transport.</summary>
    private sealed class ArbiterEvaluatorSession(IArbiterEvaluator evaluator, IDisposable? owned) : IArbiterEvaluatorSession
    {
        private bool _disposed;

        /// <inheritdoc />
        public IArbiterEvaluator Evaluator { get; } = evaluator;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owned?.Dispose();
        }
    }
}

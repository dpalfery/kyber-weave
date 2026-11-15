using System.Globalization;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Health and integrity diagnostics for the canonical store
/// (<c>kyberdash doctor</c>, <c>kyberdash kyber vacuum</c>).
/// </summary>
/// <remarks>
/// The circuit-breaker state is reported verbatim rather than translated into a boolean health
/// verdict: an operator diagnosing a store whose breaker is half-open needs to see that word, and an
/// <c>isolated</c> breaker is a deliberate maintenance state, not a failure this service can judge.
/// </remarks>
public class DoctorService : IDoctorService
{
    private readonly ICanonStoreRepository store;
    private readonly ICircuitBreaker circuitBreaker;

    /// <summary>
    /// Initializes a new <see cref="DoctorService"/>.
    /// </summary>
    /// <param name="store">Canonical store repository to diagnose.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding store calls.</param>
    public DoctorService(ICanonStoreRepository store, ICircuitBreaker circuitBreaker)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(circuitBreaker);

        this.store = store;
        this.circuitBreaker = circuitBreaker;
    }

    /// <inheritdoc />
    public DoctorDiagnosticsReport RunDiagnostics()
    {
        store.EnsureInitialized();
        return new DoctorDiagnosticsReport(
            DatabasePath: store.DatabasePath,
            Records: store.CountRecords(),
            Sessions: store.ListSessions().Count,
            ObservedHarnesses: store.ListObservedHarnesses(),
            CircuitBreakerState: DescribeState(circuitBreaker.State));
    }

    /// <inheritdoc />
    public void RunVacuum() => store.Vacuum();

    /// <summary>Renders a breaker state as the lowercase wire string reported in diagnostics.</summary>
    /// <param name="state">Circuit breaker state.</param>
    /// <returns>The wire string (<c>closed</c>, <c>open</c>, <c>half-open</c>, or <c>isolated</c>).</returns>
    public static string DescribeState(DashCircuitState state) =>
        state switch
        {
            DashCircuitState.Open => "open",
            DashCircuitState.HalfOpen => "half-open",
            DashCircuitState.Isolated => "isolated",
            _ => "closed",
        };
}

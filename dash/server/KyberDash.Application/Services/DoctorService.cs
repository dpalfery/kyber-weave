using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Health and integrity diagnostics for the relational store
/// (<c>kyberdash doctor</c>, <c>kyberdash kyber vacuum</c>).
/// </summary>
/// <remarks>
/// <para>
/// The circuit-breaker state is reported verbatim rather than translated into a boolean health
/// verdict: an operator diagnosing a store whose breaker is half-open needs to see that word, and an
/// <c>isolated</c> breaker is a deliberate maintenance state, not a failure this service can judge.
/// </para>
/// <para>
/// <c>RunVacuum</c> has no store-level work to do: the relational store bounds its own WAL with
/// <c>journal_size_limit</c> at every initialization, so the command completes without touching the
/// store. The database path is reported from the concrete SQLite store; an in-memory or substitute
/// store reports an empty path.
/// </para>
/// </remarks>
public class DoctorService : IDoctorService
{
    private readonly IRelationalStoreReader reader;
    private readonly IOperationalStore operationalStore;
    private readonly ICircuitBreaker circuitBreaker;

    /// <summary>
    /// Initializes a new <see cref="DoctorService"/>.
    /// </summary>
    /// <param name="reader">Relational reader counting the store's records, sessions, and harnesses.</param>
    /// <param name="operationalStore">Operational store the diagnostics resolve the database path from.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding store calls.</param>
    public DoctorService(IRelationalStoreReader reader, IOperationalStore operationalStore, ICircuitBreaker circuitBreaker)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(circuitBreaker);

        this.reader = reader;
        this.operationalStore = operationalStore;
        this.circuitBreaker = circuitBreaker;
    }

    /// <inheritdoc />
    public DoctorDiagnosticsReport RunDiagnostics() => new(
        DatabasePath: (operationalStore as SqliteRelationalStore)?.DatabasePath ?? string.Empty,
        Records: reader.CountRecords(),
        Sessions: reader.Sessions().Count,
        ObservedHarnesses: reader.Harnesses(),
        CircuitBreakerState: DescribeState(circuitBreaker.State));

    /// <inheritdoc />
    public void RunVacuum()
    {
    }

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

using KyberDash.Core.Exceptions;
using Polly;
using Polly.CircuitBreaker;

namespace KyberDash.Core.Resilience;

/// <summary>Observable circuit breaker states independent of the underlying resilience library.</summary>
public enum DashCircuitState
{
    /// <summary>Circuit is closed and calls flow through normally.</summary>
    Closed,

    /// <summary>Circuit is open and calls fail fast with <see cref="CircuitBreakerOpenException"/>.</summary>
    Open,

    /// <summary>Circuit is testing recovery with a probe call.</summary>
    HalfOpen,

    /// <summary>Circuit was manually isolated.</summary>
    Isolated,
}

/// <summary>Configuration options for <see cref="SqliteCircuitBreaker"/>.</summary>
public class DashCircuitBreakerOptions
{
    /// <summary>Failure ratio threshold in <c>(0, 1]</c> that opens the circuit once <see cref="MinimumThroughput"/> is met.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Sliding sampling window for failure ratio evaluation.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum number of calls within <see cref="SamplingDuration"/> before the breaker can open.</summary>
    public int MinimumThroughput { get; set; } = 5;

    /// <summary>Duration the circuit stays open before transitioning to half-open.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Optional custom predicate controlling which exceptions count as circuit failures.</summary>
    public Func<Exception, bool>? ShouldHandleException { get; set; }
}

/// <summary>Circuit breaker contract guarding external process, database, or HTTP calls.</summary>
public interface ICircuitBreaker
{
    /// <summary>Current observable state of the circuit breaker.</summary>
    DashCircuitState State { get; }

    /// <summary>Executes a synchronous operation through the circuit breaker.</summary>
    T Execute<T>(Func<T> operation);

    /// <summary>Executes a synchronous void operation through the circuit breaker.</summary>
    void Execute(Action operation);

    /// <summary>Executes an asynchronous operation through the circuit breaker.</summary>
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>Manually closes the circuit breaker.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Polly v8 circuit breaker protecting <c>sqlite3</c> CLI and external calls.</summary>
public class SqliteCircuitBreaker : ICircuitBreaker
{
    private readonly ResiliencePipeline pipeline;
    private readonly CircuitBreakerStateProvider stateProvider;
    private readonly CircuitBreakerManualControl manualControl;

    /// <summary>Initializes a new <see cref="SqliteCircuitBreaker"/> using default or custom options.</summary>
    public SqliteCircuitBreaker(DashCircuitBreakerOptions? options = null)
    {
        DashCircuitBreakerOptions resolved = options ?? new DashCircuitBreakerOptions();
        stateProvider = new CircuitBreakerStateProvider();
        manualControl = new CircuitBreakerManualControl();

        Func<Exception, bool> shouldHandle = resolved.ShouldHandleException ?? DefaultShouldHandle;

        CircuitBreakerStrategyOptions strategyOptions = new()
        {
            FailureRatio = resolved.FailureRatio,
            SamplingDuration = resolved.SamplingDuration,
            MinimumThroughput = resolved.MinimumThroughput,
            BreakDuration = resolved.BreakDuration,
            ShouldHandle = new PredicateBuilder().Handle(shouldHandle),
            StateProvider = stateProvider,
            ManualControl = manualControl,
        };

        pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(strategyOptions)
            .Build();
    }

    /// <inheritdoc />
    public DashCircuitState State => stateProvider.CircuitState switch
    {
        CircuitState.Closed => DashCircuitState.Closed,
        CircuitState.Open => DashCircuitState.Open,
        CircuitState.HalfOpen => DashCircuitState.HalfOpen,
        CircuitState.Isolated => DashCircuitState.Isolated,
        _ => DashCircuitState.Closed,
    };

    /// <inheritdoc />
    public T Execute<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return pipeline.Execute(operation);
        }
        catch (BrokenCircuitException ex)
        {
            throw new CircuitBreakerOpenException("The SQLite circuit breaker is open.", ex);
        }
    }

    /// <inheritdoc />
    public void Execute(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            pipeline.Execute(operation);
        }
        catch (BrokenCircuitException ex)
        {
            throw new CircuitBreakerOpenException("The SQLite circuit breaker is open.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await pipeline.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            throw new CircuitBreakerOpenException("The SQLite circuit breaker is open.", ex);
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync(CancellationToken cancellationToken = default) =>
        await manualControl.CloseAsync(cancellationToken).ConfigureAwait(false);

    private static bool DefaultShouldHandle(Exception exception) =>
        exception is SqliteExecutionException or TimeoutException or IOException;
}

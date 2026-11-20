using Xunit;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;

namespace KyberDash.Data.Tests;

/// <summary>
/// Polly v8 circuit-breaker behaviour for <see cref="SqliteCircuitBreaker"/> and the way
/// <see cref="SqliteRelationalStore"/> hands every database call to it.
/// </summary>
public class CircuitBreakerTests : IDisposable
{
    private static readonly TimeSpan BreakDuration = TimeSpan.FromMilliseconds(600);

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Breaker_StartsClosedAndPassesResultsThrough()
    {
        SqliteCircuitBreaker breaker = NewBreaker();

        Assert.Equal(DashCircuitState.Closed, breaker.State);
        Assert.Equal(42, breaker.Execute(() => 42));
        int captured = 0;
        breaker.Execute(() => captured = 7);
        Assert.Equal(7, captured);
    }

    [Fact]
    public void Breaker_OpensOnceMinimumThroughputFailuresAreReached()
    {
        SqliteCircuitBreaker breaker = NewBreaker();
        int calls = 0;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            Assert.Throws<SqliteExecutionException>(() => breaker.Execute<int>(() =>
            {
                calls++;
                throw new SqliteExecutionException("sqlite3 exited with code 1");
            }));
            Assert.Equal(DashCircuitState.Closed, breaker.State);
        }

        Assert.Throws<SqliteExecutionException>(() => breaker.Execute<int>(() =>
        {
            calls++;
            throw new SqliteExecutionException("sqlite3 exited with code 1");
        }));

        Assert.Equal(DashCircuitState.Open, breaker.State);
        Assert.Equal(5, calls);
    }

    [Fact]
    public void Breaker_OpenStateFastFailsWithoutInvokingTheOperation()
    {
        SqliteCircuitBreaker breaker = NewBreaker();
        Trip(breaker);

        int invocations = 0;
        Assert.Throws<CircuitBreakerOpenException>(() => breaker.Execute<int>(() =>
        {
            invocations++;
            return 1;
        }));
        Assert.Equal(0, invocations);
    }

    [Fact]
    public async Task Breaker_AsyncOperationsFastFailWhileOpen()
    {
        SqliteCircuitBreaker breaker = NewBreaker();
        Trip(breaker);

        int invocations = 0;
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(() =>
            breaker.ExecuteAsync(static _ => ValueTask.FromResult(1)));

        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
        {
            invocations++;
            await breaker.ExecuteAsync<int>(_ => ValueTask.FromResult(1), CancellationToken.None);
        });
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void Breaker_ServesTheProbeAfterBreakDurationAndClosesOnSuccess()
    {
        SqliteCircuitBreaker breaker = NewBreaker();
        Trip(breaker);
        Assert.Equal(DashCircuitState.Open, breaker.State);

        Thread.Sleep(BreakDuration + TimeSpan.FromMilliseconds(400));

        Assert.Equal(42, breaker.Execute(() => 42));
        Assert.Equal(DashCircuitState.Closed, breaker.State);
    }

    [Fact]
    public void Breaker_ReopensWhenTheProbeAfterBreakDurationFails()
    {
        SqliteCircuitBreaker breaker = NewBreaker();
        Trip(breaker);

        Thread.Sleep(BreakDuration + TimeSpan.FromMilliseconds(400));
        Assert.Throws<SqliteExecutionException>(() => breaker.Execute<int>(() => throw new SqliteExecutionException("still broken")));

        int invocations = 0;
        Assert.Throws<CircuitBreakerOpenException>(() => breaker.Execute<int>(() =>
        {
            invocations++;
            return 1;
        }));
        Assert.Equal(0, invocations);
    }

    [Fact]
    public void Breaker_ResetClosesAnOpenCircuitImmediately()
    {
        SqliteCircuitBreaker breaker = NewBreaker();
        Trip(breaker);
        Assert.Equal(DashCircuitState.Open, breaker.State);

        breaker.Reset();

        Assert.Equal(DashCircuitState.Closed, breaker.State);
        Assert.Equal(7, breaker.Execute(() => 7));
    }

    [Fact]
    public void Store_TripsWiredBreakerIntoOpenCircuitWhenSqliteFailsRepeatedly()
    {
        SqliteCircuitBreaker breaker = new(new DashCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = BreakDuration,
        });
        SqliteRelationalStore store = new(Path.Combine(directory.Path, "tripping.db"), breaker);
        int failures = 0;

        for (int attempt = 0; attempt < 10 && breaker.State != DashCircuitState.Open; attempt++)
        {
            // Removing the database file leaves the store initialized but its tables gone, so the
            // next write lands in a freshly created empty file and sqlite3 exits non-zero.
            RemoveDatabase(store);
            try
            {
                store.SetWriteMarker(attempt + 1);
            }
            catch (SqliteExecutionException)
            {
                failures++;
            }
        }

        Assert.Equal(DashCircuitState.Open, breaker.State);
        Assert.InRange(failures, 1, 8);

        RemoveDatabase(store);
        Assert.Throws<CircuitBreakerOpenException>(() => store.SetWriteMarker(99));
    }

    [Fact]
    public void Store_ReportsCircuitBreakerStateWithoutCallingSqlite()
    {
        SqliteCircuitBreaker breaker = NewBreaker();
        SqliteRelationalStore store = new(Path.Combine(directory.Path, "healthy.db"), breaker);
        Trip(breaker);

        RemoveDatabase(store);

        Assert.Throws<CircuitBreakerOpenException>(() => store.GetStoreState());
        Assert.Throws<CircuitBreakerOpenException>(() => store.CountRecords());
        Assert.Equal(DashCircuitState.Open, breaker.State);
    }

    [Fact]
    public void Breaker_DoesNotOpenForExceptionsItDoesNotHandle()
    {
        SqliteCircuitBreaker breaker = NewBreaker();

        for (int attempt = 0; attempt < 6; attempt++)
            Assert.Throws<ArgumentException>(() => breaker.Execute<int>(static () => throw new ArgumentException("not a sqlite failure")));

        Assert.Equal(DashCircuitState.Closed, breaker.State);
    }

    private static SqliteCircuitBreaker NewBreaker() =>
        new(new DashCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = BreakDuration,
        });

    private static void Trip(SqliteCircuitBreaker breaker)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                breaker.Execute<int>(static () => throw new SqliteExecutionException("sqlite3 exited with code 1"));
            }
            catch (SqliteExecutionException)
            {
                // Expected: each failure feeds the throughput window.
            }
            catch (CircuitBreakerOpenException)
            {
                // The circuit opened before the fifth failure reached the pipeline.
                break;
            }
        }

        Assert.Equal(DashCircuitState.Open, breaker.State);
    }

    /// <summary>Removes the store's database and sidecars, so the next write fails on the new file.</summary>
    private static void RemoveDatabase(SqliteRelationalStore store)
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            string path = store.DatabasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}

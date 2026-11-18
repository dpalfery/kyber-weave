using Microsoft.Extensions.Time.Testing;

namespace KyberWeave.Tests.Fakes;

/// <summary>A fake clock that tells the test each time something starts waiting on it.</summary>
public sealed class ParkObservingTimeProvider : FakeTimeProvider
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _parks = new(0);

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        _ = _parks.Release();
        return timer;
    }

    /// <summary>Completes when the next wait on this clock begins.</summary>
    public Task NextPark() => _parks.WaitAsync();

    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own and moves the clock by <paramref name="step"/> only while the
    /// work is parked in a delay, so the order of the work's steps and the clock's never depends on how fast either
    /// thread runs. <paramref name="atPark"/> runs at the park numbered <paramref name="parkNumber"/>, before the clock
    /// moves past it; pass <see cref="int.MaxValue"/> to never run it. The real-time bound only guards against a hang.
    /// </summary>
    public T RunAdvancingOnParks<T>(Func<T> work, TimeSpan step, int parkNumber, Action? atPark)
    {
        Task<T> task = Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        int parks = 0;
        while (true)
        {
            Task finished = Task.WhenAny(NextPark(), task).WaitAsync(HangGuard).GetAwaiter().GetResult();
            if (ReferenceEquals(finished, task))
            {
                break;
            }

            if (++parks == parkNumber)
            {
                atPark?.Invoke();
            }

            Advance(step);
        }

        return task.GetAwaiter().GetResult();
    }
}

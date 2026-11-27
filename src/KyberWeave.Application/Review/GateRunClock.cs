namespace KyberWeave.Application.Review;

/// <summary>A moment a gate or the whole run started, measured on the clock a process timeout uses.</summary>
/// <remarks>
/// A process timeout runs on continuous time, which keeps counting while the machine sleeps. The monotonic
/// timestamp behind <see cref="TimeProvider.GetElapsedTime(long)"/> does not, so mixing the two records a gate that
/// its own 300-second limit stopped as 47 seconds: the run-06 report read "timed out after 300 seconds" beside
/// a 46.9-second duration after a 272-second clamshell sleep. Every duration, budget and deadline a gate run
/// reports comes from this one clock, so a result can never name a limit it did not take that long to reach.
/// </remarks>
/// <param name="Started">The wall-clock instant the gate or run started.</param>
internal readonly record struct GateRunClock(DateTimeOffset Started)
{
    /// <summary>The current instant, on the clock a process timeout uses.</summary>
    public static GateRunClock Now(TimeProvider clock) => new(clock.GetUtcNow());

    /// <summary>The time since the start, including time the machine spent asleep.</summary>
    public TimeSpan Elapsed(TimeProvider clock) => clock.GetUtcNow() - Started;
}

namespace KyberWeave.Testing.Squad;

/// <summary>
/// Decides which crash-safety failures a known weakness explains. A weakness names one fault point
/// and one symptom, so a different symptom at the same point stays unknown instead of hiding behind
/// the entry, and several symptoms at one point are each explained.
/// </summary>
public static class SquadCrashFailureClassifier
{
    /// <summary>Whether one failure is the symptom a weakness records.</summary>
    /// <param name="weakness">The recorded weakness.</param>
    /// <param name="flow">The flow that failed.</param>
    /// <param name="faultPoint">The point that stopped the flow.</param>
    /// <param name="detail">What went wrong at that point.</param>
    /// <returns>True when the flow, the point and the detail all match.</returns>
    public static bool Explains(
        SquadCrashKnownWeakness weakness,
        SquadCrashFlow flow,
        string faultPoint,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(weakness);
        return weakness.Flow == flow &&
               string.Equals(weakness.FaultPoint, faultPoint, StringComparison.Ordinal) &&
               detail.Contains(weakness.DetailContains, StringComparison.Ordinal);
    }

    /// <summary>The failures no recorded weakness explains.</summary>
    /// <param name="failures">Every failure from one run.</param>
    /// <param name="weaknesses">The recorded weaknesses.</param>
    /// <returns>The unexplained failures, in the order given.</returns>
    public static IReadOnlyList<SquadCrashFailure> Unexplained(
        IEnumerable<SquadCrashFailure> failures,
        IReadOnlyList<SquadCrashKnownWeakness> weaknesses)
    {
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(weaknesses);
        return failures
            .Where(failure => !weaknesses.Any(weakness =>
                Explains(weakness, failure.Flow, failure.FaultPoint, failure.Detail)))
            .ToList();
    }

    /// <summary>The recorded weaknesses that explain at least one failure.</summary>
    /// <param name="failures">Every failure from one run.</param>
    /// <param name="weaknesses">The recorded weaknesses.</param>
    /// <returns>The matched weaknesses, each once, in the order given.</returns>
    public static IReadOnlyList<SquadCrashKnownWeakness> Matched(
        IEnumerable<SquadCrashFailure> failures,
        IReadOnlyList<SquadCrashKnownWeakness> weaknesses)
    {
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(weaknesses);
        List<SquadCrashFailure> list = failures.ToList();
        return weaknesses
            .Where(weakness => list.Any(failure =>
                Explains(weakness, failure.Flow, failure.FaultPoint, failure.Detail)))
            .Distinct()
            .ToList();
    }
}

/// <summary>One failure in a crash-safety run: where it happened and what went wrong.</summary>
/// <param name="Flow">The flow that failed.</param>
/// <param name="FaultPoint">The point that stopped the flow.</param>
/// <param name="Detail">What went wrong at that point.</param>
public sealed record SquadCrashFailure(SquadCrashFlow Flow, string FaultPoint, string Detail);

/// <summary>One fault point whose behaviour is a known crash-safety weakness.</summary>
/// <param name="Flow">The flow that fails.</param>
/// <param name="FaultPoint">The point whose recovery is wrong, as the fixture names it.</param>
/// <param name="DetailContains">The symptom this weakness records; another symptom at the same point stays unknown.</param>
/// <param name="Reason">One line saying what goes wrong and why.</param>
public sealed record SquadCrashKnownWeakness(
    SquadCrashFlow Flow,
    string FaultPoint,
    string DetailContains,
    string Reason);

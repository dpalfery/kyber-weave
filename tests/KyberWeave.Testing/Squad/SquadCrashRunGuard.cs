namespace KyberWeave.Testing.Squad;

/// <summary>
/// Guards that stop a crash-safety run from passing while executing nothing: a flow that reports no
/// fault point, or a clean run that changes no file, proves nothing about crash safety.
/// </summary>
public static class SquadCrashRunGuard
{
    /// <summary>Refuses a flow that reported no fault point.</summary>
    /// <param name="flow">The flow under test.</param>
    /// <param name="faultPoints">The points the flow reported.</param>
    /// <exception cref="InvalidOperationException">The flow reported no fault point.</exception>
    public static void RequireFaultPoints(SquadCrashFlow flow, IReadOnlyList<string> faultPoints)
    {
        ArgumentNullException.ThrowIfNull(faultPoints);
        if (faultPoints.Count == 0)
        {
            throw new InvalidOperationException(
                $"{flow} reported no fault point, so the run would prove nothing. The flow must report one per crash state it reaches.");
        }
    }

    /// <summary>Refuses a clean run that left the tree unchanged, because every case would repeat the old tree.</summary>
    /// <param name="flow">The flow under test.</param>
    /// <param name="difference">What the clean run changed, or null when it changed nothing.</param>
    /// <exception cref="InvalidOperationException">The clean run changed nothing.</exception>
    public static void RequireCleanRunChangedTheTree(SquadCrashFlow flow, string? difference)
    {
        if (difference is null)
        {
            throw new InvalidOperationException(
                $"The clean {flow} flow left the tree unchanged, so an interrupted run could always pass as the old tree.");
        }
    }
}

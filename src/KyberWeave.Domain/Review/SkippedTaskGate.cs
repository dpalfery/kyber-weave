namespace KyberWeave.Core.Review;

/// <summary>A task gate that does not run for this change, and why.</summary>
/// <param name="Id">The gate's slug.</param>
/// <param name="Reason">A sentence the operator can act on.</param>
public sealed record SkippedTaskGate(string Id, string Reason);

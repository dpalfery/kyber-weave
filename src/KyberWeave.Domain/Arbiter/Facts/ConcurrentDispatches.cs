namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>The writer dispatches that overlapped one dispatch's window.</summary>
/// <param name="Paths">Their declared task files.</param>
/// <param name="AnyWithoutFiles">Whether any of them declared no task files, so what it wrote is unknown.</param>
public sealed record ConcurrentDispatches(IReadOnlyList<string> Paths, bool AnyWithoutFiles);

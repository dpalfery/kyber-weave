namespace KyberWeave.Core.Review;

/// <summary>A path an admitted gate requires that does not exist, and how to provide it.</summary>
/// <param name="GateId">The gate that declared the path under <c>requires-paths</c>.</param>
/// <param name="Path">The repository-relative path as the gate declared it.</param>
/// <param name="Fix">The action that provides the path, as a plain sentence.</param>
public sealed record MissingGateRequirement(string GateId, string Path, string Fix);

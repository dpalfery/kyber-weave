namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Canonical physical identity shared by planning, state binding, and transactions.</summary>
/// <param name="PhysicalPath">The canonical physical path of the deployment root.</param>
/// <param name="Key">The registry key derived from the physical path.</param>
public sealed record SquadPhysicalRootIdentity(string PhysicalPath, string Key);

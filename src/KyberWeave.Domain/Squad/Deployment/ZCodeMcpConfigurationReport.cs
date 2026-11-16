namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The outcome of checking a ZCode installation for the MCP servers Squad requires.</summary>
/// <param name="ConfiguredServers">Server names declared across every ZCode config tier.</param>
/// <param name="MissingServers">Required servers no tier declares, in stable order.</param>
/// <param name="InspectedPaths">Config paths that existed and were read, in precedence order.</param>
public sealed record ZCodeMcpConfigurationReport(
    IReadOnlyList<string> ConfiguredServers,
    IReadOnlyList<string> MissingServers,
    IReadOnlyList<string> InspectedPaths);

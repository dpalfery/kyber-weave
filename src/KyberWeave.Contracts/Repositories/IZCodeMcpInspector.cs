using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the MCP servers a ZCode installation declares.</summary>
public interface IZCodeMcpInspector
{
    /// <summary>Reports which required MCP servers a ZCode installation declares and which it does not.</summary>
    /// <param name="requiredServers">Server names the canonical toolchain declares.</param>
    /// <param name="zcodeStorageRoot">The resolved ZCode storage root.</param>
    /// <param name="workingDirectory">The directory a ZCode session would start in.</param>
    /// <returns>The configured and missing servers.</returns>
    ZCodeMcpConfigurationReport Inspect(
        IReadOnlyCollection<string> requiredServers,
        string zcodeStorageRoot,
        string workingDirectory);
}

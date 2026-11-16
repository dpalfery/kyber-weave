using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Reads a ZCode installation's config tiers through <see cref="ZCodeMcpConfiguration"/>.</summary>
public sealed class FileZCodeMcpInspector : IZCodeMcpInspector
{
    /// <inheritdoc />
    public ZCodeMcpConfigurationReport Inspect(
        IReadOnlyCollection<string> requiredServers,
        string zcodeStorageRoot,
        string workingDirectory,
        Func<string, string?> readFileText) =>
        ZCodeMcpConfiguration.Inspect(requiredServers, zcodeStorageRoot, workingDirectory, readFileText);
}

using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using YamlDotNet.Core;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>
/// Reads the MCP declaration through <see cref="KyberWeaveConfigLoader"/>. It stays in the legacy engine until the host
/// configuration loader moves behind a port of its own.
/// </summary>
public sealed class McpDeclarationReader : IMcpDeclarationReader
{
    /// <inheritdoc />
    public McpDeclarationLoad TryLoad(string repositoryRoot)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(repositoryRoot);
        return loaded.Success && loaded.Config is not null
            ? new McpDeclarationLoad(true, loaded.Config.Mcp, null)
            : new McpDeclarationLoad(false, null, loaded.Error);
    }

    /// <inheritdoc />
    public McpConfig Parse(string yaml)
    {
        try
        {
            return KyberWeaveConfigLoader.LoadFromYaml(yaml).Mcp;
        }
        catch (YamlException exception)
        {
            throw new FormatException(exception.Message, exception);
        }
    }
}

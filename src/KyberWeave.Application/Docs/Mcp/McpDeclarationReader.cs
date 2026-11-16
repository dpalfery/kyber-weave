using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using YamlDotNet.Core;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>Reads the MCP declaration through the configuration repository the container provides.</summary>
public sealed class McpDeclarationReader : IMcpDeclarationReader
{
    private readonly IKyberWeaveConfigRepository _config;

    /// <summary>Creates the reader over the host configuration repository.</summary>
    /// <param name="config">Loads a repository's configuration, and the declaration inside it.</param>
    public McpDeclarationReader(IKyberWeaveConfigRepository config) =>
        _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <inheritdoc />
    public McpDeclarationLoad TryLoad(string repositoryRoot)
    {
        KyberWeaveConfigLoadResult loaded = _config.TryLoad(repositoryRoot);
        return loaded.Success && loaded.Config is not null
            ? new McpDeclarationLoad(true, loaded.Config.Mcp, null)
            : new McpDeclarationLoad(false, null, loaded.Error);
    }

    /// <inheritdoc />
    public McpConfig Parse(string yaml)
    {
        try
        {
            return KyberWeaveConfigAssembler.FromYaml(yaml).Mcp;
        }
        catch (YamlException exception)
        {
            throw new FormatException(exception.Message, exception);
        }
    }
}

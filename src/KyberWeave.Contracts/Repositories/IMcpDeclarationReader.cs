using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the MCP declaration (<c>mcp.required-servers</c>) out of the repository's host configuration.</summary>
public interface IMcpDeclarationReader
{
    /// <summary>Loads the declaration from the repository's host configuration file.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    McpDeclarationLoad TryLoad(string repositoryRoot);

    /// <summary>Parses an in-memory host configuration document and returns its declaration.</summary>
    /// <param name="yaml">The document text.</param>
    /// <exception cref="ArgumentException">The document is not a valid host configuration.</exception>
    /// <exception cref="InvalidOperationException">The document is not a valid host configuration.</exception>
    /// <exception cref="FormatException">The document is not valid YAML.</exception>
    McpConfig Parse(string yaml);
}

/// <summary>The outcome of loading the MCP declaration from a repository.</summary>
/// <param name="Success">Whether the configuration loaded.</param>
/// <param name="Mcp">The declaration, when it loaded.</param>
/// <param name="Error">Why it did not load, when it did not.</param>
public sealed record McpDeclarationLoad(bool Success, McpConfig? Mcp, string? Error);

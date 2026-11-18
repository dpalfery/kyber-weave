using System.Reflection;
using Xunit;

namespace KyberWeave.Mcp.Tests;

/// <summary>
/// Verifies version retrieval from the MCP assembly's version attributes.
/// </summary>
public class McpAssemblyVersionAttributeTests
{
    [Fact]
    public void McpAssemblyVersionRetrievalReturnsConfiguredVersion()
    {
        // Arrange
        Assembly assembly = typeof(DocsTools).Assembly;

        // Act
        string version = GetVersionFromAssembly(assembly);

        // Assert
        Assert.NotNull(version);
        Assert.NotEmpty(version);
        Assert.StartsWith("0.1.0", version, StringComparison.Ordinal);
    }

    private static string GetVersionFromAssembly(Assembly? assembly)
    {
        if (assembly is null)
        {
            return "0.0.0";
        }

        string? infoVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return infoVersion;
        }

        Version? nameVersion = assembly.GetName().Version;
        if (nameVersion is not null)
        {
            return nameVersion.ToString();
        }

        return "0.0.0";
    }
}

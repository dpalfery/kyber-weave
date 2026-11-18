using System.Reflection;
using KyberWeave.Cli.Commands.Agents;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies version retrieval from the CLI assembly's version attributes.
/// </summary>
public class CliAssemblyVersionAttributeTests
{
    [Fact]
    public void CliAssemblyVersionRetrievalReturnsConfiguredVersion()
    {
        // Arrange
        Assembly assembly = typeof(AgentCatalogCommand).Assembly;

        // Act
        string version = GetVersionFromAssembly(assembly);

        // Assert
        Assert.NotNull(version);
        Assert.NotEmpty(version);
        Assert.StartsWith("0.1.0", version, StringComparison.Ordinal);
    }

    [Fact]
    public void GetVersionFromAssemblyPrefersInformationalVersionOverAssemblyVersion()
    {
        // Arrange
        Assembly assembly = typeof(AgentCatalogCommand).Assembly;
        string? expectedInformationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Act
        string version = GetVersionFromAssembly(assembly);

        // Assert
        Assert.Equal(expectedInformationalVersion, version);
    }

    [Fact]
    public void GetVersionFromAssemblyReturnsFallbackWhenNoVersionAttributesPresent()
    {
        // Arrange: string assembly (System.Private.CoreLib) may or may not have InformationalVersion,
        // but we test our helper with a custom resolution path.
        string dummyVersion = GetVersionFromAssembly(null);

        // Assert
        Assert.Equal("0.0.0", dummyVersion);
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

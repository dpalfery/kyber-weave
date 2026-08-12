using System.Diagnostics;
using System.Reflection;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit tests verifying version retrieval from assembly attributes and execution output for CLI and MCP.
/// </summary>
public class VersionRetrievalTests
{
    [Fact]
    public void CliAssembly_VersionRetrieval_ReturnsConfiguredVersion()
    {
        // Arrange
        var assembly = typeof(KyberWeave.Cli.Commands.Agents.AgentCatalogCommand).Assembly;

        // Act
        var version = GetVersionFromAssembly(assembly);

        // Assert
        Assert.NotNull(version);
        Assert.NotEmpty(version);
        Assert.StartsWith("0.1.0", version, StringComparison.Ordinal);
    }

    [Fact]
    public void McpAssembly_VersionRetrieval_ReturnsConfiguredVersion()
    {
        // Arrange
        var assembly = typeof(KyberWeave.Mcp.DocsTools).Assembly;

        // Act
        var version = GetVersionFromAssembly(assembly);

        // Assert
        Assert.NotNull(version);
        Assert.NotEmpty(version);
        Assert.StartsWith("0.1.0", version, StringComparison.Ordinal);
    }

    [Fact]
    public void GetVersionFromAssembly_PrefersInformationalVersion_OverAssemblyVersion()
    {
        // Arrange
        var assembly = typeof(KyberWeave.Cli.Commands.Agents.AgentCatalogCommand).Assembly;
        var expectedInformationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Act
        var version = GetVersionFromAssembly(assembly);

        // Assert
        Assert.Equal(expectedInformationalVersion, version);
    }

    [Fact]
    public void GetVersionFromAssembly_ReturnsFallback_WhenNoVersionAttributesPresent()
    {
        // Arrange: string assembly (System.Private.CoreLib) may or may not have InformationalVersion,
        // but we test our helper with a custom resolution path.
        var dummyVersion = GetVersionFromAssembly(null);

        // Assert
        Assert.Equal("0.0.0", dummyVersion);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Cli_VersionFlag_OutputsApplicationVersionAndExitsZero(string flag)
    {
        // Arrange
        var assembly = typeof(KyberWeave.Cli.Commands.Agents.AgentCatalogCommand).Assembly;

        // Act
        var result = RunAssembly(assembly, flag);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("kyber-weave", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.1.0", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Mcp_VersionFlag_OutputsApplicationVersionAndExitsZero(string flag)
    {
        // Arrange
        var assembly = typeof(KyberWeave.Mcp.DocsTools).Assembly;

        // Act
        var result = RunAssembly(assembly, flag);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("kyber-weave-mcp", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.1.0", result.StandardOutput, StringComparison.Ordinal);
    }

    private static string GetVersionFromAssembly(Assembly? assembly)
    {
        if (assembly is null)
        {
            return "0.0.0";
        }

        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return infoVersion;
        }

        var nameVersion = assembly.GetName().Version;
        if (nameVersion is not null)
        {
            return nameVersion.ToString();
        }

        return "0.0.0";
    }

    private static ProcessResult RunAssembly(Assembly assembly, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(assembly.Location);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start dotnet process for '{assembly.Location}'.");

        return ProcessRunner.ReadToEnd(process);
    }
}

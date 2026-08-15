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
    public void CliAssemblyVersionRetrievalReturnsConfiguredVersion()
    {
        // Arrange
        Assembly assembly = typeof(KyberWeave.Cli.Commands.Agents.AgentCatalogCommand).Assembly;

        // Act
        string version = GetVersionFromAssembly(assembly);

        // Assert
        Assert.NotNull(version);
        Assert.NotEmpty(version);
        Assert.StartsWith("0.1.0", version, StringComparison.Ordinal);
    }

    [Fact]
    public void McpAssemblyVersionRetrievalReturnsConfiguredVersion()
    {
        // Arrange
        Assembly assembly = typeof(KyberWeave.Mcp.DocsTools).Assembly;

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
        Assembly assembly = typeof(KyberWeave.Cli.Commands.Agents.AgentCatalogCommand).Assembly;
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

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void CliVersionFlagOutputsApplicationVersionAndExitsZero(string flag)
    {
        // Arrange
        Assembly assembly = typeof(KyberWeave.Cli.Commands.Agents.AgentCatalogCommand).Assembly;

        // Act
        ProcessResult result = RunAssembly(assembly, flag);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("kyber-weave", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.1.0", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void McpVersionFlagOutputsApplicationVersionAndExitsZero(string flag)
    {
        // Arrange
        Assembly assembly = typeof(KyberWeave.Mcp.DocsTools).Assembly;

        // Act
        ProcessResult result = RunAssembly(assembly, flag);

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

        string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
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

    private static ProcessResult RunAssembly(Assembly assembly, params string[] args)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(assembly.Location);
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start dotnet process for '{assembly.Location}'.");

        return ProcessRunner.ReadToEnd(process);
    }
}

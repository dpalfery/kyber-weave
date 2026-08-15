using System.Diagnostics;
using System.Reflection;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies version retrieval from application execution output for CLI and MCP.
/// </summary>
public class VersionRetrievalTests
{
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

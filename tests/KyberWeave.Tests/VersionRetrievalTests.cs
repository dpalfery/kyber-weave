using System.Diagnostics;
using System.Reflection;
using KyberWeave.Cli.Commands.Agents;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies version retrieval from application execution output for CLI and MCP.
/// </summary>
public class VersionRetrievalTests
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

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void CliVersionFlagOutputsApplicationVersionAndExitsZero(string flag)
    {
        // Arrange
        Assembly assembly = typeof(AgentCatalogCommand).Assembly;

        // Act
        ProcessResult result = RunAssembly(assembly, flag);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("kyber-weave", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.1.0", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the exact line the CLI prints for its version flags, which is the line the install
    /// runbook and distribution.md document. A `+sha` build suffix reaching that line would be a
    /// user-visible regression that the looser assertions above cannot see.
    /// <remarks>
    /// Scope: this runs <c>dotnet &lt;dll&gt; --version</c>, so it pins the assembly's version-line formatting
    /// (Spectre SetApplicationVersion and the <c>+</c> stripping in GetVersion), not the published single-file binary.
    /// That binary is pinned by scripts/update-loop.sh, which asserts <c>kyber-weave --version</c> and
    /// <c>kyber-weave-mcp --version</c> on the release-local.sh single-file publish. CI runs it in the
    /// update-loop job of .github/workflows/ci.yml.
    /// </remarks>
    /// </summary>
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void CliVersionFlagPrintsExactlyNameAndVersionWithoutBuildMetadata(string flag)
    {
        // Arrange
        Assembly assembly = typeof(AgentCatalogCommand).Assembly;
        string informationalVersion = assembly
                                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                          ?.InformationalVersion
                                      ?? throw new InvalidOperationException(
                                          "The CLI assembly has no AssemblyInformationalVersion.");
        int plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        string expectedVersion = plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;

        // Act
        ProcessResult result = RunAssembly(assembly, flag);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"kyber-weave {expectedVersion}", result.StandardOutput.Trim());
        Assert.DoesNotContain("+", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void McpVersionFlagOutputsApplicationVersionAndExitsZero(string flag)
    {
        // Arrange
        Assembly assembly = typeof(DocsTools).Assembly;

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

    private static ProcessResult RunAssembly(Assembly assembly, params string[] args)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(assembly.Location);
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Bounded: the product CLI answers --version and exits; the timeout kills it if it does not.
        return ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(30));
    }
}

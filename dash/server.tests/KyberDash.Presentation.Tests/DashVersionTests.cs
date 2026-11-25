using System.Reflection;
using System.Reflection.Emit;
using KyberDash.Api;
using KyberDash.Contracts.Services;
using KyberDash.Core;
using Xunit;
using ApiProgram = KyberDash.Api.Program;
using CliProgram = KyberDash.Cli.Program;
using McpProgram = KyberDash.Mcp.Program;

namespace KyberDash.Presentation.Tests;

/// <summary>Proves every KyberDash surface reports the one version <see cref="DashVersion.Current"/> resolves.</summary>
public class DashVersionTests
{
    [Fact]
    public void CurrentIsTheCoreInformationalVersionWithoutMetadata()
    {
        string informational = typeof(DashVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        string expected = plus >= 0 ? informational[..plus] : informational;

        Assert.Equal(expected, DashVersion.Current);
    }

    [Fact]
    public void AnAssemblyWithoutAnInformationalVersionThrowsAnOrdinaryInvalidOperationException()
    {
        AssemblyBuilder bare = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BareAssembly"), AssemblyBuilderAccess.Run);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => DashVersion.FromAssembly(bare));

        Assert.Contains("BareAssembly", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromAssemblyStripsTheMetadataSuffix()
    {
        AssemblyBuilder stamped = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("StampedAssembly"),
            AssemblyBuilderAccess.Run,
            [new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
                ["1.2.3+abc123"])]);

        Assert.Equal("1.2.3", DashVersion.FromAssembly(stamped));
    }

    [Fact]
    public void CurrentCarriesNoMetadataSuffix()
    {
        Assert.DoesNotContain('+', DashVersion.Current);
    }

    [Fact]
    public void EveryBinaryEntryPointReportsTheCurrentVersion()
    {
        Assert.Equal(DashVersion.Current, ApiProgram.ResolveVersion());
        Assert.Equal(DashVersion.Current, CliProgram.ResolveVersion());
        Assert.Equal(DashVersion.Current, McpProgram.ResolveVersion());
    }

    [Fact]
    public void DefaultOptionsAndRuntimeSettingsReportTheCurrentVersion()
    {
        Assert.Equal(DashVersion.Current, new DashOptions().Version);
        Assert.Equal(DashVersion.Current, new DashRuntimeSettings().Version);
    }

    [Fact]
    public void CommandLineParseReportsTheCurrentVersionAndIgnoresTheEnvironmentOverride()
    {
        string? previous = Environment.GetEnvironmentVariable("KYBERDASH_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("KYBERDASH_VERSION", "9.9.9");

            DashRuntimeSettings settings = ParseNoArguments();

            Assert.Equal(DashVersion.Current, settings.Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KYBERDASH_VERSION", previous);
        }
    }

    /// <summary>
    /// Calls <c>DashCommandLine.Parse</c> with no arguments. The class is internal to KyberDash.Api and
    /// this project has no <c>InternalsVisibleTo</c> grant, so it is reached through reflection.
    /// </summary>
    private static DashRuntimeSettings ParseNoArguments()
    {
        Type commandLine = typeof(DashRuntimeSettings).Assembly
            .GetType("KyberDash.Api.DashCommandLine", throwOnError: true)!;
        MethodInfo parse = commandLine.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;
        object? result = parse.Invoke(null, new object[] { new List<string>() });
        return (DashRuntimeSettings)result!;
    }
}

using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Which environment variables a version probe hands its child, read from the process-environment seam.</summary>
public sealed class ProcessProbeEnvironmentTests
{
    private readonly FakeProcessExecutor _executor = new FakeProcessExecutor()
        .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");

    private static readonly Dictionary<string, string> Variables = new(StringComparer.Ordinal)
    {
        ["PATH"] = "/fake/bin",
        ["PATHEXT"] = ".EXE",
        ["SystemRoot"] = "C:/Windows",
        ["WINDIR"] = "C:/Windows",
        ["GH_TOKEN"] = "secret"
    };

    [Theory]
    [InlineData(OperatingSystemKind.Linux)]
    [InlineData(OperatingSystemKind.MacOS)]
    public async Task Probe_OnUnix_PassesOnlyPathFromTheEnvironment(OperatingSystemKind kind)
    {
        await new McpProcessProbe(_executor, Environment(kind)).ProbeAsync();

        Assert.Equal(
            new Dictionary<string, string?> { ["PATH"] = "/fake/bin" },
            Assert.Single(_executor.Calls).Environment.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    [Fact]
    public async Task Probe_OnWindows_AlsoPassesTheVariablesAToolNeedsToStart()
    {
        await new McpProcessProbe(_executor, Environment(OperatingSystemKind.Windows)).ProbeAsync();

        Assert.Equal(
            ["PATH", "PATHEXT", "SystemRoot", "WINDIR"],
            Assert.Single(_executor.Calls).Environment.Keys.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Probe_WithNoPath_StartsTheChildWithAnEmptyEnvironment()
    {
        await new McpProcessProbe(_executor, new FakeProcessEnvironment(operatingSystem: OperatingSystemKind.Linux)).ProbeAsync();

        Assert.Empty(Assert.Single(_executor.Calls).Environment);
    }

    [Fact]
    public async Task ArbiterProbe_ReadsPathFromTheEnvironmentToo()
    {
        FakeProcessExecutor executor = new FakeProcessExecutor().WithMatchingArbiterProbe();

        ToolProbeResult result = await new ArbiterProcessProbe(executor, Environment(OperatingSystemKind.Linux)).ProbeAsync();

        Assert.True(result.IsAvailable);
        Assert.Equal("/fake/bin", Assert.Single(executor.Calls).Environment["PATH"]);
    }

    private static FakeProcessEnvironment Environment(OperatingSystemKind kind) =>
        new(operatingSystem: kind, readVariable: name => Variables.GetValueOrDefault(name));
}

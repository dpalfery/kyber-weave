using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the contract for the EnvironmentReader and PreloadedSource seams designed in
/// Plan §2.1 &amp; §2.2 for .NET test suite optimization.
/// </summary>
/// <remarks>
/// These seams eliminate process environment mutation (<see cref="Environment.SetEnvironmentVariable(string, string?)" />)
/// and redundant canonical disk I/O across squad rendering tests, enabling thread-safe parallel test execution
/// and sub-second suite runs without altering the production render output.
/// </remarks>
public sealed class SquadRenderSeamContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    /// Verifies that SquadOperatorChannel.IsGateOn honors an injected environment reader delegate
    /// without reading or mutating process-level environment variables.
    /// </summary>
    [Fact]
    public void OperatorChannelGateHonorsInjectedEnvironmentReaderWithoutMutatingProcessEnvironment()
    {
        string? realGateValue = Environment.GetEnvironmentVariable(SquadOperatorChannel.GateVariable);
        Assert.Null(realGateValue);

        Func<string, string?> onReader = static key =>
            string.Equals(key, SquadOperatorChannel.GateVariable, StringComparison.Ordinal) ? "true" : null;
        Func<string, string?> offReader = static key =>
            string.Equals(key, SquadOperatorChannel.GateVariable, StringComparison.Ordinal) ? "false" : null;
        Func<string, string?> nullReader = static _ => null;

        Assert.True(SquadOperatorChannel.IsGateOn(onReader));
        Assert.False(SquadOperatorChannel.IsGateOn(offReader));
        Assert.False(SquadOperatorChannel.IsGateOn(nullReader));

        Assert.Null(Environment.GetEnvironmentVariable(SquadOperatorChannel.GateVariable));
    }

    /// <summary>
    /// Verifies that SquadOperatorChannel.ResolveServerName honors an injected environment reader delegate
    /// without reading or mutating process-level environment variables.
    /// </summary>
    [Fact]
    public void OperatorChannelResolveServerNameHonorsInjectedEnvironmentReaderWithoutMutatingProcessEnvironment()
    {
        string? realServerValue = Environment.GetEnvironmentVariable(SquadOperatorChannel.ServerVariable);
        Assert.Null(realServerValue);

        const string expectedServerName = "custom-operator-srv";
        Func<string, string?> validReader = static key =>
            string.Equals(key, SquadOperatorChannel.ServerVariable, StringComparison.Ordinal)
                ? expectedServerName
                : null;
        Func<string, string?> nullReader = static _ => null;
        Func<string, string?> invalidReader = static key =>
            string.Equals(key, SquadOperatorChannel.ServerVariable, StringComparison.Ordinal)
                ? "invalid server name with spaces!"
                : null;

        SquadOperatorChannel.ServerName valid = SquadOperatorChannel.ResolveServerName(validReader);
        Assert.Equal(expectedServerName, valid.Name);
        Assert.Null(valid.Warning);

        SquadOperatorChannel.ServerName missing = SquadOperatorChannel.ResolveServerName(nullReader);
        Assert.Null(missing.Name);
        Assert.NotNull(missing.Warning);
        Assert.Equal(SquadOperatorChannel.ServerNameMissingCode, missing.Warning.Code);

        SquadOperatorChannel.ServerName invalid = SquadOperatorChannel.ResolveServerName(invalidReader);
        Assert.Null(invalid.Name);
        Assert.NotNull(invalid.Warning);
        Assert.Equal(SquadOperatorChannel.ServerNameInvalidCode, invalid.Warning.Code);

        Assert.Null(Environment.GetEnvironmentVariable(SquadOperatorChannel.ServerVariable));
    }

    /// <summary>
    /// Verifies that SquadRendererRegistry honors SquadRenderRequest.EnvironmentReader:
    /// when the injected reader supplies the gate and server name while the process environment is unset,
    /// Copilot at global scope renders the operator channel overlay referencing the server and Claude emits
    /// the operator-channel-not-rendered degradation record.
    /// </summary>
    [Fact]
    public async Task RendererRegistryHonorsRenderRequestEnvironmentReaderForCopilotOverlayAndDegradations()
    {
        Assert.Null(Environment.GetEnvironmentVariable(SquadOperatorChannel.GateVariable));

        string copilotDir = Path.Combine(_temp.Path, "copilot-home");
        string claudeDir = Path.Combine(_temp.Path, "claude-home");
        Directory.CreateDirectory(copilotDir);
        Directory.CreateDirectory(claudeDir);

        const string serverName = "custom-operator-srv";
        string mcpConfig = "{\"mcpServers\":{\"" + serverName + "\":{\"command\":\"test\",\"args\":[]}}}";
        await File.WriteAllTextAsync(
            Path.Combine(copilotDir, CopilotMcpServerProbe.McpConfigFileName),
            mcpConfig,
            Encoding.UTF8);

        TestGlobalRootResolver globalRoots = new(copilotDir, claudeDir);
        Func<string, string?> onReader = static key => key switch
        {
            SquadOperatorChannel.GateVariable => "true",
            SquadOperatorChannel.ServerVariable => serverName,
            _ => null
        };

        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Copilot, SquadTarget.Claude],
            SquadDeploymentScope.Global,
            GlobalRoots: globalRoots,
            EnvironmentReader: onReader);

        SquadRendererRegistry registry = new([new CopilotRenderer(), new ClaudeRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile conductor = Assert.Single(
            result.Files,
            static file => file.RelativePath.EndsWith("conductor.agent.md", StringComparison.Ordinal));
        string conductorText = Encoding.UTF8.GetString(conductor.Content.Span);
        Assert.Contains(serverName, conductorText, StringComparison.Ordinal);

        SquadDegradationRecord degradation = Assert.Single(
            result.Degradations,
            static record => record.Code == SquadOperatorChannel.NotRenderedCode);
        Assert.Equal("claude", degradation.Target);
        Assert.Equal(SquadOperatorChannel.ConductorAgentName, degradation.CanonicalIdentity);
    }

    /// <summary>
    /// Verifies that when SquadRenderRequest.EnvironmentReader returns null, neither the Copilot conductor
    /// overlay nor the Claude degradation record is emitted.
    /// </summary>
    [Fact]
    public async Task RendererRegistryOmitsOverlayAndDegradationsWhenEnvironmentReaderReturnsNull()
    {
        Assert.Null(Environment.GetEnvironmentVariable(SquadOperatorChannel.GateVariable));

        string copilotDir = Path.Combine(_temp.Path, "copilot-home-off");
        string claudeDir = Path.Combine(_temp.Path, "claude-home-off");
        Directory.CreateDirectory(copilotDir);
        Directory.CreateDirectory(claudeDir);

        const string serverName = "custom-operator-srv";
        string mcpConfig = "{\"mcpServers\":{\"" + serverName + "\":{\"command\":\"test\",\"args\":[]}}}";
        await File.WriteAllTextAsync(
            Path.Combine(copilotDir, CopilotMcpServerProbe.McpConfigFileName),
            mcpConfig,
            Encoding.UTF8);

        TestGlobalRootResolver globalRoots = new(copilotDir, claudeDir);
        Func<string, string?> nullReader = static _ => null;

        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Copilot, SquadTarget.Claude],
            SquadDeploymentScope.Global,
            GlobalRoots: globalRoots,
            EnvironmentReader: nullReader);

        SquadRendererRegistry registry = new([new CopilotRenderer(), new ClaudeRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile conductor = Assert.Single(
            result.Files,
            static file => file.RelativePath.EndsWith("conductor.agent.md", StringComparison.Ordinal));
        string conductorText = Encoding.UTF8.GetString(conductor.Content.Span);
        Assert.DoesNotContain(serverName, conductorText, StringComparison.Ordinal);

        Assert.DoesNotContain(
            result.Degradations,
            static record => record.Code == SquadOperatorChannel.NotRenderedCode);
    }

    /// <summary>
    /// Verifies that SquadRenderSource.Load honors PreloadedSource on SquadRenderRequest
    /// without requiring or touching SourceDirectory on disk.
    /// </summary>
    [Fact]
    public void RenderSourceLoadHonorsPreloadedSourceWithoutTouchingSourceDirectoryOnDisk()
    {
        SquadSource preloadedSource = SquadSourceLoader.Load(ProductRoot);
        string nonExistentSourceDirectory = Path.Combine(
            Path.GetTempPath(),
            "kw-nonexistent-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(nonExistentSourceDirectory));

        SquadRenderRequest request = new(
            nonExistentSourceDirectory,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project,
            PreloadedSource: preloadedSource);

        SquadSource loaded = SquadRenderSource.Load(request, SquadTarget.Copilot);

        Assert.NotNull(loaded);
        Assert.Equal(preloadedSource.Manifest.Name, loaded.Manifest.Name);
        Assert.Equal(preloadedSource.Agents.Count, loaded.Agents.Count);
        Assert.Equal(preloadedSource.Skills.Count, loaded.Skills.Count);
        Assert.False(Directory.Exists(nonExistentSourceDirectory));
    }

    /// <summary>
    /// Verifies that SquadRenderSource.Load applies ModelPresets cleanly to the in-memory PreloadedSource
    /// without requiring or touching SourceDirectory on disk.
    /// </summary>
    [Fact]
    public void RenderSourceLoadWithPreloadedSourceAppliesModelPresetsCleanly()
    {
        SquadSource preloadedSource = SquadSourceLoader.Load(ProductRoot);
        string nonExistentSourceDirectory = Path.Combine(
            Path.GetTempPath(),
            "kw-nonexistent-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(nonExistentSourceDirectory));

        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    general:
                                      claude: claude-3-7-sonnet
                                  """;
        SquadModelPreset preset = SquadSourceLoader.ParseModelPreset(
            "seam-test-preset.yml",
            Encoding.UTF8.GetBytes(presetYaml));
        Dictionary<SquadTarget, SquadModelPreset> presets = new()
        {
            [SquadTarget.Copilot] = preset
        };

        SquadRenderRequest request = new(
            nonExistentSourceDirectory,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project,
            ModelPresets: presets,
            PreloadedSource: preloadedSource);

        SquadSource loaded = SquadRenderSource.Load(request, SquadTarget.Copilot);

        Assert.NotNull(loaded);
        Assert.True(loaded.ModelProfiles.Profiles.TryGetValue("general", out SquadModelProfile? generalProfile));
        Assert.NotNull(generalProfile);
        Assert.Equal("claude-3-7-sonnet", generalProfile.HarnessModels["claude"]);
        Assert.False(Directory.Exists(nonExistentSourceDirectory));
    }

    /// <summary>
    /// Verifies that SquadRendererRegistry.RenderAsync executes successfully with a PreloadedSource
    /// against a non-existent SourceDirectory path without touching the filesystem.
    /// </summary>
    [Fact]
    public async Task RendererRegistryRenderAsyncHonorsPreloadedSourceWithoutTouchingSourceDirectoryOnDisk()
    {
        SquadSource preloadedSource = SquadSourceLoader.Load(ProductRoot);
        string nonExistentSourceDirectory = Path.Combine(
            Path.GetTempPath(),
            "kw-nonexistent-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(nonExistentSourceDirectory));

        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    general:
                                      claude: claude-3-7-sonnet
                                  """;
        SquadModelPreset preset = SquadSourceLoader.ParseModelPreset(
            "seam-test-preset.yml",
            Encoding.UTF8.GetBytes(presetYaml));
        Dictionary<SquadTarget, SquadModelPreset> presets = new()
        {
            [SquadTarget.Copilot] = preset
        };

        SquadRenderRequest request = new(
            nonExistentSourceDirectory,
            [SquadTarget.Copilot, SquadTarget.Claude],
            SquadDeploymentScope.Project,
            ModelPresets: presets,
            PreloadedSource: preloadedSource);

        SquadRendererRegistry registry = new([new CopilotRenderer(), new ClaudeRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.NotEmpty(result.Files);
        Assert.False(Directory.Exists(nonExistentSourceDirectory));
    }

    private sealed class TestGlobalRootResolver(string copilotRoot, string claudeRoot) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target)
        {
            return target switch
            {
                SquadTarget.Copilot => copilotRoot,
                SquadTarget.Claude => claudeRoot,
                _ => throw new ArgumentOutOfRangeException(nameof(target), target,
                    "Unexpected target in seam contract test.")
            };
        }
    }
}

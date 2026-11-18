using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Warp has no hook primitive. Under a global-scope Arbiter install it stays advisory and
/// fallback-only, and the install must write an explicit Arbiter MCP entry into the target's own
/// <c>.warp/.mcp.json</c> without clobbering any foreign JSON the user already kept there.
/// </summary>
public sealed class ArbiterGlobalWarpFallbackTests
{
    private readonly InMemorySquadState _state = new();

    [Fact]
    public void TargetDegradations_GlobalScope_WarpUsesFallbackOnlyReason()
    {
        SquadDegradationRecord record = Assert.Single(
            ArbiterHookWiring.TargetDegradations([SquadTarget.Warp], SquadDeploymentScope.Global));

        Assert.Equal("arbiter-not-enforced", record.Code);
        Assert.Equal("fallback-only", record.Details);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GlobalWarpInstall_WritesNothingAndReportsMcpRegistration(bool withForeignConfig)
    {
        string home = _state.Layout.PhysicalOf(_state.Layout.Home);
        string warpRoot = Path.Combine(home, ".warp");
        _state.Files.CreateDirectory(warpRoot);

        string mcpConfigPath = Path.Combine(warpRoot, ".mcp.json");
        byte[] originalBytes = Encoding.UTF8.GetBytes(
            """
            {
              "mcpServers": {
                "foreign": {
                  "command": "other-mcp",
                  "args": ["--mode", "manual"]
                },
                "kyber-weave": {
                  "command": "kyber-weave-mcp",
                  "args": ["--repo-root", "/docs"]
                }
              }
            }
            """);

        if (withForeignConfig)
        {
            _state.Files.WriteAllBytes(mcpConfigPath, originalBytes);
        }

        using FakeSquadReleaseSource releaseSource = new() { Files = _state.Files };
        FakeSquadRenderer renderer = new();
        SquadInstallCommand command = SquadTestCommands.InMemoryInstall(
            _state,
            releaseSource,
            renderer,
            globalRoots: new FixedGlobalRoots(home),
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = home,
                Targets = ["warp"],
                Global = true,
                Yes = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("~/.warp/.mcp.json", execution.Output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter serve", execution.Output, StringComparison.Ordinal);

        if (withForeignConfig)
        {
            Assert.Equal(originalBytes, _state.Files.ReadAllBytes(mcpConfigPath));
        }
        else
        {
            Assert.False(_state.Files.FileExists(mcpConfigPath), $"global Warp install must not create {mcpConfigPath}");
        }
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed class FixedGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => target switch
        {
            SquadTarget.Warp => Path.Combine(root, ".warp"),
            _ => Path.Combine(root, ".other")
        };
    }
}

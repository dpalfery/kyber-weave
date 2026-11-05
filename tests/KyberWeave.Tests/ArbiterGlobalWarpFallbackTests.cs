using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Warp has no hook primitive. Under a global-scope Arbiter install it stays advisory and
/// fallback-only, and the install must write an explicit Arbiter MCP entry into the target's own
/// <c>.warp/.mcp.json</c> without clobbering any foreign JSON the user already kept there.
/// </summary>
public sealed class ArbiterGlobalWarpFallbackTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TargetDegradations_GlobalScope_WarpUsesFallbackOnlyReason()
    {
        SquadDegradationRecord record = Assert.Single(
            ArbiterHookWiring.TargetDegradations([SquadTarget.Warp], SquadDeploymentScope.Global));

        Assert.Equal("arbiter-not-enforced", record.Code);
        Assert.Equal("fallback-only", record.Details);
    }

    [Fact]
    public void Install_GlobalScope_WarpWritesOwnedArbiterServerEntryIntoWarpMcpConfig()
    {
        string home = Path.Combine(_temp.Path, "home");
        string warpRoot = Path.Combine(home, ".warp");
        Directory.CreateDirectory(warpRoot);

        string mcpConfigPath = Path.Combine(warpRoot, ".mcp.json");
        File.WriteAllText(
            mcpConfigPath,
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

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "app-data"));
        SquadStateStore stateStore = new(userPaths);
        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
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
        Assert.True(File.Exists(mcpConfigPath), $"global Warp install should create {mcpConfigPath}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(mcpConfigPath));
        JsonElement servers = document.RootElement.GetProperty("mcpServers");

        bool foundArbiterServer = false;
        foreach (JsonProperty property in servers.EnumerateObject())
        {
            if (property.Name.Equals("foreign", StringComparison.Ordinal)
                || property.Name.Equals("kyber-weave", StringComparison.Ordinal))
            {
                continue;
            }

            JsonElement value = property.Value;
            string? serverCommand = value.TryGetProperty("command", out JsonElement commandElement)
                ? commandElement.GetString()
                : null;
            if (!string.Equals(serverCommand, "kyber-weave", StringComparison.Ordinal))
            {
                continue;
            }

            string[] args = value.GetProperty("args").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
            Assert.Contains("arbiter", args, StringComparer.Ordinal);
            Assert.Contains("serve", args, StringComparer.Ordinal);
            Assert.Contains($"--repo-root", args, StringComparer.Ordinal);
            Assert.Contains(Path.Combine(home, ".warp"), args, StringComparer.Ordinal);
            foundArbiterServer = true;
            break;
        }

        Assert.True(foundArbiterServer, $"expected the Warp global install to create the Arbiter MCP server entry, but the file was: {File.ReadAllText(mcpConfigPath)}");
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        using StringWriter writer = new();
        TextWriter saved = Console.Out;
        Console.SetOut(writer);
        try
        {
            int result = execute();
            return new CommandExecution(result, writer.ToString());
        }
        finally
        {
            Console.SetOut(saved);
        }
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

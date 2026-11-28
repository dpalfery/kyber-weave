using System.Text.Json.Nodes;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Application.Update;

namespace KyberWeave.Application.Tests.Update;

/// <summary>
/// The arbiter hook refresh, run after an update, reports a leftover old MCP server key once per
/// project it refreshes, through the same log the refresh already writes to.
/// </summary>
/// <remarks>
/// The refresh re-renders hooks for every enabled project, so it is the one place an update reaches each
/// project in turn. A notice printed once for the whole run would not say which project holds the
/// leftover, and the operator would have to search every tree for it. The notice never changes a file:
/// the refresh rewrites hooks, and a rename belongs to <c>docs mcp-migrate</c>, which the operator runs.
/// </remarks>
public sealed class ArbiterHookRefresherRenameNoticeTests
{
    private const string WarningCode = "KW-MCP-WIRE-007";

    private readonly InMemoryMcpWiring _wiring = McpWiringTestServices.InMemory();
    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly FakeSquadStateStore _state = new(new FakeSquadUserPaths("/kw/home/state"));

    private ArbiterHookRefresher Refresher(List<string> log) => new(
        new FakeProcessEnvironment(homeDirectory: "/kw/home", readVariable: _ => null),
        new ArbiterHookRefresherTests.NoUserConfig(),
        _configs,
        new FakeSquadLifecycleService(_state),
        _state,
        _wiring.Files,
        _wiring.Wiring,
        log.Add);

    private string ProjectRoot(string name) => Path.Combine(_wiring.Root, name);

    /// <summary>
    /// Each enabled project that holds a proven old key gets its own log line naming the rule id and that
    /// project's root. A project with no leftover gets no such line.
    /// </summary>
    [Fact]
    public async Task TheRefresherLogsTheLeftoverOncePerProjectThatHoldsIt()
    {
        string withLeftover = ProjectRoot("project-with-leftover");
        string clean = ProjectRoot("project-clean");
        ArrangeEnabledProject(withLeftover);
        ArrangeEnabledProject(clean);
        WriteProvenOldKey(withLeftover);
        List<string> log = [];

        ArbiterHookRefresher refresher = Refresher(log);
        await refresher.RefreshAsync([withLeftover, clean]);

        string[] notices = [.. log.Where(line => line.Contains(WarningCode, StringComparison.Ordinal))];
        Assert.Single(notices);
        Assert.Contains(withLeftover, notices[0], StringComparison.Ordinal);
        Assert.DoesNotContain(clean, notices[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The refresh leaves the leftover file byte-identical. Naming the rename is the notice's whole job.
    /// </summary>
    [Fact]
    public async Task TheRefresherNeverRewritesTheLeftoverFile()
    {
        string project = ProjectRoot("project-read-only");
        ArrangeEnabledProject(project);
        WriteProvenOldKey(project);
        byte[] before = _wiring.Files.ReadAllBytes(Path.Combine(project, ".mcp.json"));
        List<string> log = [];

        ArbiterHookRefresher refresher = Refresher(log);
        await refresher.RefreshAsync([project]);

        Assert.Contains(log, line => line.Contains(WarningCode, StringComparison.Ordinal));
        Assert.Equal(before, _wiring.Files.ReadAllBytes(Path.Combine(project, ".mcp.json")));
    }

    private void ArrangeEnabledProject(string project)
    {
        _wiring.Files.CreateDirectory(project);
        _configs.WriteRepositoryConfig(project, "arbiter:\n  enabled: true\n  provider:\n    kind: none\n");
    }

    private void WriteProvenOldKey(string project)
    {
        string binary = McpWiringFixture.WriteExecutable(_wiring.Files, Path.Combine(_wiring.Root, "bin"), "kyber-weave-mcp");
        _wiring.Files.WriteAllText(
            Path.Combine(project, ".mcp.json"),
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["kyber-weave"] = new JsonObject
                    {
                        ["type"] = "stdio",
                        ["command"] = binary,
                        ["args"] = new JsonArray("--repo-root", ".")
                    }
                }
            }.ToJsonString());
    }
}
using System.Text.Json.Nodes;
using KyberWeave.Cli.Update;
using Xunit;

namespace KyberWeave.Tests;

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
public sealed class ArbiterHookRefresherRenameNoticeTests : IDisposable
{
    private const string WarningCode = "KW-MCP-WIRE-007";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Each enabled project that holds a proven old key gets its own log line naming the rule id and that
    /// project's root. A project with no leftover gets no such line.
    /// </summary>
    [Fact]
    public void TheRefresherLogsTheLeftoverOncePerProjectThatHoldsIt()
    {
        string withLeftover = Path.Combine(_temp.Path, "project-with-leftover");
        string clean = Path.Combine(_temp.Path, "project-clean");
        ArrangeEnabledProject(withLeftover);
        ArrangeEnabledProject(clean);
        WriteProvenOldKey(withLeftover);
        List<string> log = [];

        ArbiterHookRefresher refresher = new(log.Add, EnvironmentWithHome());
        refresher.Refresh([withLeftover, clean]);

        string[] notices = [.. log.Where(line => line.Contains(WarningCode, StringComparison.Ordinal))];
        Assert.Single(notices);
        Assert.Contains(withLeftover, notices[0], StringComparison.Ordinal);
        Assert.DoesNotContain(clean, notices[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The refresh leaves the leftover file byte-identical. Naming the rename is the notice's whole job.
    /// </summary>
    [Fact]
    public void TheRefresherNeverRewritesTheLeftoverFile()
    {
        string project = Path.Combine(_temp.Path, "project-read-only");
        ArrangeEnabledProject(project);
        WriteProvenOldKey(project);
        byte[] before = File.ReadAllBytes(Path.Combine(project, ".mcp.json"));

        ArbiterHookRefresher refresher = new(_ => { }, EnvironmentWithHome());
        refresher.Refresh([project]);

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(project, ".mcp.json")));
    }

    private Func<string, string?> EnvironmentWithHome()
    {
        string home = Path.Combine(_temp.Path, "home");
        Directory.CreateDirectory(home);
        return key => key == "HOME" ? home : null;
    }

    private static void ArrangeEnabledProject(string project)
    {
        Directory.CreateDirectory(Path.Combine(project, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(project, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n  enabled: true\n  provider:\n    kind: none\n");
    }

    private void WriteProvenOldKey(string project)
    {
        string bin = Path.Combine(_temp.Path, "bin");
        Directory.CreateDirectory(bin);
        string binary = McpWiringFixture.WriteExecutable(bin, "kyber-weave-mcp");
        File.WriteAllText(
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

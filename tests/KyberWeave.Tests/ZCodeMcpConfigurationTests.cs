using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Covers the ZCode MCP configuration check <c>squad doctor</c> fails on.
/// </summary>
/// <remarks>
/// The check exists because <c>ZCodeRenderer</c> grants MCP by fully qualified tool name — the
/// only form ZCode's exact-match allow-list registers — which makes each name a hard
/// requirement that fails an agent mid-run when its server is not connected. The config tiers
/// and the <c>mcp.servers</c> shape were verified against zai-org/ZCode 3.14.0 on 2026-09-21.
/// </remarks>
public sealed class ZCodeMcpConfigurationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static readonly string[] Required = ["codegraph", "context7", "kyber-weave"];

    private static string ServersConfig(params string[] serverNames)
    {
        Dictionary<string, object> servers = serverNames.ToDictionary(
            name => name,
            object (_) => new Dictionary<string, string> { ["command"] = "x" },
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mcp"] = new Dictionary<string, object> { ["servers"] = servers },
        });
    }

    private static Func<string, string?> Files(Dictionary<string, string> byPath) =>
        path => byPath.TryGetValue(path, out string? content) ? content : null;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Inspect_UserConfigDeclaringEveryRequiredServer_ReportsNothingMissing()
    {
        string root = Path.Combine(_temp.Path, "zcode");
        string working = Path.Combine(_temp.Path, "project");
        Directory.CreateDirectory(working);
        string userConfig = Path.Combine(root, "cli", "config.json");

        ZCodeMcpConfigurationReport report = ZCodeMcpConfiguration.Inspect(
            Required,
            root,
            working,
            Files(new(StringComparer.Ordinal) { [userConfig] = ServersConfig(Required) }));

        Assert.Empty(report.MissingServers);
        Assert.Equal(Required, report.ConfiguredServers);
        Assert.Equal([userConfig], report.InspectedPaths);
    }

    [Fact]
    public void Inspect_NoConfigAnywhere_ReportsEveryRequiredServerMissing()
    {
        string working = Path.Combine(_temp.Path, "bare");
        Directory.CreateDirectory(working);

        ZCodeMcpConfigurationReport report = ZCodeMcpConfiguration.Inspect(
            Required,
            Path.Combine(_temp.Path, "zcode"),
            working,
            _ => null);

        Assert.Equal(Required, report.MissingServers);
        Assert.Empty(report.InspectedPaths);
    }

    [Fact]
    public void Inspect_PartialUserConfig_ReportsOnlyTheAbsentServers()
    {
        string root = Path.Combine(_temp.Path, "partial-zcode");
        string working = Path.Combine(_temp.Path, "partial-project");
        Directory.CreateDirectory(working);

        ZCodeMcpConfigurationReport report = ZCodeMcpConfiguration.Inspect(
            Required,
            root,
            working,
            Files(new(StringComparer.Ordinal)
            {
                [Path.Combine(root, "cli", "config.json")] = ServersConfig("codegraph", "kyber-weave"),
            }));

        Assert.Equal(["context7"], report.MissingServers);
    }

    /// <summary>
    /// A server declared in any tier counts. Precedence only matters when two tiers define the
    /// same server differently, which is not what this check answers.
    /// </summary>
    [Theory]
    [InlineData("zcode.json")]
    [InlineData(".zcode/config.json")]
    public void Inspect_ProjectConfigCompletesTheUserConfig(string relativePath)
    {
        string root = Path.Combine(_temp.Path, $"merge-zcode-{relativePath.GetHashCode(StringComparison.Ordinal)}");
        string working = Path.Combine(_temp.Path, $"merge-project-{relativePath.GetHashCode(StringComparison.Ordinal)}");
        Directory.CreateDirectory(working);

        string projectConfig = Path.Combine(
            working,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        ZCodeMcpConfigurationReport report = ZCodeMcpConfiguration.Inspect(
            Required,
            root,
            working,
            Files(new(StringComparer.Ordinal)
            {
                [Path.Combine(root, "cli", "config.json")] = ServersConfig("codegraph"),
                [projectConfig] = ServersConfig("context7", "kyber-weave"),
            }));

        Assert.Empty(report.MissingServers);
        Assert.Contains(projectConfig, report.InspectedPaths, StringComparer.Ordinal);
    }

    /// <summary>
    /// ZCode walks project config directories from the working directory up to the git
    /// worktree root, so a config at the repository root counts for a nested working directory.
    /// </summary>
    [Fact]
    public void Inspect_ProjectConfigAtTheWorktreeRootCountsForANestedWorkingDirectory()
    {
        string repositoryRoot = Path.Combine(_temp.Path, "repo");
        string nested = Path.Combine(repositoryRoot, "src", "nested");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));

        string rootConfig = Path.Combine(repositoryRoot, "zcode.json");

        ZCodeMcpConfigurationReport report = ZCodeMcpConfiguration.Inspect(
            Required,
            Path.Combine(_temp.Path, "nested-zcode"),
            nested,
            Files(new(StringComparer.Ordinal) { [rootConfig] = ServersConfig(Required) }));

        Assert.Empty(report.MissingServers);
        Assert.Contains(rootConfig, report.InspectedPaths, StringComparer.Ordinal);
    }

    /// <summary>
    /// The walk stops at the worktree root, matching ZCode. A config above it is outside the
    /// project and must not silently satisfy the check.
    /// </summary>
    [Fact]
    public void Inspect_ConfigAboveTheWorktreeRootIsNotRead()
    {
        string outer = Path.Combine(_temp.Path, "outer");
        string repositoryRoot = Path.Combine(outer, "repo");
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));

        ZCodeMcpConfigurationReport report = ZCodeMcpConfiguration.Inspect(
            Required,
            Path.Combine(_temp.Path, "outer-zcode"),
            repositoryRoot,
            Files(new(StringComparer.Ordinal)
            {
                [Path.Combine(outer, "zcode.json")] = ServersConfig(Required),
            }));

        Assert.Equal(Required, report.MissingServers);
        Assert.Empty(report.InspectedPaths);
    }

    /// <summary>
    /// A doctor check that crashes on an unparseable config is worse than one that reports the
    /// servers it could find.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"mcp\":{}}")]
    [InlineData("{\"mcp\":{\"servers\":[]}}")]
    [InlineData("{\"mcp\":\"not-an-object\"}")]
    [InlineData("[]")]
    public void Inspect_UnparseableConfigContributesNothingRatherThanThrowing(string content)
    {
        string root = Path.Combine(_temp.Path, $"bad-{content.GetHashCode(StringComparison.Ordinal)}");
        string working = Path.Combine(_temp.Path, $"bad-project-{content.GetHashCode(StringComparison.Ordinal)}");
        Directory.CreateDirectory(working);

        ZCodeMcpConfigurationReport report = ZCodeMcpConfiguration.Inspect(
            Required,
            root,
            working,
            Files(new(StringComparer.Ordinal)
            {
                [Path.Combine(root, "cli", "config.json")] = content,
            }));

        Assert.Equal(Required, report.MissingServers);
    }

    /// <summary>
    /// ZCode's own config loader tolerates comments and trailing commas, so a hand-edited file
    /// that ZCode reads must not read as empty here.
    /// </summary>
    [Fact]
    public void Inspect_ConfigWithCommentsAndTrailingCommasIsRead()
    {
        string root = Path.Combine(_temp.Path, "jsonc-zcode");
        string working = Path.Combine(_temp.Path, "jsonc-project");
        Directory.CreateDirectory(working);

        const string content = """
            {
              // the servers Squad agents call
              "mcp": {
                "servers": {
                  "codegraph": { "command": "codegraph" },
                  "context7": { "url": "https://mcp.context7.com/mcp" },
                  "kyber-weave": { "command": "kyber-weave-mcp" },
                },
              },
            }
            """;

        ZCodeMcpConfigurationReport report = ZCodeMcpConfiguration.Inspect(
            Required,
            root,
            working,
            Files(new(StringComparer.Ordinal)
            {
                [Path.Combine(root, "cli", "config.json")] = content,
            }));

        Assert.Empty(report.MissingServers);
    }
}

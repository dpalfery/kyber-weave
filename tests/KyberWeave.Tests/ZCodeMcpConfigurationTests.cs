using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Covers the ZCode MCP configuration check <c>squad doctor</c> fails on, through the in-memory file system.
/// </summary>
/// <remarks>
/// The check exists because <c>ZCodeRenderer</c> grants MCP by fully qualified tool name — the
/// only form ZCode's exact-match allow-list registers — which makes each name a hard
/// requirement that fails an agent mid-run when its server is not connected. The config tiers
/// and the <c>mcp.servers</c> shape were verified against zai-org/ZCode 3.14.0 on 2026-09-21.
/// </remarks>
public sealed class ZCodeMcpConfigurationTests
{
    private static readonly string Base = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "zcode-mcp");
    private static readonly string[] Required = ["codegraph", "context7", "kw-docs"];

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private ZCodeMcpConfiguration Zcode =>
        new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: Base));

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

    private void Write(string path, string content)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }

    private string WorkingDirectory(string name)
    {
        string working = Path.Combine(Base, name);
        _files.CreateDirectory(working);
        return working;
    }

    [Fact]
    public void Inspect_UserConfigDeclaringEveryRequiredServer_ReportsNothingMissing()
    {
        string root = Path.Combine(Base, "zcode");
        string working = WorkingDirectory("project");
        string userConfig = Path.Combine(root, "cli", "config.json");
        Write(userConfig, ServersConfig(Required));

        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, root, working);

        Assert.Empty(report.MissingServers);
        Assert.Equal(Required, report.ConfiguredServers);
        Assert.Equal([userConfig], report.InspectedPaths);
    }

    [Fact]
    public void Inspect_NoConfigAnywhere_ReportsEveryRequiredServerMissing()
    {
        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, Path.Combine(Base, "zcode"), WorkingDirectory("bare"));

        Assert.Equal(Required, report.MissingServers);
        Assert.Empty(report.InspectedPaths);
    }

    [Fact]
    public void Inspect_PartialUserConfig_ReportsOnlyTheAbsentServers()
    {
        string root = Path.Combine(Base, "partial-zcode");
        Write(Path.Combine(root, "cli", "config.json"), ServersConfig("codegraph", "kw-docs"));

        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, root, WorkingDirectory("partial-project"));

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
        string root = Path.Combine(Base, "merge-zcode");
        string working = WorkingDirectory("merge-project");
        string projectConfig = Path.Combine(working, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Write(Path.Combine(root, "cli", "config.json"), ServersConfig("codegraph"));
        Write(projectConfig, ServersConfig("context7", "kw-docs"));

        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, root, working);

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
        string repositoryRoot = Path.Combine(Base, "repo");
        string nested = WorkingDirectory(Path.Combine("repo", "src", "nested"));
        _files.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
        string rootConfig = Path.Combine(repositoryRoot, "zcode.json");
        Write(rootConfig, ServersConfig(Required));

        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, Path.Combine(Base, "nested-zcode"), nested);

        Assert.Empty(report.MissingServers);
        Assert.Contains(rootConfig, report.InspectedPaths, StringComparer.Ordinal);
    }

    /// <summary>
    /// A <c>.git</c> file, which a linked worktree has instead of a directory, marks the worktree root too.
    /// </summary>
    [Fact]
    public void Inspect_GitFileMarksTheWorktreeRootAsADirectoryDoes()
    {
        string repositoryRoot = Path.Combine(Base, "linked");
        string nested = WorkingDirectory(Path.Combine("linked", "src"));
        Write(Path.Combine(repositoryRoot, ".git"), "gitdir: elsewhere");
        string rootConfig = Path.Combine(repositoryRoot, "zcode.json");
        Write(rootConfig, ServersConfig(Required));

        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, Path.Combine(Base, "linked-zcode"), nested);

        Assert.Contains(rootConfig, report.InspectedPaths, StringComparer.Ordinal);
    }

    /// <summary>
    /// The walk stops at the worktree root, matching ZCode. A config above it is outside the
    /// project and must not silently satisfy the check.
    /// </summary>
    [Fact]
    public void Inspect_ConfigAboveTheWorktreeRootIsNotRead()
    {
        string outer = Path.Combine(Base, "outer");
        string repositoryRoot = WorkingDirectory(Path.Combine("outer", "repo"));
        _files.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
        Write(Path.Combine(outer, "zcode.json"), ServersConfig(Required));

        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, Path.Combine(Base, "outer-zcode"), repositoryRoot);

        Assert.Equal(Required, report.MissingServers);
        Assert.Empty(report.InspectedPaths);
    }

    /// <summary>
    /// With no worktree marker anywhere, only the starting directory contributes, as in ZCode: a config in
    /// its parent is not read.
    /// </summary>
    [Fact]
    public void Inspect_NoWorktreeMarker_ReadsOnlyTheStartingDirectory()
    {
        string outer = Path.Combine(Base, "unmarked");
        string working = WorkingDirectory(Path.Combine("unmarked", "project"));
        Write(Path.Combine(outer, "zcode.json"), ServersConfig(Required));
        string ownConfig = Path.Combine(working, "zcode.json");
        Write(ownConfig, ServersConfig("codegraph"));

        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, Path.Combine(Base, "unmarked-zcode"), working);

        Assert.Equal(["context7", "kw-docs"], report.MissingServers);
        Assert.Equal([ownConfig], report.InspectedPaths);
    }

    /// <summary>
    /// A tier the process may not read declares nothing, so the check still reports the servers the other
    /// tiers declare.
    /// </summary>
    [Fact]
    public void Inspect_UnreadableTier_DeclaresNothingRatherThanThrowing()
    {
        string root = Path.Combine(Base, "unreadable-zcode");
        string working = WorkingDirectory("unreadable-project");
        string userConfig = Path.Combine(root, "cli", "config.json");
        Write(userConfig, ServersConfig(Required));
        Assert.Empty(Zcode.Inspect(Required, root, working).MissingServers);

        _files.FailWhen(
            operation => operation.Name == "ReadAllText" && string.Equals(operation.Path, userConfig, StringComparison.Ordinal),
            new UnauthorizedAccessException());

        Assert.Equal(Required, Zcode.Inspect(Required, root, working).MissingServers);
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
        string root = Path.Combine(Base, "bad-zcode");
        Write(Path.Combine(root, "cli", "config.json"), content);

        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, root, WorkingDirectory("bad-project"));

        Assert.Equal(Required, report.MissingServers);
    }

    /// <summary>
    /// ZCode's own config loader tolerates comments and trailing commas, so a hand-edited file
    /// that ZCode reads must not read as empty here.
    /// </summary>
    [Fact]
    public void Inspect_ConfigWithCommentsAndTrailingCommasIsRead()
    {
        string root = Path.Combine(Base, "jsonc-zcode");
        const string Content = """
            {
              // the servers Squad agents call
              "mcp": {
                "servers": {
                  "codegraph": { "command": "codegraph" },
                  "context7": { "url": "https://mcp.context7.com/mcp" },
                  "kw-docs": { "command": "kyber-weave-mcp" },
                },
              },
            }
            """;
        Write(Path.Combine(root, "cli", "config.json"), Content);

        ZCodeMcpConfigurationReport report = Zcode.Inspect(Required, root, WorkingDirectory("jsonc-project"));

        Assert.Empty(report.MissingServers);
    }
}

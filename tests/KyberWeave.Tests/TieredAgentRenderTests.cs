using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The five tiered agents are ordinary canonical agents: every harness renderer must produce them
/// and must keep producing every agent it produced before, with no renderer code that names an
/// agent. A renderer that quietly dropped one would ship a roster the conductor cannot dispatch to.
/// </summary>
public sealed class TieredAgentRenderTests
{
    /// <summary>The five agents the tiered roster adds.</summary>
    private static readonly string[] TieredAgents =
    [
        "dev-easy",
        "dev-medium",
        "dev-hard",
        "platform-researcher",
        "devops"
    ];

    /// <summary>The roster as it stood before the tiered agents were added.</summary>
    private static readonly string[] ExistingAgents =
    [
        "architect",
        "azure-reader",
        "bug-crusher",
        "bug-crusher-investigator",
        "code-reviewer",
        "conductor",
        "csharp-dev",
        "dal-dev",
        "docs-dev",
        "github-devops",
        "go-dev",
        "maui-dev",
        "product-owner",
        "pulumi-dev",
        "python-dev",
        "react-dev",
        "research-agent",
        "review-lens",
        "review-triage",
        "rust-dev",
        "sql-database-architect",
        "tauri-dev",
        "test-dev",
        "typescript-dev"
    ];

    [Theory]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("claude")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    [InlineData("kilo")]
    [InlineData("antigravity")]
    [InlineData("warp")]
    [InlineData("factory")]
    [InlineData("pi")]
    [InlineData("zcode")]
    [InlineData("devin")]
    [InlineData("commandcode")]
    public async Task EveryHarnessRendersTheFiveNewAgentsAndEveryAgentRenderedBefore(string token)
    {
        Assert.Equal(13, SquadTargetCatalog.All.Count);
        SquadTarget target = SquadTargetCatalog.Parse([token]).Single();

        SquadRenderResult render = await TestSquadCorpus.GetProjectRenderAsync(target);

        Assert.True(render.Success, $"{token}: {string.Join("; ", render.Errors)}");
        string[] paths = render.Files
            .Select(file => file.RelativePath.Replace('\\', '/'))
            .ToArray();
        string[] missing = ExistingAgents
            .Concat(TieredAgents)
            .Where(agent => !paths.Any(path => RendersAgent(path, agent)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"{token} renders no file for: {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// The renderer chooses a projection from the agent's frontmatter and the harness's shape, never
    /// from the agent's name, so no file under the rendering source may name a developer agent.
    /// </summary>
    [Fact]
    public void NoRenderingSourceNamesADeveloperAgent()
    {
        string[] renderingRoots =
        [
            Path.Combine(KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Domain", "Squad", "Rendering"),
            Path.Combine(KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Application", "Squad", "Rendering")
        ];
        string[] developers = ["dev-easy", "dev-medium", "dev-hard"];

        string[] offenders = renderingRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file => developers.Any(agent =>
                File.ReadAllText(file).Contains(agent, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// A native agent file carries the agent's name as its own file stem — <c>&lt;name&gt;.md</c>,
    /// <c>&lt;name&gt;.agent.md</c> or <c>&lt;name&gt;.toml</c> — or as its folder; a fallback
    /// projection carries it as the skill's folder. Either way one path segment is the agent.
    /// </summary>
    private static bool RendersAgent(string relativePath, string agent)
    {
        string[] segments = relativePath.Split('/');
        return segments.Any(segment =>
            string.Equals(segment, agent, StringComparison.Ordinal) ||
            segment.StartsWith($"{agent}.", StringComparison.Ordinal));
    }
}

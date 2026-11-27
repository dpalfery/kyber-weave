using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
/// The five tiered agents are ordinary canonical agents: every harness renderer must produce them
/// and must keep producing every agent it produced before, with no renderer code that names an
/// agent. A renderer that quietly dropped one would ship a roster the conductor cannot dispatch to.
/// </summary>
public sealed class TieredAgentRenderTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>The five agents the tiered roster adds.</summary>
    private static readonly string[] TieredAgents =
    [
        "dev-easy",
        "dev-medium",
        "dev-hard",
        "platform-researcher",
        "devops"
    ];

    /// <summary>
    /// The agents that served before the tiered roster and still ship: the removal deleted the per-technology
    /// workers and their planning, testing and delivery predecessors, and this list is what remains.
    /// </summary>
    private static readonly string[] ExistingAgents =
    [
        "architect",
        "bug-crusher",
        "bug-crusher-investigator",
        "code-reviewer",
        "conductor",
        "docs-dev",
        "product-owner",
        "research-agent",
        "review-lens",
        "review-triage"
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
    /// <remarks>
    /// <c>dev-hard</c> is also the name of the model profile that agent uses, so a switch arm that
    /// maps the profile to a harness value — <c>"dev-hard" =&gt; "high"</c> — is a profile fact, not
    /// a renderer naming the agent. Any other mention of any developer name is a per-agent
    /// special case and fails.
    /// </remarks>
    [Fact]
    public void NoRenderingSourceNamesADeveloperAgent()
    {
        string[] renderingRoots =
        [
            "src/KyberWeave.Domain/Squad/Rendering",
            "src/KyberWeave.Application/Squad/Rendering"
        ];

        string[] offenders = renderingRoots
            .SelectMany(root => Corpus.EnumerateFiles(root, "*.cs"))
            .SelectMany(file => Corpus.ReadText(file)
                .ReplaceLineEndings("\n")
                .Split('\n')
                .Select(line => (File: file, Line: line)))
            .Where(entry => NamesADeveloperAgent(entry.Line))
            .Select(entry => $"{Path.GetFileName(entry.File)}: {entry.Line.Trim()}")
            .ToArray();

        Assert.Empty(offenders);
    }

    private static bool NamesADeveloperAgent(string line) =>
        line.Contains("dev-easy", StringComparison.Ordinal) ||
        line.Contains("dev-medium", StringComparison.Ordinal) ||
        (DevHardToken.IsMatch(line) && !ProfileMappingArm.IsMatch(line));

    private static readonly Regex DevHardToken = new(
        @"(?<![\w-])dev-hard(?![\w-])", RegexOptions.CultureInvariant);

    /// <summary>A model-profile switch arm or table entry: <c>"dev-hard" =&gt; "high",</c> or <c>["dev-hard"] = "high",</c>.</summary>
    private static readonly Regex ProfileMappingArm = new(
        @"^\s*(case\s+)?""dev-hard""\s*=>|^\s*\[""dev-hard""\]\s*=", RegexOptions.CultureInvariant);

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

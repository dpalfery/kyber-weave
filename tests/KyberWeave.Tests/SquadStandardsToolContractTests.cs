using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T19 RED — every developer agent and every developer skill reaches a coding standard through
/// <c>docs_get_coding_standard_for_technology</c> instead of being told to read a path.
/// </summary>
/// <remarks>
/// <para>
/// Reading the path costs a worker the whole standard. <c>docs/standards/test/README.md</c> is
/// 21,354 characters and the tool's default budget is 12,000, so a worker that reads the file
/// spends context the budget exists to bound — and a worker whose profile grants no file read
/// cannot reach the standard at all. The tool answers with the sections the task names, and says
/// what it left out.
/// </para>
/// <para>
/// The registry property names stay in the text. <c>&lt;csharp-coding-standard&gt;</c> and the
/// rest are still what a host's Config Reg publishes, and a standard that is missing or still a
/// draft has to be able to report that as the question it cannot answer — so this file asserts
/// that each file names the tool and keeps a registry property name. That a file read is not
/// presented as the way to get the standard is graded from the prose rule catalog.
/// </para>
/// </remarks>
public sealed class SquadStandardsToolContractTests
{
    private const string ToolName = "docs_get_coding_standard_for_technology";

    /// <summary>
    /// The canonical developer agents: the roles that write code, and so the ones whose
    /// instructions decide whether a standard arrives for a budget or is read whole.
    /// </summary>
    private static readonly string[] DeveloperAgents =
    [
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "go-dev",
        "maui-dev",
        "pulumi-dev",
        "python-dev",
        "react-dev",
        "rust-dev",
        "sql-database-architect",
        "tauri-dev",
        "test-dev",
        "typescript-dev"
    ];

    /// <summary>
    /// The canonical developer skills that state the coding-standard policy line for their own
    /// role. Chosen by reading the tree rather than by assuming symmetry with the agents:
    /// <c>react-dev</c> and <c>tauri-dev</c> ship no skill at all, and <c>csharp-dev</c> and
    /// <c>dal-dev</c> ship one that names no standard of its own, so neither carries the line
    /// that is being rerouted here.
    /// </summary>
    private static readonly string[] DeveloperSkills =
    [
        "go-dev",
        "maui-dev",
        "python-dev",
        "rust-dev",
        "test-dev",
        "typescript-dev"
    ];

    /// <summary>A registry property name as the repository instructions publish it.</summary>
    private static readonly Regex RegistryProperty = new(
        @"<[a-z][a-z-]*-coding-standard>",
        RegexOptions.CultureInvariant);

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// A developer agent reaches its stack's standard through the tool, so the standard arrives
    /// with the sections the task names and within a budget rather than as a whole file.
    /// </summary>
    /// <param name="agentName">The canonical agent identity.</param>
    [Theory]
    [InlineData("csharp-dev")]
    [InlineData("dal-dev")]
    [InlineData("github-devops")]
    [InlineData("go-dev")]
    [InlineData("maui-dev")]
    [InlineData("pulumi-dev")]
    [InlineData("python-dev")]
    [InlineData("react-dev")]
    [InlineData("rust-dev")]
    [InlineData("sql-database-architect")]
    [InlineData("tauri-dev")]
    [InlineData("test-dev")]
    [InlineData("typescript-dev")]
    public void EveryDeveloperAgentReachesItsCodingStandardThroughTheTool(string agentName)
    {
        AssertRoutesThroughTheTool($"agents/{agentName}.md");
    }

    /// <summary>
    /// When github-devops runs another technology's toolchain (for example, dotnet via csharp-coding-standard),
    /// its workflow routes that secondary standard through the tool as well rather than relying on built-in defaults.
    /// </summary>
    [Fact]
    public void GithubDevopsWorkflowRoutesSecondaryToolchainStandardThroughTheTool()
    {
        string path = Path.Combine(ProductRoot, "agents", "github-devops.md");
        string text = File.ReadAllText(path);

        Assert.Contains("<csharp-coding-standard>", text, StringComparison.Ordinal);
        Assert.Contains(ToolName, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same contract for the developer skills, which carry the policy line the agent bodies
    /// defer to: a skill that tells a worker to read the path undoes the agent that does not.
    /// </summary>
    /// <param name="skillName">The canonical skill identity.</param>
    [Theory]
    [InlineData("go-dev")]
    [InlineData("maui-dev")]
    [InlineData("python-dev")]
    [InlineData("rust-dev")]
    [InlineData("test-dev")]
    [InlineData("typescript-dev")]
    public void EveryDeveloperSkillReachesItsCodingStandardThroughTheTool(string skillName)
    {
        AssertRoutesThroughTheTool($"skills/{skillName}/SKILL.md");
    }

    /// <summary>
    /// The registry property names survive the reroute, in every file that carries one. They are
    /// how a host repository's Config Reg publishes a standard, how a worker names the standard it
    /// could not find, and what makes "not declared here" and "still a draft" answerable at all —
    /// a route that replaced them with a bare technology word would lose all three.
    /// </summary>
    [Fact]
    public void EveryRoutingFileKeepsTheRegistryPropertyNamesItCarried()
    {
        List<string> missing = new();
        foreach (string relativePath in AgentAndSkillPaths())
        {
            string text = File.ReadAllText(Path.Combine(ProductRoot, relativePath));
            if (!RegistryProperty.IsMatch(text)) missing.Add(relativePath);
        }

        Assert.True(
            missing.Count == 0,
            "These files no longer name a coding-standard registry property, so a worker cannot "
            + "name the standard it found missing: " + string.Join(", ", missing));
    }

    private static IEnumerable<string> AgentAndSkillPaths() =>
        DeveloperAgents.Select(name => $"agents/{name}.md")
            .Concat(DeveloperSkills.Select(name => $"skills/{name}/SKILL.md"));

    /// <summary>
    /// A routing instruction names the tool. What it says about reading the file instead is graded
    /// from the prose rule catalog (<c>standards-via-tool</c>).
    /// </summary>
    private static void AssertRoutesThroughTheTool(string relativePath)
    {
        string path = Path.Combine(ProductRoot, relativePath);
        Assert.True(File.Exists(path), $"Expected the canonical product file '{relativePath}' at '{path}'.");

        string text = File.ReadAllText(path);
        Assert.True(
            text.Contains(ToolName, StringComparison.Ordinal),
            $"'{relativePath}' must reach its coding standard through {ToolName}; it does not name it.");
    }
}

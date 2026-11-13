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
/// the route and not the wording. Two facts per file: it names the tool, and it does not present
/// a file read as the way to get the standard. A direct read stays allowed as the fallback for a
/// tool that is missing, which is why the read assertion is scoped to a block that neither names
/// the tool nor says the tool may be absent.
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

    /// <summary>
    /// The files where the prose rule catalog's <c>standards-via-tool</c> rule keeps the retired
    /// "read the standard's file" route out (its <c>absent-from</c> list), so the read-route
    /// heuristic below does not check them a second time. They still must name the tool.
    /// </summary>
    private static readonly HashSet<string> ReadRouteCheckedByTheCatalog = new(StringComparer.Ordinal)
    {
        "agents/csharp-dev.md",
        "agents/go-dev.md",
        "agents/python-dev.md",
        "agents/test-dev.md",
        "skills/go-dev/SKILL.md",
        "skills/test-dev/SKILL.md"
    };

    /// <summary>A verb that tells a reader to go and fetch a document.</summary>
    private static readonly Regex ReadImperative = new(
        @"\b(?:read|reads|reading|open|opens|consult|consults)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>How a block names a coding standard it could go and fetch.</summary>
    private static readonly Regex StandardByItsLocation = new(
        @"the\s+path\s+declared\s+as|AGENTS[.]md|docs/standards/|<[a-z][a-z-]*-coding-standard>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>How a block says the fetch is only the stand-in for a tool that is not there.</summary>
    private static readonly Regex FallbackForAMissingTool = new(
        @"only\s+when|only\s+if|if\s+the\s+tool|when\s+the\s+tool|missing|unavailable|not\s+available|fall\s*back",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// A new block starts at a heading, a numbered step, or a bullet. Blocks are what the read
    /// assertion is scoped to, so a long numbered list is twelve claims rather than one: a step
    /// that says to read a path is not excused by a later step that happens to name the tool.
    /// </summary>
    private static readonly Regex BlockStart = new(
        @"^\s*(?:#{1,6}\s|\d+[.)]\s|[-*+]\s)",
        RegexOptions.CultureInvariant);

    /// <summary>A registry property name as the repository instructions publish it.</summary>
    private static readonly Regex RegistryProperty = new(
        @"<[a-z][a-z-]*-coding-standard>",
        RegexOptions.CultureInvariant);

    /// <summary>The paragraph break: a blank line, with any indentation around it.</summary>
    private static readonly Regex BlankLine = new(
        @"\n\s*\n",
        RegexOptions.CultureInvariant);

    /// <summary>Runs of whitespace inside a block, which is what a wrapped sentence is made of.</summary>
    private static readonly Regex WhitespaceRun = new(
        @"\s+",
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

        string step1 = Blocks(text).First(b => b.StartsWith("1. Fetch", StringComparison.Ordinal));
        Assert.Contains("<csharp-coding-standard>", step1, StringComparison.Ordinal);
        Assert.Contains(ToolName, step1, StringComparison.Ordinal);
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
    /// The two facts a routing instruction owes: it names the tool, and no block of it tells the
    /// reader to get the standard by reading a path unless that same block says the tool may be
    /// absent. The second is skipped for the files the prose rule catalog already checks for it. The second is what the path form cost — 15,000 characters of a 21,354-character
    /// standard read whole, and nothing read at all where no read tool is granted.
    /// </summary>
    private static void AssertRoutesThroughTheTool(string relativePath)
    {
        string path = Path.Combine(ProductRoot, relativePath);
        Assert.True(File.Exists(path), $"Expected the canonical product file '{relativePath}' at '{path}'.");

        string text = File.ReadAllText(path);
        Assert.True(
            text.Contains(ToolName, StringComparison.Ordinal),
            $"'{relativePath}' must reach its coding standard through {ToolName}; it does not name it.");

        if (ReadRouteCheckedByTheCatalog.Contains(relativePath))
            return;

        List<string> offenders = Blocks(text)
            .Where(block => ReadImperative.IsMatch(block))
            .Where(block => StandardByItsLocation.IsMatch(block))
            .Where(block => !block.Contains(ToolName, StringComparison.Ordinal))
            .Where(block => !FallbackForAMissingTool.IsMatch(block))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"'{relativePath}' must not instruct a file read as the way to reach its coding "
            + $"standard; call {ToolName} instead, and read the file only in a sentence that says "
            + "the tool is missing or the standard is not declared. Offending text:\n"
            + string.Join("\n---\n", offenders));
    }

    /// <summary>
    /// The text split into the blocks an instruction lives in: blank-line-separated paragraphs,
    /// and inside a paragraph each heading, numbered step, and bullet starts its own block.
    /// Whitespace is collapsed so a sentence wrapped across lines is still one block, and so a
    /// pattern does not have to care which line a phrase was wrapped at.
    /// </summary>
    private static IEnumerable<string> Blocks(string text)
    {
        foreach (string paragraph in BlankLine.Split(text))
        {
            List<string> current = new();
            foreach (string line in paragraph.Split('\n'))
            {
                if (BlockStart.IsMatch(line) && current.Count > 0)
                {
                    yield return WhitespaceRun.Replace(string.Join(' ', current), " ").Trim();
                    current.Clear();
                }

                current.Add(line);
            }

            if (current.Count > 0)
                yield return WhitespaceRun.Replace(string.Join(' ', current), " ").Trim();
        }
    }
}

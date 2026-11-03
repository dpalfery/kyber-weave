using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T4 — MCP must ship as an installable dotnet tool with host-neutral tool descriptions.
/// </summary>
public class McpPackagingTests
{
    [Fact]
    public void McpProjectIsPackableAsDotnetToolWithKyberWeaveMcpCommand()
    {
        XDocument doc = XDocument.Load(KyberWeaveTestPaths.McpProjectPath);
        XNamespace ns = doc.Root!.Name.Namespace;

        Assert.Equal("true", PropertyValue(doc, ns, "PackAsTool"));
        Assert.Equal("kyber-weave-mcp", PropertyValue(doc, ns, "ToolCommandName"));
        Assert.Equal("true", PropertyValue(doc, ns, "IsPackable"));
    }

    [Fact]
    public void PackagedMcpConfigurationBindsTheServerToTheCurrentRepository()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(KyberWeaveTestPaths.SquadMcpConfigurationPath));
        JsonElement server = document.RootElement
            .GetProperty("mcpServers")
            .GetProperty("kyber-weave");

        Assert.Equal(
            ["--repo-root", "."],
            server.GetProperty("args").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public void McpToolDescriptionsDoNotHardRequireMotorcycleRAG()
    {
        string source = File.ReadAllText(KyberWeaveTestPaths.McpDocsToolsSourcePath);

        Assert.DoesNotContain("MotorcycleRAG", source, StringComparison.Ordinal);
    }

    [Fact]
    public void McpToolDescriptionsUseGenericRepositoryDocumentationWording()
    {
        string source = File.ReadAllText(KyberWeaveTestPaths.McpDocsToolsSourcePath);

        Assert.Contains("repository documentation", source, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A tool description is routing metadata read while the model is choosing between
    /// every installed tool. An unroutable description does not fail the build anywhere
    /// else, so every tool is scored here rather than only the one that prompted the work.
    /// </summary>
    /// <remarks>
    /// The components are asserted individually rather than through <c>Total</c>. A tool
    /// description is routing metadata *plus a calling contract* — it must also state what
    /// comes back and what an empty result means, which a skill description never carries.
    /// That pushes it past the scorer's 500-character length budget by design, so the total
    /// would penalise the very content that makes the tool callable. "Negative boundary" is
    /// excluded for the same reason and asserted separately, since only one of the tools
    /// should own an exclusion.
    /// </remarks>
    [Theory]
    [InlineData("docs_explore")]
    [InlineData("docs_for_symbol")]
    [InlineData("docs_analysis_candidates")]
    [InlineData("docs_glossary")]
    [InlineData("docs_status")]
    [InlineData("docs_validate")]
    [InlineData("docs_drift")]
    [InlineData("agent_coding_standards")]
    public void McpToolDescriptionsScoreAsRoutingMetadata(string toolName)
    {
        DescriptionScore score = ScoreDescription(toolName);

        Assert.Equal(35, score.Components.Single(c => c.Name == "Trigger clause").Points);
        Assert.Equal(15, score.Components.Single(c => c.Name == "Specific opening").Points);
        Assert.Equal(15, score.Components.Single(c => c.Name == "Trigger keywords").Points);
    }

    /// <summary>
    /// Models pick tools by the words a request uses. <c>docs_explore</c> lost to grep while
    /// it described itself in the project's own vocabulary, so the description must keep the
    /// requester's words and must say it comes before a file search.
    /// </summary>
    [Fact]
    public void DocsExploreDescriptionUsesRequesterVocabularyAndPrecedesFileSearch()
    {
        string description = McpToolMethod("docs_explore")
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (string jargon in new[] { "governed", "corpus", "frontmatter", "code joins" })
        {
            Assert.DoesNotContain(jargon, description, StringComparison.OrdinalIgnoreCase);
        }

        foreach (string phrase in new[] { "how to", "runbook", "before grepping", "docs/" })
        {
            Assert.Contains(phrase, description, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A tool description is read only once the model is already weighing tools. The server
    /// instructions are read before that, so the documentation-first rule lives there too.
    /// </summary>
    [Fact]
    public void ServerInstructionsTellModelsToTryDocsExploreBeforeFileSearch()
    {
        string text = DocsServerInstructions.Text;

        Assert.Contains("docs_explore", text, StringComparison.Ordinal);
        Assert.Contains("before grepping", text, StringComparison.Ordinal);
        foreach (string tool in new[] { "docs_for_symbol", "docs_glossary", "docs_analysis_candidates", "docs_status", "docs_validate", "docs_drift", "agent_coding_standards" })
        {
            Assert.Contains(tool, text, StringComparison.Ordinal);
        }

        ModelContextProtocol.Server.McpServerOptions options = new();
        DocsServerInstructions.Configure(options);
        Assert.Equal(text, options.ServerInstructions);
    }

    [Fact]
    public void McpServerHostRegistersTheDocumentationInstructions()
    {
        string program = File.ReadAllText(
            Path.Combine(Path.GetDirectoryName(KyberWeaveTestPaths.McpDocsToolsSourcePath)!, "Program.cs"));

        Assert.Contains("AddMcpServer(DocsServerInstructions.Configure)", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exclusions must be asymmetric: when two tools share a boundary the broader one
    /// yields and the narrower states its territory positively. If every description
    /// disclaimed the overlap, requests in the middle would match nothing — so only
    /// <c>docs_explore</c>, the broadest of the tools, carries a negative boundary.
    /// </summary>
    [Fact]
    public void OnlyTheBroadestMcpToolCarriesANegativeBoundary()
    {
        static int Boundary(string toolName) =>
            ScoreDescription(toolName).Components.Single(c => c.Name == "Negative boundary").Points;

        Assert.Equal(20, Boundary("docs_explore"));
        Assert.Equal(0, Boundary("docs_for_symbol"));
        Assert.Equal(0, Boundary("docs_analysis_candidates"));
        Assert.Equal(0, Boundary("docs_glossary"));
        Assert.Equal(0, Boundary("docs_status"));
        Assert.Equal(0, Boundary("docs_validate"));
        Assert.Equal(0, Boundary("docs_drift"));
        Assert.Equal(0, Boundary("agent_coding_standards"));
    }

    /// <summary>
    /// Every tool is a pure lookup, and a client can act on <c>readOnlyHint</c> where it
    /// cannot act on prose claiming the same thing. The corpus is local, so no tool
    /// reaches an open world.
    /// </summary>
    [Theory]
    [InlineData("docs_explore")]
    [InlineData("docs_for_symbol")]
    [InlineData("docs_analysis_candidates")]
    [InlineData("docs_glossary")]
    [InlineData("docs_status")]
    [InlineData("docs_validate")]
    [InlineData("docs_drift")]
    [InlineData("agent_coding_standards")]
    public void McpToolsDeclareReadOnlyAndClosedWorldAnnotations(string toolName)
    {
        McpServerToolAttribute attribute = McpToolMethod(toolName).GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.True(attribute.ReadOnly, $"{toolName} must declare ReadOnly so clients see readOnlyHint.");
        Assert.False(attribute.OpenWorld, $"{toolName} queries a local corpus and must not claim an open world.");
    }

    /// <summary>
    /// A client binds arguments to a tool by the names the server publishes, so a renamed
    /// parameter or a changed default breaks callers while leaving the suite green as long as
    /// nothing pins them. <c>docs_analysis_candidates</c> and <c>docs_glossary</c> are pinned in
    /// <c>McpAnalysisToolsTests</c>; this is the same pin for the coding-standards lookup. Its
    /// declared name is already covered — the theory above resolves each tool through it, so a
    /// rename fails there — as are its annotations; the parameter contract was the gap.
    /// </summary>
    /// <remarks>
    /// The declared default is asserted as the literal a client sees on the wire rather than
    /// through <c>DocumentIndex.DefaultCharBudget</c>, matching the sibling pin: a constant that
    /// moves has moved the published default too, and that is what a bound client notices.
    /// </remarks>
    [Fact]
    public void AgentCodingStandardsIsPinnedToTheParameterContractClientsBindBy()
    {
        MethodInfo tool = McpToolMethod("agent_coding_standards");

        Assert.Equal(typeof(string), tool.ReturnType);
        Assert.Equal(
            ["technology", "topic", "charBudget"],
            tool.GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(12_000, tool.GetParameters()[2].DefaultValue);
    }

    private static MethodInfo McpToolMethod(string toolName)
    {
        MethodInfo? method = typeof(DocsTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
                candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        Assert.True(method is not null, $"DocsTools must expose an MCP tool named '{toolName}'.");
        return method!;
    }

    private static DescriptionScore ScoreDescription(string toolName)
    {
        DescriptionAttribute? description = McpToolMethod(toolName)
            .GetCustomAttribute<DescriptionAttribute>();
        Assert.True(
            description is not null,
            $"The '{toolName}' tool must carry a [Description] written as routing metadata.");

        return DescriptionScorer.Score(new Skill
        {
            SkillFilePath = $"/tmp/{toolName}/SKILL.md",
            DirectoryPath = $"/tmp/{toolName}",
            Frontmatter = new SkillFrontmatter
            {
                Name = toolName,
                Description = description!.Description
            },
            RawFrontmatter = $"name: {toolName}\ndescription: {description!.Description}",
            InstructionsBody = "# Instructions\nQuery governed documentation."
        });
    }

    private static string PropertyValue(XDocument doc, XNamespace ns, string name) =>
        doc.Descendants(ns + "PropertyGroup")
            .SelectMany(pg => pg.Elements(ns + name))
            .Single()
            .Value;
}

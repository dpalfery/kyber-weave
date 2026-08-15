using System.ComponentModel;
using System.Reflection;
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
    /// excluded for the same reason and asserted separately, since only one of the four
    /// should own an exclusion.
    /// </remarks>
    [Theory]
    [InlineData(nameof(DocsTools.Explore), "docs_explore")]
    [InlineData(nameof(DocsTools.ForSymbol), "docs_for_symbol")]
    [InlineData(nameof(DocsTools.AnalysisCandidates), "docs_analysis_candidates")]
    [InlineData(nameof(DocsTools.Glossary), "docs_glossary")]
    public void McpToolDescriptionsScoreAsRoutingMetadata(string method, string toolName)
    {
        DescriptionScore score = ScoreDescription(method, toolName);

        Assert.Equal(35, score.Components.Single(c => c.Name == "Trigger clause").Points);
        Assert.Equal(15, score.Components.Single(c => c.Name == "Specific opening").Points);
        Assert.Equal(15, score.Components.Single(c => c.Name == "Trigger keywords").Points);
    }

    /// <summary>
    /// Exclusions must be asymmetric: when two tools share a boundary the broader one
    /// yields and the narrower states its territory positively. If every description
    /// disclaimed the overlap, requests in the middle would match nothing — so only
    /// <c>docs_explore</c>, the broadest of the four, carries a negative boundary.
    /// </summary>
    [Fact]
    public void OnlyTheBroadestMcpToolCarriesANegativeBoundary()
    {
        static int Boundary(string method, string toolName) =>
            ScoreDescription(method, toolName).Components.Single(c => c.Name == "Negative boundary").Points;

        Assert.Equal(20, Boundary(nameof(DocsTools.Explore), "docs_explore"));
        Assert.Equal(0, Boundary(nameof(DocsTools.ForSymbol), "docs_for_symbol"));
        Assert.Equal(0, Boundary(nameof(DocsTools.AnalysisCandidates), "docs_analysis_candidates"));
        Assert.Equal(0, Boundary(nameof(DocsTools.Glossary), "docs_glossary"));
    }

    /// <summary>
    /// Every tool is a pure lookup, and a client can act on <c>readOnlyHint</c> where it
    /// cannot act on prose claiming the same thing. The corpus is local, so no tool
    /// reaches an open world.
    /// </summary>
    [Theory]
    [InlineData(nameof(DocsTools.Explore))]
    [InlineData(nameof(DocsTools.ForSymbol))]
    [InlineData(nameof(DocsTools.AnalysisCandidates))]
    [InlineData(nameof(DocsTools.Glossary))]
    public void McpToolsDeclareReadOnlyAndClosedWorldAnnotations(string method)
    {
        McpServerToolAttribute attribute = typeof(DocsTools).GetMethod(method)!.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.True(attribute.ReadOnly, $"{method} must declare ReadOnly so clients see readOnlyHint.");
        Assert.False(attribute.OpenWorld, $"{method} queries a local corpus and must not claim an open world.");
    }

    private static DescriptionScore ScoreDescription(string method, string toolName)
    {
        string description = typeof(DocsTools)
            .GetMethod(method)!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;

        return DescriptionScorer.Score(new Skill
        {
            SkillFilePath = $"/tmp/{toolName}/SKILL.md",
            DirectoryPath = $"/tmp/{toolName}",
            Frontmatter = new SkillFrontmatter
            {
                Name = toolName,
                Description = description
            },
            RawFrontmatter = $"name: {toolName}\ndescription: {description}",
            InstructionsBody = "# Instructions\nQuery governed documentation."
        });
    }

    private static string PropertyValue(XDocument doc, XNamespace ns, string name) =>
        doc.Descendants(ns + "PropertyGroup")
            .SelectMany(pg => pg.Elements(ns + name))
            .Single()
            .Value;
}

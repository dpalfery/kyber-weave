using System.Xml.Linq;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T4 — MCP must ship as an installable dotnet tool with host-neutral tool descriptions.
/// </summary>
public class McpPackagingTests
{
    [Fact]
    public void McpProject_Is_Packable_As_Dotnet_Tool_With_Kyber_Weave_Mcp_Command()
    {
        var doc = XDocument.Load(KyberWeaveTestPaths.McpProjectPath);
        XNamespace ns = doc.Root!.Name.Namespace;

        Assert.Equal("true", PropertyValue(doc, ns, "PackAsTool"));
        Assert.Equal("kyber-weave-mcp", PropertyValue(doc, ns, "ToolCommandName"));
        Assert.Equal("true", PropertyValue(doc, ns, "IsPackable"));
    }

    [Fact]
    public void McpTool_Descriptions_Do_Not_Hard_Require_MotorcycleRAG()
    {
        var source = File.ReadAllText(KyberWeaveTestPaths.McpDocsToolsSourcePath);

        Assert.DoesNotContain("MotorcycleRAG", source, StringComparison.Ordinal);
    }

    [Fact]
    public void McpTool_Descriptions_Use_Generic_Repository_Documentation_Wording()
    {
        var source = File.ReadAllText(KyberWeaveTestPaths.McpDocsToolsSourcePath);

        Assert.Contains("repository documentation", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string PropertyValue(XDocument doc, XNamespace ns, string name) =>
        doc.Descendants(ns + "PropertyGroup")
            .SelectMany(pg => pg.Elements(ns + name))
            .Single()
            .Value;
}

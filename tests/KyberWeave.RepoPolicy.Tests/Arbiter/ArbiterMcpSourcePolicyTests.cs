using System.Xml.Linq;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// What the Arbiter's served MCP surface must keep true in its own source and project file: its sources never write
/// to standard output, which belongs to the stdio transport, and its MCP packages stay on the versions the docs MCP
/// project pins.
/// </summary>
public sealed class ArbiterMcpSourcePolicyTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void McpSourcesNeverWriteToStdout()
    {
        foreach (string file in Corpus.EnumerateFiles("src/KyberWeave.Arbiter/Mcp", "*.cs", recursive: false))
        {
            string source = Corpus.ReadText(file);
            Assert.DoesNotContain("Console.Write", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Console.Out", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ArbiterProjectPinsTheMcpPackagesToTheVersionsTheMcpProjectUses()
    {
        XDocument arbiter = LoadProject("KyberWeave.Arbiter");
        XDocument mcp = LoadProject("KyberWeave.Mcp");

        // Comparing the two project files is the contract, not a literal: a dependency bump
        // that moves one project and not the other fails here, which is what the name says.
        foreach (string package in new[] { "ModelContextProtocol", "Microsoft.Extensions.Hosting" })
        {
            string? mcpVersion = PackageVersion(mcp, package);
            string? arbiterVersion = PackageVersion(arbiter, package);

            Assert.True(mcpVersion is not null, $"KyberWeave.Mcp must pin '{package}'.");
            Assert.True(
                string.Equals(mcpVersion, arbiterVersion, StringComparison.Ordinal),
                $"'{package}' drifts: KyberWeave.Mcp pins {mcpVersion}, KyberWeave.Arbiter pins {arbiterVersion ?? "nothing"}.");
        }
    }

    private static XDocument LoadProject(string projectName) =>
        XDocument.Parse(Corpus.ReadText($"src/{projectName}/{projectName}.csproj"));

    private static string? PackageVersion(XDocument doc, string name)
    {
        XNamespace ns = doc.Root!.Name.Namespace;

        return doc.Descendants(ns + "PackageReference")
            .Where(element => string.Equals((string?)element.Attribute("Include"), name, StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Version"))
            .SingleOrDefault();
    }
}

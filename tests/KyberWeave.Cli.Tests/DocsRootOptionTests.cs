using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Cli.Tests;

/// <summary>
/// The <c>--docs-root</c> option of the docs commands: an unsupplied option leaves the configured roots alone, a
/// supplied one replaces them, and one that escapes the repository is reported. The checkout lives in memory.
/// </summary>
public sealed class DocsRootOptionTests
{
    private readonly DocFixture _fixture = DocFixture.CreateInMemory();

    [Fact]
    public void AnUnsuppliedDocsRootOptionLeavesTheConfiguredRootsAlone()
    {
        WriteHostConfig("ontology:\n  docs-root: [docs, automation]\n");

        Assert.True(TryResolveOntology([], out OntologyConfig ontology, out _));
        Assert.Equal(["docs", "automation"], ontology.DocsRoots);
    }

    /// <summary>
    /// The option carries no default of its own, so a value that happens to equal the
    /// product default is still an override rather than being silently discarded.
    /// </summary>
    [Fact]
    public void ARepeatedDocsRootOptionReplacesTheConfiguredRoots()
    {
        WriteHostConfig("ontology:\n  docs-root: [docs, automation]\n");

        Assert.True(TryResolveOntology(["lab", OntologyConfig.ProductDefaults.DocsRoot], out OntologyConfig ontology, out _));
        Assert.Equal(["lab", OntologyConfig.ProductDefaults.DocsRoot], ontology.DocsRoots);
    }

    [Fact]
    public void ADocsRootOptionThatEscapesTheRepositoryReportsKWCONFIG001()
    {
        WriteHostConfig("ontology:\n  docs-root: docs\n");

        Assert.False(TryResolveOntology(["../elsewhere"], out _, out DiagnosticReport report));
        Assert.Contains(report.Items, i =>
            i.Code == KyberWeaveConfigDiagnosticCodes.LoadError &&
            i.Message.Contains("escapes the repository root", StringComparison.Ordinal));
    }

    private bool TryResolveOntology(
        string[] docsRoots,
        out OntologyConfig ontology,
        out DiagnosticReport report)
    {
        report = new DiagnosticReport();
        DocsSettings settings = new DocsSettings { Path = _fixture.Root, DocsRoots = docsRoots };
        return DocsCommandComposition.TryResolveOntology(_fixture.Config(), settings, report, out ontology);
    }

    private void WriteHostConfig(string yaml) => _fixture.Write(".kyber-weave/kyber-weave.yml", yaml);
}

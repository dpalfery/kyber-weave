using System.Xml.Linq;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Holds the project references of the shared core and the entry apps to the layering in
/// <c>docs/adr</c>: Kernel at the bottom, Domain above it, Contracts over both, Data and
/// Application above those, and entry apps that never see Data.
/// </summary>
public sealed class LayerReferenceTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>Each layer and the only repository projects it may reference.</summary>
    public static TheoryData<string, string[]> LayerReferences => new()
    {
        { "KyberWeave.Kernel", [] },
        { "KyberWeave.Domain", ["KyberWeave.Kernel"] },
        { "KyberWeave.Contracts", ["KyberWeave.Domain", "KyberWeave.Kernel"] },
        { "KyberWeave.Data", ["KyberWeave.Contracts", "KyberWeave.Domain", "KyberWeave.Kernel"] },
        { "KyberWeave.Application", ["KyberWeave.Contracts", "KyberWeave.Data", "KyberWeave.Domain", "KyberWeave.Kernel"] }
    };

    /// <summary>Entry apps that exist today. Each may reference these layers and nothing else new.</summary>
    public static TheoryData<string> EntryApps => [.. EntryAppNames];

    private static readonly string[] EntryAppNames = ["KyberWeave.Cli", "KyberWeave.Mcp", "KyberWeave.Arbiter", "KyberWeave.Hooks"];

    [Theory]
    [MemberData(nameof(LayerReferences))]
    public void LayersReferenceOnlyWhatTheLayeringAllows(string project, string[] allowed)
    {
        Assert.Equal(allowed, References(project));
    }

    [Theory]
    [MemberData(nameof(EntryApps))]
    public void EntryAppsNeverReferenceData(string project)
    {
        Assert.DoesNotContain("KyberWeave.Data", References(project), StringComparer.Ordinal);
    }

    private static string[] References(string project)
    {
        string path = $"src/{project}/{project}.csproj";
        return
        [
            .. XDocument.Parse(Corpus.ReadText(path))
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
                .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
                .Order(StringComparer.Ordinal)
        ];
    }
}

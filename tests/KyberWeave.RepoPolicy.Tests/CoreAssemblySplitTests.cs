using System.Xml.Linq;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Holds the engine's project references to the split ADR 0057 records. Core references the shared layers and neither
/// feature assembly. Each feature assembly references Core and nothing else, so a Squad-to-Docs
/// edge needs a deliberate change here as well as in a project file.
/// </summary>
public sealed class CoreAssemblySplitTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>Each engine project and the only repository projects it may reference.</summary>
    public static TheoryData<string, string[]> AllowedReferences => new()
    {
        // Core reads the shared layers only; Data and Application sit above it.
        { "KyberWeave.Core", ["KyberWeave.Contracts", "KyberWeave.Domain", "KyberWeave.Kernel"] },
        { "KyberWeave.Core.Squad", ["KyberWeave.Application", "KyberWeave.Core", "KyberWeave.Data", "KyberWeave.Domain"] }
    };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void EngineProjectsReferenceOnlyWhatTheSplitAllows(string project, string[] allowed)
    {
        string path = $"src/{project}/{project}.csproj";
        string[] references =
        [
            .. XDocument.Parse(Corpus.ReadText(path))
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
                .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
                .Order(StringComparer.Ordinal)
        ];

        Assert.Equal(allowed, references);
    }
}

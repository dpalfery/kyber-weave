using System.Xml.Linq;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Holds the engine's project references to the split ADR 0057 records. Core references the shared layers and neither
/// feature assembly. Each feature assembly references Core and nothing else, so a Squad-to-Docs
/// edge needs a deliberate change here as well as in a project file.
/// </summary>
public sealed class CoreAssemblySplitTests
{
    /// <summary>Each engine project and the only repository projects it may reference.</summary>
    public static TheoryData<string, string[]> AllowedReferences => new()
    {
        { "KyberWeave.Core", ["KyberWeave.Contracts", "KyberWeave.Data", "KyberWeave.Domain", "KyberWeave.Kernel"] },
        { "KyberWeave.Core.Docs", ["KyberWeave.Core", "KyberWeave.Domain"] },
        { "KyberWeave.Core.Squad", ["KyberWeave.Core", "KyberWeave.Domain"] }
    };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void EngineProjectsReferenceOnlyWhatTheSplitAllows(string project, string[] allowed)
    {
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, "src", project, $"{project}.csproj");
        string[] references =
        [
            .. XDocument.Load(path)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
                .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
                .Order(StringComparer.Ordinal)
        ];

        Assert.Equal(allowed, references);
    }
}

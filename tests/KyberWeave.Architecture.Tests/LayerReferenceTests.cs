using System.Xml.Linq;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Holds the project references of the shared core and the entry apps to the layering in
/// <c>docs/adr</c>: Kernel at the bottom, Domain above it, Contracts over both, Data and
/// Application above those, and entry apps that never see Data.
/// </summary>
/// <remarks>
/// The legacy engine assemblies (<c>KyberWeave.Core</c>, <c>.Core.Docs</c>, <c>.Core.Squad</c>) are
/// being drained into the layers. Layers never reference them; the entry apps may, until the
/// last type has moved. <see cref="LegacyReferenceBaseline"/> can only shrink.
/// </remarks>
public sealed class LayerReferenceTests
{
    private static readonly string[] LegacyProjects = ["KyberWeave.Core", "KyberWeave.Core.Docs", "KyberWeave.Core.Squad"];

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
    public static TheoryData<string> EntryApps => new() { "KyberWeave.Cli", "KyberWeave.Mcp", "KyberWeave.Arbiter", "KyberWeave.Hooks" };

    /// <summary>Legacy references an entry app still holds. Remove an entry when the reference is dropped; never add one.</summary>
    private static readonly Dictionary<string, string[]> LegacyReferenceBaseline = new(StringComparer.Ordinal)
    {
        ["KyberWeave.Cli"] = ["KyberWeave.Core", "KyberWeave.Core.Docs", "KyberWeave.Core.Squad"],
        ["KyberWeave.Mcp"] = ["KyberWeave.Core", "KyberWeave.Core.Docs"],
        ["KyberWeave.Arbiter"] = ["KyberWeave.Core"],
        ["KyberWeave.Hooks"] = []
    };

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

    [Theory]
    [MemberData(nameof(EntryApps))]
    public void EntryAppsHoldNoLegacyReferenceBeyondTheBaseline(string project)
    {
        string[] legacy = [.. References(project).Where(r => LegacyProjects.Contains(r, StringComparer.Ordinal))];
        string[] baseline = LegacyReferenceBaseline[project];

        Assert.True(
            legacy.All(r => baseline.Contains(r, StringComparer.Ordinal)),
            $"{project} references a legacy engine project outside its baseline: {string.Join(", ", legacy.Except(baseline))}.");
    }

    private static string[] References(string project)
    {
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, "src", project, $"{project}.csproj");
        return
        [
            .. XDocument.Load(path)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
                .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
                .Order(StringComparer.Ordinal)
        ];
    }
}

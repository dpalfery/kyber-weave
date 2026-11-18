using System.Xml.Linq;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Holds the project references of the shared core and the entry apps to the layering in
/// <c>docs/adr</c>: Kernel at the bottom, Domain above it, Contracts over both, Data and
/// Application above those, and entry apps that never see Data.
/// </summary>
/// <remarks>
/// The legacy engine assemblies (<c>KyberWeave.Core</c>, <c>.Core.Squad</c>) are
/// being drained into the layers. <c>KyberWeave.Core</c> now sits on the shared layers and reads nothing above
/// them, so its references are pinned with theirs. Layers never reference either legacy assembly; the entry apps
/// may, until the last type has moved. <see cref="LegacyReferenceBaseline"/> can only shrink.
/// </remarks>
public sealed class LayerReferenceTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly string[] LegacyProjects = ["KyberWeave.Core", "KyberWeave.Core.Squad"];

    /// <summary>Each layer and the only repository projects it may reference.</summary>
    public static TheoryData<string, string[]> LayerReferences => new()
    {
        { "KyberWeave.Kernel", [] },
        { "KyberWeave.Domain", ["KyberWeave.Kernel"] },
        { "KyberWeave.Contracts", ["KyberWeave.Domain", "KyberWeave.Kernel"] },
        { "KyberWeave.Data", ["KyberWeave.Contracts", "KyberWeave.Domain", "KyberWeave.Kernel"] },
        { "KyberWeave.Application", ["KyberWeave.Contracts", "KyberWeave.Data", "KyberWeave.Domain", "KyberWeave.Kernel"] },

        // The drained engine assembly reads the shared layers only: no Data, no Application.
        { "KyberWeave.Core", ["KyberWeave.Contracts", "KyberWeave.Domain", "KyberWeave.Kernel"] }
    };

    /// <summary>Entry apps that exist today. Each may reference these layers and nothing else new.</summary>
    public static TheoryData<string> EntryApps => [.. EntryAppNames];

    private static readonly string[] EntryAppNames = ["KyberWeave.Cli", "KyberWeave.Mcp", "KyberWeave.Arbiter", "KyberWeave.Hooks"];

    /// <summary>Legacy references an entry app still holds. Remove an entry when the reference is dropped; never add one.</summary>
    private static readonly Dictionary<string, string[]> LegacyReferenceBaseline = new(StringComparer.Ordinal)
    {
        ["KyberWeave.Cli"] = ["KyberWeave.Core", "KyberWeave.Core.Squad"],
        ["KyberWeave.Mcp"] = ["KyberWeave.Core"],
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
    public void EntryAppLegacyReferencesMatchTheBaselineExactly(string project)
    {
        string[] legacy = [.. References(project).Where(r => LegacyProjects.Contains(r, StringComparer.Ordinal))];
        string[] baseline = [.. LegacyReferenceBaseline[project].Order(StringComparer.Ordinal)];

        string[] added = [.. legacy.Except(baseline, StringComparer.Ordinal)];
        string[] stale = [.. baseline.Except(legacy, StringComparer.Ordinal)];

        Assert.True(added.Length == 0, $"{project} references legacy projects outside its baseline: {string.Join(", ", added)}. Move the code instead of adding a reference.");
        Assert.True(stale.Length == 0, $"{project} no longer references {string.Join(", ", stale)}; remove it from the baseline so it cannot grow back.");
    }

    [Fact]
    public void BaselineNamesEveryEntryAppAndOnlyLegacyProjects()
    {
        Assert.Equal(EntryAppNames.Order(StringComparer.Ordinal), LegacyReferenceBaseline.Keys.Order(StringComparer.Ordinal));
        Assert.All(LegacyReferenceBaseline.Values.SelectMany(v => v), r => Assert.Contains(r, LegacyProjects));
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

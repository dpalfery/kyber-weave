using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Review.Catalogue;

namespace KyberWeave.Application.Review;

/// <summary>Renders the catalogue <c>review:</c> block for the technologies a host declares.</summary>
/// <remarks>
/// Each family's project roots are detected with that family's own root rules, over the repository files the caller
/// lists. The render reads the files a gate's condition names — a <c>package.json</c> script, the test runner in
/// <c>global.json</c> — through the facts the caller hands it, so nothing here touches the file system itself.
/// </remarks>
public static class ReviewCataloguePlanner
{
    /// <summary>Renders the block, or reports that no declared technology has a catalogue entry.</summary>
    /// <param name="repoRoot">The repository root the gate ids are composed against.</param>
    /// <param name="declaredTechnologies">The technology slugs the host declares, in order.</param>
    /// <param name="repositoryFiles">The repository-relative files, forward-slashed, as the file lister returns them.</param>
    /// <param name="facts">Reads the files a gate's condition names.</param>
    public static ReviewGatesPlan Plan(
        string repoRoot,
        IReadOnlyList<string> declaredTechnologies,
        IReadOnlyList<string> repositoryFiles,
        GateCatalogueFacts facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(declaredTechnologies);
        ArgumentNullException.ThrowIfNull(repositoryFiles);
        ArgumentNullException.ThrowIfNull(facts);

        IReadOnlyList<GateCatalogueFamily> families = GateCatalogueRenderer.LoadEmbedded();

        // No declared technology maps to a family: there is nothing to detect and nothing to write, so the repository
        // is never even walked. The render would be an empty block; the plan carries the no-family list instead.
        HashSet<string> familyTechnologies = new(StringComparer.Ordinal);
        foreach (GateCatalogueFamily family in families)
        {
            foreach (string technology in family.Technologies)
            {
                familyTechnologies.Add(technology);
            }
        }

        if (!declaredTechnologies.Any(familyTechnologies.Contains))
        {
            RenderedReviewBlock empty = new(
                string.Empty, [], [], [.. declaredTechnologies.Distinct(StringComparer.Ordinal)]);
            return new ReviewGatesPlan(empty, false);
        }

        Dictionary<string, ProjectRootSet> rootsByFamily = new(StringComparer.Ordinal);
        foreach (GateCatalogueFamily family in families)
        {
            rootsByFamily[family.Family] = ProjectRootDetection.Detect(
                repositoryFiles, [.. family.Markers], family.Nesting, family.Pair, family.Fallback?.ToArray());
        }

        RenderedReviewBlock rendered = GateCatalogueRenderer.Render(
            declaredTechnologies, rootsByFamily, repoRoot, facts);
        return new ReviewGatesPlan(rendered, true);
    }
}

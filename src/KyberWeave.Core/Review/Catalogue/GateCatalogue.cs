using KyberWeave.Data.Hooks;

namespace KyberWeave.Core.Review.Catalogue;

/// <summary>Forwards the gate-catalogue calls the Docs scaffolding still makes statically to the layered renderer.</summary>
/// <remarks>Delete once the scaffolding renders through the container.</remarks>
public static class GateCatalogue
{
    /// <inheritdoc cref="GateCatalogueRenderer.LoadEmbedded" />
    public static IReadOnlyList<GateCatalogueFamily> LoadEmbedded() => GateCatalogueRenderer.LoadEmbedded();

    /// <inheritdoc cref="GateCatalogueRenderer.ParseFamily" />
    public static GateCatalogueFamily ParseFamily(string yaml) => GateCatalogueRenderer.ParseFamily(yaml);

    /// <inheritdoc cref="GateCatalogueRenderer.Render" />
    public static RenderedReviewBlock Render(
        IReadOnlyList<string> declaredTechnologies,
        IReadOnlyDictionary<string, ProjectRootSet> rootsByFamily,
        string repositoryRoot)
    {
        PhysicalFileSystem files = new();
        return GateCatalogueRenderer.Render(
            declaredTechnologies, rootsByFamily, repositoryRoot, new GateCatalogueFacts(files.FileExists, files.ReadAllText));
    }
}

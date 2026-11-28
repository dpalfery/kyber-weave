namespace KyberWeave.Core.Configuration;

/// <summary>Resolves the ontology-derived paths of a <see cref="DocsAnalysisConfig"/>.</summary>
public static class DocsAnalysisConfigResolution
{
    /// <summary>Resolves an omitted path to the primary documentation root.</summary>
    public static string ResolveGlossaryPath(this DocsAnalysisConfig config, OntologyConfig ontology)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ontology);
        return string.IsNullOrWhiteSpace(config.GlossaryPath)
            ? ontology.DocsRoot == "."
                ? "glossary.md"
                : $"{ontology.DocsRoot.TrimEnd('/')}/glossary.md"
            : config.GlossaryPath.Replace('\\', '/');
    }

    /// <summary>Returns this analysis configuration with ontology-derived paths resolved.</summary>
    public static DocsAnalysisConfig ResolveFor(this DocsAnalysisConfig config, OntologyConfig ontology)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Clone(resolvedGlossaryPath: config.ResolveGlossaryPath(ontology));
    }
}

using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Parsing;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// CLI composition root for docs commands: constructs injectable collaborators once from
/// resolved settings. Core types never invent <see cref="DocumentLoader"/> or
/// <see cref="ICodeGraphResolver"/> implementations.
/// </summary>
internal static class DocsCommandComposition
{
    public static bool TryResolveOntology(
        DocsSettings settings,
        DiagnosticReport report,
        out OntologyConfig ontology)
    {
        if (!CommandHelpers.TryLoadConfig(settings.Path, settings.Config, report, out var config))
        {
            ontology = OntologyConfig.ProductDefaults;
            return false;
        }

        var loaded = config.Ontology;
        if (!string.Equals(settings.DocsRoot, OntologyConfig.ProductDefaults.DocsRoot, StringComparison.Ordinal)
            && !string.Equals(settings.DocsRoot, loaded.DocsRoot, StringComparison.Ordinal))
        {
            ontology = loaded.WithDocsRoot(settings.DocsRoot);
            return true;
        }

        ontology = loaded;
        return true;
    }

    public static bool TryCreateLoader(
        DocsSettings settings,
        DiagnosticReport report,
        out DocumentLoader? loader) =>
        TryCreateLoader(settings, report, out loader, out _);

    public static bool TryCreateLoader(
        DocsSettings settings,
        DiagnosticReport report,
        out DocumentLoader? loader,
        out OntologyConfig ontology)
    {
        if (!TryResolveOntology(settings, report, out ontology))
        {
            loader = null;
            return false;
        }

        loader = new DocumentLoader(settings.Path, ontology);
        return true;
    }

    public static ICodeGraphResolver CreateResolver(DocsSettings settings) =>
        CodeGraphResolverAdapter.ForRepository(settings.Path);
}

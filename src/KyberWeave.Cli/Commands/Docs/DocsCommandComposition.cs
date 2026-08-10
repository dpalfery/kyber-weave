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

        // An unsupplied option leaves the configured roots alone; a supplied one replaces
        // them outright. The flag has no default of its own, so it cannot be confused with
        // the product default and silently ignored on a repository configured otherwise.
        var loaded = config.Ontology;
        if (settings.DocsRoots.Length == 0)
        {
            ontology = loaded;
            return true;
        }

        try
        {
            ontology = loaded.WithDocsRoots(settings.DocsRoots);
            return true;
        }
        catch (ArgumentException ex)
        {
            // A rejected --docs-root is the same class of operator error as a rejected
            // docs-root in the config file, and reports under the same code.
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigLoadErrorCode,
                Severity.Error,
                ex.Message,
                "--docs-root"));
            ontology = OntologyConfig.ProductDefaults;
            return false;
        }
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

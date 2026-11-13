using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Entity-drift tier. Resolves every documented code reference against the CodeGraph
/// index so that a renamed or deleted symbol cannot leave a dangling documentation
/// reference behind.
/// </summary>
/// <remarks>
/// This is the countermeasure to the failure mode the ontology exists to prevent: prose
/// still reads correctly after a rename, so nothing surfaces the break except resolution.
/// </remarks>
public sealed class DocDriftLinter(ICodeGraphResolver resolver)
{
    public const string UnresolvedCodeRef = "KW-DOC-DRIFT-001";
    public const string UnresolvedEndpoint = "KW-DOC-DRIFT-002";
    private const string SourceRootNotIndexed = "KW-DOC-DRIFT-003";

    /// <summary>The index is older than the code, so its misses say nothing about the documentation.</summary>
    public const string StaleIndex = "KW-DOC-DRIFT-004";

    private readonly ICodeGraphResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public DiagnosticReport Validate(DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        DiagnosticReport report = new DiagnosticReport();

        if (!_resolver.IsAvailable)
        {
            report.Add(new Diagnostic(
                UnresolvedCodeRef, Severity.Critical,
                $"Drift cannot be verified: {_resolver.UnavailableReason}",
                "codegraph", null,
                "Run 'codegraph index' at the repository root (or restore the cached index in CI), and ensure the 'sqlite3' CLI is on PATH."));
            return report;
        }

        IReadOnlyList<string> routes = _resolver.AllRoutes();

        // Findings that depend on the index are held back until the index is judged: an index that lags
        // the code reports renamed or added symbols as missing, and listing each one blames the
        // documentation for what a re-index would clear.
        DiagnosticReport indexDependent = new DiagnosticReport();

        foreach (DocumentModel doc in set.Documents)
        {
            if (doc.Frontmatter.CodeRefs is not null)
            {
                foreach (string symbol in doc.Frontmatter.CodeRefs)
                {
                    ReportUnresolvedCodeRef(doc, symbol, null, indexDependent);
                }
            }

            if (doc.Frontmatter.Sections is not null)
            {
                foreach (KeyValuePair<string, DocumentSectionVocabulary> entry in doc.Frontmatter.Sections)
                {
                    if (entry.Value.CodeRefs is null)
                    {
                        continue;
                    }

                    foreach (string symbol in entry.Value.CodeRefs)
                    {
                        ReportUnresolvedCodeRef(doc, symbol, entry.Key, indexDependent);
                    }
                }
            }

            foreach (string endpoint in doc.ApiEndpoints)
            {
                if (_resolver.ResolveRoute(endpoint).Count > 0) continue;

                string? nearest = StringDistance.Nearest(endpoint, routes);
                indexDependent.Add(new Diagnostic(
                    UnresolvedEndpoint, Severity.Error,
                    $"api-endpoints entry '{endpoint}' matches no indexed route.",
                    doc.Subject, doc.RelativePath,
                    nearest is null
                        ? "Route strings are exact, including the HTTP method and the full path template, e.g. 'GET /api/me/usage'."
                        : $"Nearest indexed route: '{nearest}'."));
            }

            string? sourceRoot = doc.Frontmatter.SourceRoot;
            if (!string.IsNullOrWhiteSpace(sourceRoot) && !_resolver.HasFilesUnder(sourceRoot))
            {
                indexDependent.Add(new Diagnostic(
                    SourceRootNotIndexed, Severity.Warning,
                    $"source-root '{sourceRoot}' contains no indexed source files.",
                    doc.Subject, doc.RelativePath,
                    "The path exists but CodeGraph indexed nothing beneath it. Check that the path points at the component's source root and that the index is current."));
            }
        }

        if (indexDependent.Items.Count > 0
            && _resolver is ICodeGraphIndexFreshness freshness
            && freshness.DescribeStaleness() is { } lag)
        {
            report.Add(new Diagnostic(
                StaleIndex, Severity.Error,
                $"The CodeGraph index is older than the code ({lag}); {indexDependent.Items.Count} finding(s) that depend on it are not listed.",
                "codegraph", null,
                "Run 'codegraph sync .' (or 'kyber-weave doctor --ensure .') and run drift again; findings that remain after that are real."));
            return report;
        }

        report.AddRange(indexDependent.Items);
        return report;
    }

    private void ReportUnresolvedCodeRef(
        DocumentModel doc,
        string symbol,
        string? sectionSlug,
        DiagnosticReport report)
    {
        if (_resolver.ResolveSymbol(symbol).Count > 0)
        {
            return;
        }

        string? nearest = StringDistance.Nearest(symbol, _resolver.CandidateNames(symbol));
        string message = sectionSlug is null
            ? $"code-refs entry '{symbol}' resolves to no symbol in the CodeGraph index."
            : $"section '{sectionSlug}': code-refs entry '{symbol}' resolves to no symbol in the CodeGraph index.";
        report.Add(new Diagnostic(
            UnresolvedCodeRef,
            Severity.Error,
            message,
            doc.Subject,
            doc.RelativePath,
            nearest is null
                ? "The symbol was renamed or removed. Update the reference, or drop it if the documentation no longer covers it."
                : $"Nearest surviving symbol: '{nearest}'."));
    }
}

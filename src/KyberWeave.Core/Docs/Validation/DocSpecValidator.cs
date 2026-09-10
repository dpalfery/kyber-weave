using System.Globalization;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Schema tier of documentation validation. Needs no code index, so it runs on every
/// documentation change.
/// </summary>
public sealed class DocSpecValidator
{
    public const string MissingFrontmatter = "KW-DOC-SPEC-001";
    public const string InvalidVocabulary = "KW-DOC-SPEC-002";
    public const string MissingRequiredKey = "KW-DOC-SPEC-003";
    public const string UnknownCatalogValue = "KW-DOC-SPEC-004";
    public const string SourceRootMissing = "KW-DOC-SPEC-005";
    public const string BadReference = "KW-DOC-SPEC-006";

    /// <summary>
    /// A <c>technology</c> declaration that does not match its context: carried by a document
    /// that is not a coding standard, or naming a technology other than the folder it sits in.
    /// </summary>
    public const string MisplacedTechnology = "KW-DOC-SPEC-007";

    /// <summary>
    /// The scalar keys the required-key matrix can demand beyond the base set.
    /// </summary>
    /// <remarks>
    /// Enumerated rather than reflected so that a key a host invents in
    /// <c>ontology.required-keys</c> keeps being ignored: frontmatter tolerates unknown keys
    /// by design, and a value this type cannot read is one it cannot honestly say is missing.
    /// </remarks>
    private static readonly string[] TypedKeys = ["component", "source-root", "technology"];

    private readonly string _repoRoot;
    private readonly OntologyConfig _config;

    public DocSpecValidator(string repoRoot)
        : this(repoRoot, OntologyConfig.ProductDefaults)
    {
    }

    public DocSpecValidator(string repoRoot, OntologyConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
    }

    public DiagnosticReport Validate(DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        DiagnosticReport report = new DiagnosticReport();

        Dictionary<string, List<string>> idOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (DocumentModel doc in set.Documents)
        {
            string? id = doc.Frontmatter.Id;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!idOwners.TryGetValue(id, out List<string>? paths))
            {
                paths = [];
                idOwners[id] = paths;
            }
            paths.Add(doc.RelativePath);
        }

        HashSet<string> knownIds = new HashSet<string>(idOwners.Keys, StringComparer.Ordinal);

        foreach (DocumentModel doc in set.Documents)
        {
            ValidateDocument(doc, set, knownIds, idOwners, report);
        }

        return report;
    }

    private void ValidateDocument(
        DocumentModel doc,
        DocumentSet set,
        HashSet<string> knownIds,
        Dictionary<string, List<string>> idOwners,
        DiagnosticReport report)
    {
        if (!doc.HasFrontmatter)
        {
            report.Add(new Diagnostic(
                MissingFrontmatter, Severity.Error,
                "Document has no YAML frontmatter block.",
                doc.Subject, doc.RelativePath,
                $"Add a frontmatter block per {_config.DocsRoot}/documentation-ontology.md."));
            return;
        }

        if (doc.ParseError is not null)
        {
            report.Add(new Diagnostic(
                MissingFrontmatter, Severity.Error,
                $"Frontmatter block could not be parsed: {doc.ParseError}",
                doc.Subject, doc.RelativePath,
                "Check YAML indentation and that list values use '- ' items."));
            return;
        }

        DocumentFrontmatter fm = doc.Frontmatter;

        // --- KW-DOC-SPEC-003: base keys -------------------------------------------------
        foreach (string key in _config.BaseRequiredKeys)
        {
            if (key is "doc-type" or "status")
                continue; // handled with vocabulary checks below

            RequireValue(GetFrontmatterValue(fm, key), key, doc, report);
        }

        // --- KW-DOC-SPEC-002: closed vocabularies ---------------------------------------
        if (string.IsNullOrWhiteSpace(fm.DocType))
        {
            if (_config.IsRequiredForAll("doc-type"))
                RequireValue(fm.DocType, "doc-type", doc, report);
        }
        else if (!IsKnownDocType(fm.DocType))
        {
            report.Add(new Diagnostic(
                InvalidVocabulary, Severity.Error,
                $"doc-type '{fm.DocType}' is not in the closed vocabulary.",
                doc.Subject, doc.RelativePath,
                $"One of: {string.Join(", ", _config.DocTypes)}."));
        }

        if (string.IsNullOrWhiteSpace(fm.Status))
        {
            if (_config.IsRequiredForAll("status"))
                RequireValue(fm.Status, "status", doc, report);
        }
        else if (!IsKnownStatus(fm.Status))
        {
            report.Add(new Diagnostic(
                InvalidVocabulary, Severity.Error,
                $"status '{fm.Status}' is not in the closed vocabulary.",
                doc.Subject, doc.RelativePath,
                $"One of: {string.Join(", ", _config.Statuses)}."));
        }

        if (!string.IsNullOrWhiteSpace(fm.LastReviewed) &&
            !DateOnly.TryParseExact(fm.LastReviewed, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            report.Add(new Diagnostic(
                InvalidVocabulary, Severity.Error,
                $"last-reviewed '{fm.LastReviewed}' is not an ISO yyyy-MM-dd date.",
                doc.Subject, doc.RelativePath));
        }

        if (fm.Keywords is not null)
        {
            foreach (string? keyword in fm.Keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    report.Add(new Diagnostic(
                        InvalidVocabulary, Severity.Error,
                        $"keyword '{keyword}' is empty or whitespace.",
                        doc.Subject, doc.RelativePath,
                        "Each keyword must be a non-empty string."));
                }
            }
        }

        if (fm.Aliases is not null)
        {
            foreach (string? alias in fm.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    report.Add(new Diagnostic(
                        InvalidVocabulary, Severity.Error,
                        $"alias '{alias}' is empty or whitespace.",
                        doc.Subject, doc.RelativePath,
                        "Each keyword must be a non-empty string."));
                }
            }
        }

        // --- KW-DOC-SPEC-003: per-doc-type requirements ---------------------------------
        ValidateRequiredForType(doc, report);

        // --- KW-DOC-SPEC-002 / -007: the technology declaration -------------------------
        ValidateTechnology(doc, report);

        // --- KW-DOC-SPEC-004: catalog vocabularies --------------------------------------
        if (!string.IsNullOrWhiteSpace(fm.Component) &&
            set.Components.Count > 0 &&
            !set.Components.Contains(fm.Component))
        {
            report.Add(new Diagnostic(
                UnknownCatalogValue, Severity.Error,
                $"component '{fm.Component}' is not a Component in the catalog.",
                doc.Subject, doc.RelativePath,
                Nearest(fm.Component, set.Components) is { } nearC
                    ? $"Closest catalog component: '{nearC}'."
                    : "Add a catalog row before referencing a new component."));
        }

        if (!string.IsNullOrWhiteSpace(fm.Owner) &&
            set.Owners.Count > 0 &&
            !set.Owners.Contains(fm.Owner))
        {
            report.Add(new Diagnostic(
                UnknownCatalogValue, Severity.Error,
                $"owner '{fm.Owner}' is not an Owner in the catalog.",
                doc.Subject, doc.RelativePath,
                Nearest(fm.Owner, set.Owners) is { } nearO
                    ? $"Closest catalog owner: '{nearO}'."
                    : null));
        }

        // --- KW-DOC-SPEC-005: source-root exists ----------------------------------------
        if (!string.IsNullOrWhiteSpace(fm.SourceRoot))
        {
            string full = Path.Combine(_repoRoot, fm.SourceRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(full) && !File.Exists(full))
            {
                report.Add(new Diagnostic(
                    SourceRootMissing, Severity.Error,
                    $"source-root '{fm.SourceRoot}' does not exist.",
                    doc.Subject, doc.RelativePath,
                    "Use a repository-relative path to the component's source root."));
            }
        }

        // --- KW-DOC-SPEC-006: identity and cross-references ------------------------------
        if (!string.IsNullOrWhiteSpace(fm.Id) &&
            idOwners.TryGetValue(fm.Id, out List<string>? owners) &&
            owners.Count > 1)
        {
            report.Add(new Diagnostic(
                BadReference, Severity.Error,
                $"id '{fm.Id}' is declared by {owners.Count} documents: {string.Join(", ", owners)}.",
                doc.Subject, doc.RelativePath,
                "Document ids are unique and permanent."));
        }

        foreach (string reference in doc.DecidedBy)
        {
            if (!knownIds.Contains(reference))
            {
                report.Add(new Diagnostic(
                    BadReference, Severity.Error,
                    $"decided-by references unknown document id '{reference}'.",
                    doc.Subject, doc.RelativePath,
                    Nearest(reference, knownIds) is { } near ? $"Closest known id: '{near}'." : null));
            }
        }

        foreach (string reference in doc.Supersedes)
        {
            if (!knownIds.Contains(reference))
            {
                report.Add(new Diagnostic(
                    BadReference, Severity.Error,
                    $"supersedes references unknown document id '{reference}'.",
                    doc.Subject, doc.RelativePath,
                    Nearest(reference, knownIds) is { } near ? $"Closest known id: '{near}'." : null));
            }
        }
    }

    private void ValidateRequiredForType(DocumentModel doc, DiagnosticReport report)
    {
        DocumentFrontmatter fm = doc.Frontmatter;

        foreach (string key in TypedKeys)
        {
            if (_config.IsRequired(doc.DocType, key))
                RequireValue(GetFrontmatterValue(fm, key), key, doc, report);
        }

        // Architecture and runbook keep the source-root ↔ code-refs pairing invariant:
        // naming a source root without symbols (or symbols without a root) is incomplete.
        switch (doc.DocType)
        {
            case DocType.Architecture:
                if (!string.IsNullOrWhiteSpace(fm.SourceRoot))
                {
                    RequireList(doc.CodeRefs, "code-refs", doc, report,
                        "An architecture document with a source-root must name the symbols it describes.");
                }
                else if (doc.CodeRefs.Count > 0)
                {
                    RequireValue(fm.SourceRoot, "source-root", doc, report);
                }
                break;

            case DocType.Runbook:
                if (!string.IsNullOrWhiteSpace(fm.SourceRoot))
                {
                    RequireList(doc.CodeRefs, "code-refs", doc, report,
                        "A runbook with a source-root must name the symbols it operates, or drop source-root if it is process-only.");
                }
                break;
        }
    }

    /// <summary>
    /// Checks the <c>technology</c> declaration against the three things that must agree
    /// about it: the doc-type that takes the key, the closed vocabulary the host declared,
    /// and the folder the document sits in.
    /// </summary>
    /// <remarks>
    /// A missing declaration on a standard is left to the required-key matrix, which already
    /// reports it as <c>KW-DOC-SPEC-003</c>. What is checked here is a declaration that
    /// exists and disagrees with its surroundings — the case that produces a standard nothing
    /// can resolve, because the registry publishes a property per declared technology and
    /// points it at the folder of the same name.
    /// </remarks>
    private void ValidateTechnology(DocumentModel doc, DiagnosticReport report)
    {
        string? declared = doc.Frontmatter.Technology;
        if (string.IsNullOrWhiteSpace(declared))
            return;

        string technology = declared.Trim();

        if (doc.DocType != DocType.CodingStandard)
        {
            report.Add(new Diagnostic(
                MisplacedTechnology, Severity.Error,
                $"technology '{technology}' is declared on a '{doc.Frontmatter.DocType}' document.",
                doc.Subject, doc.RelativePath,
                "Only a coding-standard is scoped by technology. Drop the key, or make the " +
                "document a coding-standard."));
            return;
        }

        if (!_config.Technologies.Contains(technology, StringComparer.Ordinal))
        {
            report.Add(new Diagnostic(
                InvalidVocabulary, Severity.Error,
                $"technology '{technology}' is not declared by this repository.",
                doc.Subject, doc.RelativePath,
                Nearest(technology, _config.Technologies) is { } near
                    ? $"Closest declared technology: '{near}'."
                    : "Add it to ontology.technologies in .kyber-weave/kyber-weave.yml and " +
                      "re-run docs init, which creates its folder and publishes its registry property."));
            return;
        }

        string folder = ContainingFolder(doc.RelativePath);
        if (!string.Equals(folder, technology, StringComparison.Ordinal))
        {
            report.Add(new Diagnostic(
                MisplacedTechnology, Severity.Error,
                $"technology '{technology}' does not match the containing folder '{folder}'.",
                doc.Subject, doc.RelativePath,
                $"A standard for '{technology}' lives in a folder of that name — the registry " +
                "property points there."));
        }
    }

    /// <summary>Name of the directory a repository-relative path sits in, or empty.</summary>
    private static string ContainingFolder(string relativePath)
    {
        int end = relativePath.LastIndexOf('/');
        if (end <= 0) return string.Empty;

        int start = relativePath.LastIndexOf('/', end - 1);
        return relativePath[(start + 1)..end];
    }

    private bool IsKnownDocType(string value) =>
        _config.DocTypes.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private bool IsKnownStatus(string value) =>
        _config.Statuses.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string? GetFrontmatterValue(DocumentFrontmatter fm, string key) =>
        key switch
        {
            "id" => fm.Id,
            "title" => fm.Title,
            "owner" => fm.Owner,
            "last-reviewed" => fm.LastReviewed,
            "doc-type" => fm.DocType,
            "status" => fm.Status,
            "component" => fm.Component,
            "source-root" => fm.SourceRoot,
            "technology" => fm.Technology,
            _ => null
        };

    private static void RequireValue(string? value, string key, DocumentModel doc, DiagnosticReport report)
    {
        if (!string.IsNullOrWhiteSpace(value)) return;
        report.Add(new Diagnostic(
            MissingRequiredKey, Severity.Error,
            $"Required key '{key}' is missing or empty.",
            doc.Subject, doc.RelativePath));
    }

    private static void RequireList(
        IReadOnlyList<string> values, string key, DocumentModel doc, DiagnosticReport report, string? hint = null)
    {
        if (values.Count > 0) return;
        report.Add(new Diagnostic(
            MissingRequiredKey, Severity.Error,
            $"Required key '{key}' must have at least one entry for doc-type '{doc.Frontmatter.DocType}'.",
            doc.Subject, doc.RelativePath, hint));
    }

    /// <summary>Closest candidate by edit distance, when one is plausibly a typo.</summary>
    internal static string? Nearest(string value, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in candidates)
        {
            int distance = Levenshtein(value, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        if (best is null) return null;
        int threshold = Math.Max(3, value.Length / 2);
        return bestDistance <= threshold ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i < a.Length + 1; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

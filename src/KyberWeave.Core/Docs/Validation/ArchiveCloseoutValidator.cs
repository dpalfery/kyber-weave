using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Parsing;
using KyberWeave.Core.Review;
using YamlDotNet.Core;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Checks that archived work carries its closeout evidence: the merge gate that
/// <c>docs validate --merge-ready</c> runs over the change it is about to merge.
/// </summary>
/// <remarks>
/// <para>
/// Closeouts were skipped before this rule existed (D3): a plan or specification could be
/// archived — moved out of its active folder — without any record that its closeout ran,
/// so finished work looked done without ever showing its evidence. Retrieval then served
/// the archived documents as authoritative while nothing attested the work completed.
/// </para>
/// <para>
/// This validator (D4) closes that gap with <c>KW-DOC-LIFECYCLE-004</c>: an archive entry
/// the change touches must name its closeout evidence. Each entry reports at most one
/// finding, naming every condition it fails (C1–C9), so a plan that is both open and
/// unevidenced reads as one piece of unfinished work rather than a list.
/// </para>
/// </remarks>
public sealed partial class ArchiveCloseoutValidator
{
    /// <summary>Archived work without closeout evidence.</summary>
    public const string MissingCloseoutEvidence = "KW-DOC-LIFECYCLE-004";

    private static readonly string[] ClosedFrontmatterStatuses = ["ARCHIVED", "COMPLETE", "SUPERSEDED"];

    private static readonly string[] LiveBodyStatuses =
        ["DRAFT", "READY", "IN PROGRESS", "IN-PROGRESS", "BLOCKED", "REVIEW REQUIRED", "REVIEW-REQUIRED"];

    private static readonly string[] ComponentScales = ["NEW-COMPONENT", "EXTENDS-COMPONENT"];

    private const string NoDesignChangeScale = "NO-DESIGN-CHANGE";

    private const string ResidualMarker = "RESIDUAL";

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;
    private readonly Func<DocumentLoader> _loaderFactory;

    /// <summary>
    /// Creates the validator over the repository and configuration it will check. The
    /// archived-entry loader comes from the caller: Core takes the port as a factory
    /// rather than constructing it, so the composition root decides which loader runs.
    /// </summary>
    public ArchiveCloseoutValidator(
        string repoRoot,
        KyberWeaveConfig config,
        Func<DocumentLoader> loaderFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loaderFactory);
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _loaderFactory = loaderFactory;
    }

    /// <summary>
    /// One error per archived entry the change touches whose closeout evidence is
    /// incomplete. Entries the change does not touch are never checked.
    /// </summary>
    public DiagnosticReport Validate(DocumentSet live, IReadOnlyList<ChangedPath> changes)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(changes);

        DiagnosticReport report = new();
        Dictionary<string, DocumentModel> byPath = new(DocsRootPath.PathComparer);
        foreach (DocumentModel document in live.Documents)
            byPath.TryAdd(document.RelativePath, document);

        string? todoIndex = _config.ConfigReg.Resolve(_config.Ontology)
            .FirstOrDefault(e => string.Equals(e.Name, ConfigRegConfig.TodoIndexProperty, StringComparison.Ordinal))
            ?.Path;
        string todoFolder = todoIndex is null
            ? string.Empty
            : Path.GetDirectoryName(todoIndex)?.Replace('\\', '/') ?? string.Empty;

        DocumentLoader archivedLoader = _loaderFactory();
        foreach (string entry in SelectEntries(changes))
        {
            DocumentModel archived = archivedLoader.LoadArchivedDocument(entry);
            List<string> failures = CheckCloseout(archived, live, byPath, todoFolder);
            if (failures.Count == 0)
                continue;

            List<string> conditions = failures
                .Select(failure => failure[..failure.IndexOf(':', StringComparison.Ordinal)])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            report.Add(new Diagnostic(
                MissingCloseoutEvidence,
                Severity.Error,
                $"Archived '{archived.Subject}' is missing closeout evidence " +
                $"({string.Join(", ", conditions)}): {string.Join("; ", failures)}",
                archived.Subject,
                entry,
                "Record the closeout in Decisions, Closeout mapping, and Design harvest sections; " +
                "map every decision and harvest id to a live governed document or a Documentation waiver; " +
                "and carry each impact component in a freshly reviewed mapped document."));
        }

        return report;
    }

    /// <summary>
    /// The archived entries this change touches, in a stable order: changed paths under a
    /// docs-root archive container that still exist. A newly archived entry (added,
    /// renamed, copied, or untracked) is always this change's work; a modified entry is
    /// re-checked only when its harvest document declares documentation-impact, so a touch
    /// to someone else's legacy archive does not select it. Deleted paths carry no
    /// document to check.
    /// </summary>
    private List<string> SelectEntries(IReadOnlyList<ChangedPath> changes)
    {
        List<string> entries = new();
        foreach (ChangedPath change in changes)
        {
            if (change.Kind == ChangeKind.Deleted || change.Kind == ChangeKind.Unknown)
                continue;

            string relative = change.Path.Replace('\\', '/');
            if (!IsUnderArchive(relative))
                continue;

            string absolute = Path.Combine(_repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
                continue;

            bool newlyArchived = change.Kind is ChangeKind.Added or ChangeKind.Renamed
                or ChangeKind.Copied or ChangeKind.Untracked;
            if (!newlyArchived && string.IsNullOrWhiteSpace(FrontmatterImpact(absolute)))
                continue;

            entries.Add(relative);
        }

        return entries
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when <paramref name="relativePath"/> sits under an archive container.</summary>
    private bool IsUnderArchive(string relativePath)
    {
        foreach (string root in _config.Ontology.DocsRoots)
        {
            if (root == ".")
            {
                string? first = relativePath.Split('/').FirstOrDefault();
                if (string.Equals(first, OntologyConfig.ArchiveSegment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (relativePath.StartsWith(
                         root + "/" + OntologyConfig.ArchiveSegment + "/",
                         StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads only the frontmatter impact key, to decide scope without parsing the body.</summary>
    private static string? FrontmatterImpact(string absolutePath)
    {
        string raw = File.ReadAllText(absolutePath);
        FrontmatterReadResult read = MarkdownFrontmatterReader.Read(raw);
        if (!read.HasFrontmatter)
            return null;

        try
        {
            DocumentFrontmatter? frontmatter = MarkdownFrontmatterReader.Deserializer
                .Deserialize<DocumentFrontmatter>(read.Yaml);
            return frontmatter?.DocumentationImpact;
        }
        catch (YamlException)
        {
            // Unparseable frontmatter carries no readable harvest declaration, so a
            // modified legacy archive stays out of scope. Anything else — a missing or
            // unreadable file, a reader failure — propagates: hiding it would report a
            // clean run over an entry the gate never read.
            return null;
        }
    }

    /// <summary>Every failed closeout condition for one archived entry, each tagged C1–C9.</summary>
    private static List<string> CheckCloseout(
        DocumentModel archived,
        DocumentSet live,
        Dictionary<string, DocumentModel> byPath,
        string todoFolder)
    {
        List<string> failures = new();

        string? closed = CheckClosedStatus(archived);
        if (closed is not null)
            failures.Add(closed);

        string? liveBody = CheckBodyStatus(archived);
        if (liveBody is not null)
            failures.Add(liveBody);

        List<string>? decisions = DecisionIds(archived, failures);
        List<(string Ids, string Destination)>? mapping = MappingRows(archived, failures);

        string scale = (archived.Frontmatter.DocumentationImpact ?? string.Empty).Trim().ToUpperInvariant();
        IReadOnlyList<string> components = archived.Frontmatter.ImpactComponents ?? [];
        string designSection = SectionBody(archived, "Design harvest") ?? string.Empty;
        bool componentScale = ComponentScales.Contains(scale, StringComparer.Ordinal);
        bool noDesignChange = string.Equals(scale, NoDesignChangeScale, StringComparison.Ordinal);

        string? scaleFailure = CheckScale(archived, scale, componentScale, noDesignChange, components, mapping,
            designSection);
        if (scaleFailure is not null)
            failures.Add(scaleFailure);

        List<string>? harvest = HarvestIds(archived, componentScale, noDesignChange, designSection, failures);

        if (decisions is not null && harvest is not null && mapping is not null)
        {
            HashSet<string> mapped = MappedIds(archived.RelativePath, mapping, byPath);
            List<string> unmapped = decisions.Concat(harvest)
                .Where(id => !mapped.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (unmapped.Count > 0)
            {
                failures.Add(
                    $"C7: ids {string.Join(", ", unmapped)} are not mapped to a live governed document. " +
                    $"Map each id in the Closeout mapping table to a relative link that resolves to a live " +
                    $"governed document, or record a Documentation waiver for it.");
            }

            string? freshness = CheckImpactFreshness(
                archived, live, components, componentScale, mapping, byPath);
            if (freshness is not null)
                failures.Add(freshness);

            string? residual = CheckResidual(archived.RelativePath, mapping, byPath, todoFolder);
            if (residual is not null)
                failures.Add(residual);
        }

        return failures;
    }

    /// <summary>C1: an archived document is closed work, never a draft.</summary>
    private static string? CheckClosedStatus(DocumentModel archived)
    {
        string raw = (archived.Frontmatter.Status ?? string.Empty).Trim();
        if (ClosedFrontmatterStatuses.Contains(raw.ToUpperInvariant(), StringComparer.Ordinal))
            return null;

        return $"C1: frontmatter status '{raw}' is not closed work. " +
               $"An archived plan or specification must carry status archived, complete, or superseded.";
    }

    /// <summary>C2: the body must not still report a live implementation status.</summary>
    private static string? CheckBodyStatus(DocumentModel archived)
    {
        string? bodyStatus = BodyStatus(archived.Body);
        if (bodyStatus is null
            || !LiveBodyStatuses.Contains(bodyStatus.ToUpperInvariant(), StringComparer.Ordinal))
        {
            return null;
        }

        return $"C2: the body still reports live status '{bodyStatus}'. " +
               $"Close the work before archiving it, so the archive does not read as unfinished.";
    }

    /// <summary>The body's **Status:** value, when the body declares one.</summary>
    private static string? BodyStatus(string body)
    {
        foreach (string line in body.Split('\n'))
        {
            string trimmed = line.Trim();
            string? value = trimmed.StartsWith("**Status:**", StringComparison.Ordinal)
                ? trimmed["**Status:**".Length..].Trim()
                : trimmed.StartsWith("**Status**:", StringComparison.Ordinal)
                    ? trimmed["**Status**:".Length..].Trim()
                    : null;
            if (value is not null && value.Length > 0)
                return value;
        }

        return null;
    }

    /// <summary>C3: the Decisions table and the ids it declares.</summary>
    private static List<string>? DecisionIds(DocumentModel archived, List<string> failures)
    {
        List<string[]>? rows = TableRows(archived, "Decisions");
        if (rows is null || rows.Count == 0)
        {
            failures.Add(
                "C3: no Decisions table. Record each decision under a Decisions section " +
                "with an ID column whose ids match ^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*$.");
            return null;
        }

        List<string> ids = rows
            .Select(row => row[0].Trim())
            .Where(cell => cell.Length > 0)
            .SelectMany(cell => cell.Split(','))
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToList();
        List<string> invalid = ids
            .Where(id => !IdPattern().IsMatch(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0 || invalid.Count > 0)
        {
            failures.Add(
                $"C3: the Decisions table declares no valid id{(invalid.Count == 0 ? string.Empty : $" ({string.Join(", ", invalid)})")}. " +
                $"Give every decision an id matching ^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*$.");
            return null;
        }

        return ids;
    }

    /// <summary>C4: the Closeout mapping table rows, each an id list plus a destination.</summary>
    private static List<(string Ids, string Destination)>? MappingRows(DocumentModel archived, List<string> failures)
    {
        List<string[]>? rows = TableRows(archived, "Closeout mapping");
        if (rows is null || rows.Count == 0 || rows.Any(row => row.Length < 2))
        {
            failures.Add(
                "C4: no Closeout mapping table. Map every decision and harvest id to its lasting " +
                "record under a Closeout mapping section with ID and Destination columns.");
            return null;
        }

        return rows
            .Select(row => (Ids: row[0].Trim(), Destination: row[1].Trim()))
            .ToList();
    }

    /// <summary>
    /// C5: the declared authoring scale, with components for a component scale and a
    /// Documentation waiver reason when nothing survives.
    /// </summary>
    private static string? CheckScale(
        DocumentModel archived,
        string scale,
        bool componentScale,
        bool noDesignChange,
        IReadOnlyList<string> components,
        List<(string Ids, string Destination)>? mapping,
        string designSection)
    {
        if (!componentScale && !noDesignChange)
        {
            return $"C5: documentation-impact '{archived.Frontmatter.DocumentationImpact}' names no authoring scale. " +
                   $"Declare new-component, extends-component, or no-design-change.";
        }

        if (componentScale)
        {
            List<string> named = components
                .Where(component => !string.IsNullOrWhiteSpace(component))
                .Select(component => component.Trim())
                .ToList();
            if (named.Count == 0)
            {
                return $"C5: scale '{scale}' touches components but impact-components names none. " +
                       $"List every component the change touches.";
            }

            return null;
        }

        bool waived = designSection.Contains("documentation waiver", StringComparison.OrdinalIgnoreCase)
                      || (mapping ?? []).Any(row =>
                          row.Destination.Contains("documentation waiver", StringComparison.OrdinalIgnoreCase));
        if (!waived)
        {
            return "C5: scale 'no-design-change' claims nothing survives but records no Documentation waiver reason. " +
                   "Record why nothing survives in the Design harvest section.";
        }

        return null;
    }

    /// <summary>C6: the Design harvest table, or the waiver section when nothing survives.</summary>
    private static List<string>? HarvestIds(
        DocumentModel archived,
        bool componentScale,
        bool noDesignChange,
        string designSection,
        List<string> failures)
    {
        if (noDesignChange)
        {
            if (designSection.Length == 0)
            {
                failures.Add("C6: no Design harvest section. Record the Documentation waiver reason there.");
                return null;
            }

            return [];
        }

        if (!componentScale)
            return [];

        List<string[]>? rows = TableRows(archived, "Design harvest");
        if (rows is null || rows.Count == 0)
        {
            failures.Add(
                "C6: no Design harvest table. Record each surviving design decision under a Design harvest " +
                "section with an ID column.");
            return null;
        }

        return rows
            .Select(row => row[0].Trim())
            .Where(cell => cell.Length > 0)
            .SelectMany(cell => cell.Split(','))
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToList();
    }

    /// <summary>
    /// C7: the ids one mapping row covers. A row maps its comma-separated ids when its
    /// destination starts with a Documentation waiver marker or links a live governed
    /// document. A waiver buried after other text is prose, not a waiver: only a leading
    /// marker excuses the link check, so an unresolvable link with trailing waiver
    /// words still maps nothing. A hyphenated range is one token, not an expansion;
    /// RESIDUAL maps nothing by itself; an ADR waiver excuses the lasting record,
    /// not the mapping.
    /// </summary>
    private static HashSet<string> MappedIds(
        string entryPath,
        List<(string Ids, string Destination)> mapping,
        Dictionary<string, DocumentModel> byPath)
    {
        HashSet<string> mapped = new(StringComparer.Ordinal);
        foreach ((string ids, string destination) in mapping)
        {
            bool waived = IsDocumentationWaiver(destination);
            bool linked = !waived && ResolvesToLiveDocument(entryPath, destination, byPath) is not null;
            if (!waived && !linked)
                continue;

            foreach (string token in ids.Split(','))
            {
                string id = token.Trim();
                if (id.Length > 0
                    && !string.Equals(id, ResidualMarker, StringComparison.OrdinalIgnoreCase))
                {
                    mapped.Add(id);
                }
            }
        }

        return mapped;
    }

    /// <summary>
    /// True when a mapping destination leads with a Documentation waiver marker. Leading
    /// markdown emphasis is skipped, so a bold <c>**Documentation waiver — …**</c> cell
    /// still reads as a waiver; a marker buried after other text stays prose.
    /// </summary>
    private static bool IsDocumentationWaiver(string destination) =>
        destination.TrimStart().TrimStart('*', '_')
            .StartsWith("documentation waiver", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first live governed document a destination links, if any.</summary>
    private static DocumentModel? ResolvesToLiveDocument(
        string entryPath,
        string destination,
        Dictionary<string, DocumentModel> byPath)
    {
        foreach (string link in DocumentLoader.ExtractRelativeLinks(destination))
        {
            string? target = DocGraphProjection.ResolveLink(entryPath, link);
            if (target is null)
                continue;

            if (!byPath.TryGetValue(target, out DocumentModel? document))
                byPath.TryGetValue($"{target}/README.md", out document);

            if (document is not null && !InventoryReachability.IsArchived(document.RelativePath))
                return document;
        }

        return null;
    }

    /// <summary>
    /// C8: every impact component is a catalog row, and a freshly reviewed mapped
    /// document carries it — reviewed no earlier than the harvest itself. A mapping with
    /// no linked documents is fully waived, so there is no lasting record to be stale.
    /// </summary>
    private static string? CheckImpactFreshness(
        DocumentModel archived,
        DocumentSet live,
        IReadOnlyList<string> components,
        bool componentScale,
        List<(string Ids, string Destination)> mapping,
        Dictionary<string, DocumentModel> byPath)
    {
        if (!componentScale)
            return null;

        List<DocumentModel> linked = mapping
            .Select(row => ResolvesToLiveDocument(archived.RelativePath, row.Destination, byPath))
            .Where(document => document is not null)
            .Cast<DocumentModel>()
            .ToList();

        List<string> named = components
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .Select(component => component.Trim())
            .ToList();
        foreach (string component in named)
        {
            if (!live.Components.Contains(component))
            {
                return $"C8: impact component '{component}' is not a catalog row. " +
                       $"Name only components the catalog lists.";
            }

            if (linked.Count == 0)
                continue;

            bool fresh = linked.Any(document =>
                string.Equals(document.Frontmatter.Component, component, StringComparison.Ordinal)
                && IsFresh(document.Frontmatter.LastReviewed, archived.Frontmatter.LastReviewed));
            if (!fresh)
            {
                return $"C8: no freshly reviewed mapped document carries component '{component}'. " +
                       $"Map the work to a live governed document with that component whose last-reviewed " +
                       $"is no earlier than the harvest's.";
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="target"/> was reviewed no earlier than the harvest.</summary>
    private static bool IsFresh(string? target, string? harvest)
    {
        if (!DateOnly.TryParse(target, out DateOnly targetDate)
            || !DateOnly.TryParse(harvest, out DateOnly harvestDate))
        {
            return false;
        }

        return targetDate >= harvestDate;
    }

    /// <summary>
    /// C9: a RESIDUAL marker tracks nothing by itself — it must link an existing document
    /// inside the todo-index folder.
    /// </summary>
    private static string? CheckResidual(
        string entryPath,
        List<(string Ids, string Destination)> mapping,
        Dictionary<string, DocumentModel> byPath,
        string todoFolder)
    {
        foreach ((string Ids, string Destination) row in mapping)
        {
            if (!row.Destination.Contains(ResidualMarker, StringComparison.OrdinalIgnoreCase))
                continue;

            bool tracked = false;
            foreach (string link in DocumentLoader.ExtractRelativeLinks(row.Destination))
            {
                string? target = DocGraphProjection.ResolveLink(entryPath, link);
                if (target is null)
                    continue;

                if (!byPath.TryGetValue(target, out DocumentModel? document))
                    byPath.TryGetValue($"{target}/README.md", out document);

                if (document is not null
                    && !InventoryReachability.IsArchived(document.RelativePath)
                    && InventoryReachability.IsWithin(document.RelativePath, todoFolder))
                {
                    tracked = true;
                    break;
                }
            }

            if (!tracked)
            {
                return "C9: RESIDUAL tracks nothing without a link to an existing document in the todo-index folder. " +
                       "Link the follow-up todo the residual work waits on, so the marker is tracked.";
            }
        }

        return null;
    }

    /// <summary>The body of the section headed <paramref name="heading"/>, if the body has one.</summary>
    private static string? SectionBody(DocumentModel archived, string heading)
    {
        return archived.Sections
            .FirstOrDefault(section => string.Equals(section.Heading, heading, StringComparison.OrdinalIgnoreCase))
            ?.Body;
    }

    /// <summary>Data rows of the first pipe table under <paramref name="heading"/>, if any.</summary>
    private static List<string[]>? TableRows(DocumentModel archived, string heading)
    {
        DocumentSection? section = archived.Sections
            .FirstOrDefault(candidate => string.Equals(candidate.Heading, heading, StringComparison.OrdinalIgnoreCase));
        if (section is null)
            return null;

        List<string> lines = section.Body
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();
        List<string> tableLines = new();
        bool inFence = false;
        foreach (string line in lines)
        {
            // Fenced code blocks hold examples, not evidence: the same fence rule as
            // SplitSections, so a documented table shape never counts as a table.
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && line.Length > 0 && line[0] == '|')
                tableLines.Add(line);
        }

        lines = tableLines;
        if (lines.Count < 2)
            return null;

        List<string[]> rows = new();
        for (int index = 0; index < lines.Count; index++)
        {
            List<string> cells = lines[index].Split('|')
                .Select(cell => cell.Trim())
                .ToList();
            if (cells.Count > 0 && cells[0].Length == 0)
                cells.RemoveAt(0);
            if (cells.Count > 0 && cells[^1].Length == 0)
                cells.RemoveAt(cells.Count - 1);

            // The separator row (| --- | --- |) shapes the table; it is not evidence.
            if (cells.Count > 0 && cells.All(IsSeparatorCell))
                continue;

            // The header names the columns; what the gate checks is that rows follow it.
            if (index == 0)
                continue;

            rows.Add(cells.ToArray());
        }

        return rows;
    }

    private static bool IsSeparatorCell(string cell)
    {
        string trimmed = cell.Trim(':');
        return trimmed.Length >= 3 && trimmed.All(character => character == '-');
    }

    [GeneratedRegex("^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*$")]
    private static partial Regex IdPattern();
}

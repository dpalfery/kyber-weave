using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>
/// Section inventory rows, inbound link labels, and graph nodes shared by retrieval and export.
/// </summary>
public sealed class DocumentSectionGraph
{
    private static readonly HashSet<string> GenericLinkText = new(StringComparer.OrdinalIgnoreCase)
    {
        "here",
        "this",
        "link"
    };

    private static readonly Regex MarkdownLink = new(
        @"\[(?<text>[^\]]*)\]\((?<target>[^)]+)\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private DocumentSectionGraph(
        IReadOnlyDictionary<string, IReadOnlyList<string>> inboundLabelsByLeafId,
        IReadOnlyDictionary<string, DocumentModel> documentsByPath,
        IReadOnlyList<IndexRowEdge> indexRows,
        DocGraphContribution contribution)
    {
        InboundLabelsByLeafId = inboundLabelsByLeafId;
        DocumentsByPath = documentsByPath;
        IndexRows = indexRows;
        Contribution = contribution;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> InboundLabelsByLeafId { get; }

    public IReadOnlyDictionary<string, DocumentModel> DocumentsByPath { get; }

    public IReadOnlyList<IndexRowEdge> IndexRows { get; }

    public DocGraphContribution Contribution { get; }

    public static DocumentSectionGraph Build(IReadOnlyList<DocumentModel> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        Dictionary<string, DocumentModel> byPath = new(StringComparer.Ordinal);
        foreach (DocumentModel document in documents)
        {
            byPath.TryAdd(document.RelativePath, document);
        }

        Dictionary<string, List<string>> inbound = new(StringComparer.Ordinal);
        List<IndexRowEdge> indexRows = new();
        List<DocGraphNode> nodes = new();
        List<DocGraphEdge> edges = new();
        HashSet<string> nodeIds = new(StringComparer.Ordinal);

        foreach (DocumentModel document in documents)
        {
            string documentNodeId = DocNodeId(document);
            foreach (DocumentSection section in document.Sections)
            {
                string sectionNodeId = SectionNodeId(document, section.Heading);
                AddSectionNode(nodes, nodeIds, sectionNodeId, document, section);
                edges.Add(new DocGraphEdge(
                    "HAS_SECTION",
                    documentNodeId,
                    sectionNodeId));

                if (IndexSectionCatalog.IsIndexSection(document.RelativePath, section.Heading))
                {
                    foreach (IndexRowEdge row in ParseIndexRows(document, section, byPath))
                    {
                        indexRows.Add(row);
                        if (byPath.TryGetValue(row.TargetRelativePath, out DocumentModel? target))
                        {
                            edges.Add(new DocGraphEdge(
                                "INDEX_ROW",
                                sectionNodeId,
                                DocNodeId(target),
                                EdgeProperties(row.RowTitle, row.Line)));
                        }
                    }
                }
            }

            foreach (BodyLink link in document.LinkTargets)
            {
                AddLinkEdges(document, link, byPath, nodes, nodeIds, edges, inbound);
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> inboundSnapshot =
            inbound.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)new ReadOnlyCollection<string>(pair.Value.ToArray()),
                StringComparer.Ordinal);

        return new DocumentSectionGraph(
            inboundSnapshot,
            byPath,
            indexRows,
            new DocGraphContribution(nodes, edges));
    }

    internal IndexRowEdge? SelectBestIndexRow(
        DocumentModel sourceDocument,
        string? sectionHeading,
        string query,
        IReadOnlyDictionary<string, double> queryVector)
    {
        if (sectionHeading is null
            || !IndexSectionCatalog.IsIndexSection(sourceDocument.RelativePath, sectionHeading))
        {
            return null;
        }

        IndexRowEdge? best = null;
        double bestScore = 0;
        foreach (IndexRowEdge row in IndexRows)
        {
            if (!string.Equals(row.SourceRelativePath, sourceDocument.RelativePath, StringComparison.Ordinal)
                || !string.Equals(row.SectionHeading, sectionHeading, StringComparison.Ordinal))
            {
                continue;
            }

            double score = ScoreIndexRow(row, query, queryVector);
            if (best is null || score > bestScore)
            {
                best = row;
                bestScore = score;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private double ScoreIndexRow(
        IndexRowEdge row,
        string query,
        IReadOnlyDictionary<string, double> queryVector)
    {
        double score = 0;
        score += CoverageLabel(row.RowTitle, query, queryVector);
        if (DocumentsByPath.TryGetValue(row.TargetRelativePath, out DocumentModel? target))
        {
            score += CoverageLabel(target.Frontmatter.Title ?? string.Empty, query, queryVector);
            if (!string.IsNullOrWhiteSpace(target.Frontmatter.Id))
            {
                score += CoverageLabel(target.Frontmatter.Id, query, queryVector);
            }
        }

        if (!string.IsNullOrWhiteSpace(row.Status))
        {
            score += CoverageLabel(row.Status, query, queryVector);
        }

        if (string.Equals(query, row.RowTitle, StringComparison.OrdinalIgnoreCase))
        {
            score += 4.0;
        }

        return score;
    }

    private static double CoverageLabel(
        string label,
        string query,
        IReadOnlyDictionary<string, double> queryVector)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return 0;
        }

        if (string.Equals(query, label, StringComparison.OrdinalIgnoreCase))
        {
            return 3.0;
        }

        Dictionary<string, double> labelVector = TextVectorizer.VectorizeFused(label);
        double sum = 0;
        foreach ((string term, double weight) in queryVector)
        {
            if (labelVector.TryGetValue(term, out double labelWeight))
            {
                sum += Math.Min(weight, labelWeight);
            }
        }

        return sum;
    }

    private static void AddLinkEdges(
        DocumentModel source,
        BodyLink link,
        IReadOnlyDictionary<string, DocumentModel> byPath,
        List<DocGraphNode> nodes,
        HashSet<string> nodeIds,
        List<DocGraphEdge> edges,
        Dictionary<string, List<string>> inbound)
    {
        string combined = link.Path;
        if (string.IsNullOrEmpty(combined))
        {
            combined = string.IsNullOrEmpty(link.Fragment) ? string.Empty : $"#{link.Fragment}";
        }
        else if (!string.IsNullOrEmpty(link.Fragment))
        {
            combined = string.Create(CultureInfo.InvariantCulture, $"{link.Path}#{link.Fragment}");
        }

        string? resolved = DocGraphProjection.ResolveLink(source.RelativePath, combined);
        if (resolved is null)
        {
            return;
        }

        if (!byPath.TryGetValue(resolved, out DocumentModel? target))
        {
            return;
        }

        string targetLeafId = ResolveLeafId(target, link.Fragment);
        if (IsUsableInboundLinkLabel(link.Text, target))
        {
            if (!inbound.TryGetValue(targetLeafId, out List<string>? labels))
            {
                labels = [];
                inbound[targetLeafId] = labels;
            }

            if (!labels.Contains(link.Text, StringComparer.Ordinal))
            {
                labels.Add(link.Text);
            }
        }

        string fromSectionId = SectionNodeIdForLinkSource(source, link.Line);
        string toSectionId = SectionNodeIdForFragment(target, link.Fragment);
        if (string.Equals(fromSectionId, toSectionId, StringComparison.Ordinal))
        {
            return;
        }

        AddSectionNodeIfMissing(nodes, nodeIds, fromSectionId, source, link.Line);
        AddSectionNodeIfMissing(nodes, nodeIds, toSectionId, target, link.Fragment);
        edges.Add(new DocGraphEdge(
            "SECTION_LINK",
            fromSectionId,
            toSectionId,
            EdgeProperties(link.Text, link.Line)));
    }

    private static IReadOnlyDictionary<string, string?> EdgeProperties(string linkText, int line)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["linkText"] = linkText,
            ["line"] = line.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static IEnumerable<IndexRowEdge> ParseIndexRows(
        DocumentModel document,
        DocumentSection section,
        IReadOnlyDictionary<string, DocumentModel> byPath)
    {
        List<string[]>? rows = DocumentSectionTable.ReadDataRows(section.Body);
        if (rows is null)
        {
            yield break;
        }

        int lineOffset = section.LineNumber;
        int rowIndex = 0;
        foreach (string[] cells in rows)
        {
            if (cells.Length == 0)
            {
                continue;
            }

            Match match = MarkdownLink.Match(cells[0]);
            if (!match.Success)
            {
                rowIndex++;
                continue;
            }

            string rowTitle = match.Groups["text"].Value.Trim();
            string targetRaw = match.Groups["target"].Value.Trim();
            string path = targetRaw;
            string fragment = string.Empty;
            int hash = targetRaw.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                path = targetRaw[..hash];
                fragment = targetRaw[(hash + 1)..];
            }

            string? resolved = DocGraphProjection.ResolveLink(document.RelativePath, path);
            if (resolved is null || !byPath.ContainsKey(resolved))
            {
                rowIndex++;
                continue;
            }

            string? status = cells.Length > 1 ? StripMarkdown(cells[1]) : null;
            int line = lineOffset + rowIndex + 1;
            yield return new IndexRowEdge(
                document.RelativePath,
                section.Heading,
                line,
                rowTitle,
                status,
                resolved,
                string.IsNullOrEmpty(fragment) ? null : fragment);
            rowIndex++;
        }
    }

    private static string StripMarkdown(string cell)
    {
        string trimmed = cell.Trim();
        Match match = MarkdownLink.Match(trimmed);
        return match.Success ? match.Groups["text"].Value.Trim() : trimmed;
    }

    private static bool IsUsableInboundLinkLabel(string text, DocumentModel target)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (GenericLinkText.Contains(trimmed))
        {
            return false;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal) || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(trimmed, target.Frontmatter.Title, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(trimmed, target.Frontmatter.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(trimmed, target.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string ResolveLeafId(DocumentModel document, string fragment)
    {
        string docId = document.Frontmatter.Id ?? document.RelativePath;
        if (string.IsNullOrEmpty(fragment))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{docId}#");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{docId}#{fragment}");
    }

    private static string DocNodeId(DocumentModel document)
    {
        return string.Create(CultureInfo.InvariantCulture, $"doc:{document.Subject}");
    }

    private static string SectionNodeId(DocumentModel document, string heading)
    {
        return string.Create(CultureInfo.InvariantCulture, $"section:{document.Subject}:{Slugify(heading)}");
    }

    private static string SectionNodeIdForLinkSource(DocumentModel document, int line)
    {
        DocumentSection? section = document.Sections
            .LastOrDefault(candidate => candidate.LineNumber <= line);
        string heading = section?.Heading ?? "preamble";
        return SectionNodeId(document, heading);
    }

    private static string SectionNodeIdForFragment(DocumentModel document, string fragment)
    {
        if (string.IsNullOrEmpty(fragment))
        {
            return SectionNodeId(document, "preamble");
        }

        return string.Create(CultureInfo.InvariantCulture, $"section:{document.Subject}:{fragment}");
    }

    private static void AddSectionNode(
        List<DocGraphNode> nodes,
        HashSet<string> nodeIds,
        string id,
        DocumentModel document,
        DocumentSection section)
    {
        if (!nodeIds.Add(id))
        {
            return;
        }

        nodes.Add(new DocGraphNode(
            id,
            "Section",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["path"] = document.RelativePath,
                ["heading"] = section.Heading,
                ["line"] = section.LineNumber.ToString(CultureInfo.InvariantCulture)
            }));
    }

    private static void AddSectionNodeIfMissing(
        List<DocGraphNode> nodes,
        HashSet<string> nodeIds,
        string id,
        DocumentModel document,
        int line)
    {
        if (nodeIds.Contains(id))
        {
            return;
        }

        DocumentSection? section = document.Sections
            .LastOrDefault(candidate => candidate.LineNumber <= line);
        if (section is null)
        {
            return;
        }

        AddSectionNode(nodes, nodeIds, id, document, section);
    }

    private static void AddSectionNodeIfMissing(
        List<DocGraphNode> nodes,
        HashSet<string> nodeIds,
        string id,
        DocumentModel document,
        string fragment)
    {
        if (nodeIds.Contains(id))
        {
            return;
        }

        DocumentSection? section = document.Sections.FirstOrDefault(candidate =>
            string.Equals(Slugify(candidate.Heading), fragment, StringComparison.OrdinalIgnoreCase));
        if (section is null && document.Sections.Count > 0)
        {
            section = document.Sections[0];
        }

        if (section is null)
        {
            return;
        }

        AddSectionNode(nodes, nodeIds, id, document, section);
    }

    private static string Slugify(string heading)
    {
        StringBuilder builder = new();
        bool lastDash = false;
        foreach (char ch in heading.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastDash = false;
            }
            else if (!lastDash)
            {
                builder.Append('-');
                lastDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}

/// <summary>One inventory-table row pointing at a target document.</summary>
public sealed record IndexRowEdge(
    string SourceRelativePath,
    string SectionHeading,
    int Line,
    string RowTitle,
    string? Status,
    string TargetRelativePath,
    string? TargetFragment);

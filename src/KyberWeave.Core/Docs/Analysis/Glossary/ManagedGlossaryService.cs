using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Parsing;
using Markdig;
using Markdig.Syntax;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Docs.Analysis.Glossary;

/// <summary>
/// Previews, merges, validates, and reads the one managed documentation glossary.
/// </summary>
/// <remarks>
/// A row is human-owned as soon as it is reviewed or differs from the generated row
/// fingerprint. The merger therefore removes only proposals it can prove it generated
/// unchanged, and never writes any source document other than the configured glossary.
/// </remarks>
public sealed class ManagedGlossaryService
{
    public const string ValidationRuleCode = "KW-DOC-GLOSSARY-001";

    private const string Header = "| Sense ID | Status | Definition | Scope | Aliases |";
    private const string Separator = "|---|---|---|---|---|";
    private const string EvidenceStart = "<!-- kyber-weave:glossary-evidence:start";
    private const string EvidenceEnd = "<!-- kyber-weave:glossary-evidence:end -->";
    private static readonly ISerializer FrontmatterSerializer = new SerializerBuilder().Build();
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .Build();

    private readonly string _repositoryRoot;
    private readonly KyberWeaveConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly string _relativePath;
    private readonly string _filePath;

    public ManagedGlossaryService(
        string repositoryRoot,
        KyberWeaveConfig config,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _config = config;
        _timeProvider = timeProvider;
        _relativePath = config.DocsAnalysis.ResolveGlossaryPath(config.Ontology);
        _filePath = ResolveContainedPath(_repositoryRoot, _relativePath);
        RejectLinkedPath(_repositoryRoot, _filePath);
    }

    /// <summary>Builds the exact Markdown a write would produce without changing disk.</summary>
    public GlossaryUpdateResult Preview(IReadOnlyList<GlossaryProposal> proposals) =>
        Merge(proposals, write: false);

    /// <summary>Atomically writes the glossary when the conservative merge changes it.</summary>
    public GlossaryUpdateResult Write(IReadOnlyList<GlossaryProposal> proposals) =>
        Merge(proposals, write: true);

    /// <summary>Validates the configured glossary's managed structure and approved scopes.</summary>
    public DiagnosticReport Validate()
    {
        var diagnostics = new DiagnosticReport();
        if (!File.Exists(_filePath)) return diagnostics;

        string markdown;
        try
        {
            markdown = File.ReadAllText(_filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error($"The managed glossary could not be read: {exception.Message}"));
            return diagnostics;
        }

        var document = Parse(markdown);
        ValidateFrontmatter(document, diagnostics);
        var catalog = ReadCatalog();

        foreach (var section in document.Sections)
        {
            ValidateSectionStructure(section, diagnostics);
            if (!section.HasTable)
            {
                diagnostics.Add(Error($"Glossary term '{section.Term}' must contain the managed sense table."));
                continue;
            }

            foreach (var row in section.Rows)
            {
                if (!TryParseStatus(row.StatusText, out var status))
                {
                    diagnostics.Add(Error(
                        $"Glossary sense '{row.Id}' has unknown status '{row.StatusText}'. " +
                        "Use proposed, approved, or rejected."));
                    continue;
                }

                if (status == GlossarySenseStatus.Approved)
                {
                    ValidateApprovedSense(row, catalog.Components, diagnostics);
                }
            }
        }

        return diagnostics;
    }

    /// <summary>Loads approved senses for analysis and every sense for lookup.</summary>
    public ManagedGlossaryLoadResult Load()
    {
        if (!File.Exists(_filePath))
        {
            return new ManagedGlossaryLoadResult(new AnalysisGlossary([]), []);
        }

        var diagnostics = Validate();
        if (diagnostics.HasErrors)
        {
            throw new InvalidDataException(
                $"{ValidationRuleCode}: The managed glossary is invalid: " +
                string.Join(" ", diagnostics.Items.Select(item => item.Message)));
        }

        var document = Parse(File.ReadAllText(_filePath));
        var terms = document.Sections.Select(section => new GlossaryLookupResult(
            section.Term,
            section.Rows.Select(row => ToSense(row, section.Evidence)).ToArray())).ToArray();
        var approved = document.Sections
            .SelectMany(section => section.Rows.Select(row => (section.Term, Row: row)))
            .Where(item => TryParseStatus(item.Row.StatusText, out var status)
                && status == GlossarySenseStatus.Approved)
            .Select(item => new ApprovedGlossarySense(
                item.Row.Id,
                item.Term,
                item.Row.Definition,
                item.Row.Scopes,
                item.Row.Aliases))
            .ToArray();
        return new ManagedGlossaryLoadResult(new AnalysisGlossary(approved), terms);
    }

    /// <summary>Returns all senses for a term using case-insensitive term matching.</summary>
    public GlossaryLookupResult Lookup(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        var normalized = term.Trim().ToLowerInvariant();
        return Load().Terms.FirstOrDefault(candidate =>
                   StringComparer.OrdinalIgnoreCase.Equals(candidate.Term, normalized))
               ?? new GlossaryLookupResult(normalized, []);
    }

    private GlossaryUpdateResult Merge(IReadOnlyList<GlossaryProposal> proposals, bool write)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ValidateProposals(proposals);

        var exists = File.Exists(_filePath);
        var original = exists ? File.ReadAllText(_filePath) : string.Empty;
        if (!exists && proposals.Count == 0)
        {
            return new GlossaryUpdateResult(_relativePath, string.Empty, false, false, new DiagnosticReport());
        }

        var initial = exists ? original : CreateDocument(FirstCatalogOwner());
        var merged = MergeDocument(initial, proposals);
        var changed = !StringComparer.Ordinal.Equals(original, merged);
        if (exists && changed)
        {
            merged = DemoteToNeedsReview(merged);
        }

        var diagnostics = ValidateMarkdown(merged);
        if (diagnostics.HasErrors)
        {
            throw new InvalidDataException(
                $"{ValidationRuleCode}: Refusing to write an invalid managed glossary: " +
                string.Join(" ", diagnostics.Items.Select(item => item.Message)));
        }

        var written = false;
        if (write && changed)
        {
            AtomicWrite(merged);
            written = true;
        }

        return new GlossaryUpdateResult(_relativePath, merged, changed, written, diagnostics);
    }

    private string MergeDocument(string markdown, IReadOnlyList<GlossaryProposal> proposals)
    {
        var newline = markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hadFinalNewline = markdown.EndsWith(newline, StringComparison.Ordinal);
        var lines = SplitLines(markdown);
        var document = Parse(markdown);
        var proposalsByTerm = proposals
            .GroupBy(proposal => proposal.Term.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var section in document.Sections.OrderByDescending(section => section.StartLine))
        {
            proposalsByTerm.Remove(section.Term, out var termProposals);
            var replacement = MergeSection(
                lines.GetRange(section.StartLine, section.EndLine - section.StartLine),
                section.Term,
                termProposals ?? []);
            lines.RemoveRange(section.StartLine, section.EndLine - section.StartLine);
            lines.InsertRange(section.StartLine, replacement);
        }

        foreach (var entry in proposalsByTerm.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add(string.Empty);
            lines.AddRange(NewTermSection(entry.Key, entry.Value));
        }

        var result = string.Join(newline, lines);
        return hadFinalNewline || result.Length > 0 ? result + newline : result;
    }

    private static List<string> MergeSection(
        List<string> sectionLines,
        string term,
        IReadOnlyList<GlossaryProposal> proposals)
    {
        var section = ParseSection(term, sectionLines, 0, sectionLines.Count);
        if (!section.HasTable)
        {
            if (proposals.Count == 0) return sectionLines;
            if (sectionLines.Count > 0 && sectionLines[^1].Length != 0) sectionLines.Add(string.Empty);
            sectionLines.Add(Header);
            sectionLines.Add(Separator);
            foreach (var proposal in proposals)
            {
                var id = SenseId(proposal);
                sectionLines.Add(RowMarkdown(id, proposal));
                sectionLines.Add(string.Empty);
                sectionLines.AddRange(EvidenceLines(id, proposal));
            }

            return sectionLines;
        }

        var removals = new HashSet<int>();
        var replacements = new Dictionary<int, string>();
        var evidenceToAppend = new List<string>();
        var used = new HashSet<GlossaryProposal>();

        foreach (var row in section.Rows)
        {
            if (!StringComparer.Ordinal.Equals(row.StatusText, "proposed")
                || !section.Evidence.TryGetValue(row.Id, out var evidence))
            {
                continue;
            }

            var untouched = IsUntouchedGenerated(row, evidence);
            var proposal = proposals.FirstOrDefault(candidate =>
                !used.Contains(candidate) && SameScopes(row.Scopes, candidate.Scopes));
            if (proposal is not null && untouched)
            {
                replacements[row.LineIndex] = RowMarkdown(row.Id, proposal);
                for (var index = evidence.StartLine; index < evidence.EndLine; index++) removals.Add(index);
                evidenceToAppend.AddRange(EvidenceLines(row.Id, proposal));
                used.Add(proposal);
            }
            else if (proposal is null && untouched)
            {
                removals.Add(row.LineIndex);
                for (var index = evidence.StartLine; index < evidence.EndLine; index++) removals.Add(index);
            }
        }

        var insertAt = section.Rows.Count > 0
            ? section.Rows.Max(row => row.LineIndex) + 1
            : section.TableSeparatorLine + 1;
        var newRows = proposals.Where(proposal => !used.Contains(proposal)).Select(proposal =>
        {
            var id = SenseId(proposal);
            evidenceToAppend.AddRange(EvidenceLines(id, proposal));
            return RowMarkdown(id, proposal);
        }).ToArray();

        var merged = new List<string>();
        for (var index = 0; index < sectionLines.Count; index++)
        {
            if (index == insertAt) merged.AddRange(newRows);
            if (removals.Contains(index)) continue;
            merged.Add(replacements.TryGetValue(index, out var replacement)
                ? replacement
                : sectionLines[index]);
        }

        if (insertAt == sectionLines.Count) merged.AddRange(newRows);
        if (evidenceToAppend.Count > 0)
        {
            if (merged.Count > 0 && merged[^1].Length != 0) merged.Add(string.Empty);
            foreach (var evidence in ChunkEvidence(evidenceToAppend))
            {
                merged.AddRange(evidence);
                merged.Add(string.Empty);
            }

            if (merged.Count > 0 && merged[^1].Length == 0) merged.RemoveAt(merged.Count - 1);
        }

        return merged;
    }

    private static IEnumerable<IReadOnlyList<string>> ChunkEvidence(IReadOnlyList<string> lines)
    {
        var start = 0;
        while (start < lines.Count)
        {
            var end = start;
            while (end < lines.Count && !StringComparer.Ordinal.Equals(lines[end], EvidenceEnd)) end++;
            if (end < lines.Count) end++;
            yield return lines.Skip(start).Take(end - start).ToArray();
            start = end;
        }
    }

    private static IReadOnlyList<string> NewTermSection(string term, IReadOnlyList<GlossaryProposal> proposals)
    {
        var lines = new List<string> { $"## {term}", string.Empty, Header, Separator };
        foreach (var proposal in proposals)
        {
            var id = SenseId(proposal);
            lines.Add(RowMarkdown(id, proposal));
        }

        foreach (var proposal in proposals)
        {
            lines.Add(string.Empty);
            lines.AddRange(EvidenceLines(SenseId(proposal), proposal));
        }

        return lines;
    }

    private string CreateDocument(string owner)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var safeOwner = FrontmatterSerializer.Serialize(owner).TrimEnd();
        return $"""
            ---
            id: reference/glossary
            title: Glossary
            doc-type: reference
            status: needs-review
            owner: {safeOwner}
            last-reviewed: {today}
            ---

            # Glossary

            """;
    }

    private string FirstCatalogOwner()
    {
        var catalog = ReadCatalog();
        return catalog.FirstOwner ?? throw new InvalidOperationException(
            "The catalog has no data-row owner; a managed glossary cannot be created without an owner.");
    }

    private CatalogData ReadCatalog()
    {
        var catalogPath = ResolveContainedPath(_repositoryRoot, _config.Ontology.ResolvedCatalogPath);
        if (!File.Exists(catalogPath))
        {
            return new CatalogData(null, new HashSet<string>(StringComparer.Ordinal));
        }

        string? firstOwner = null;
        var components = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(catalogPath))
        {
            if (!line.StartsWith('|')) continue;
            // Catalog column configuration intentionally uses the raw pipe-split indices,
            // including the empty cell before a leading pipe, matching DocumentLoader.
            var cells = line.Split('|', StringSplitOptions.None)
                .Select(cell => cell.Trim())
                .ToArray();
            var maxColumn = Math.Max(
                _config.Ontology.CatalogComponentColumn,
                _config.Ontology.CatalogOwnerColumn);
            if (cells.Length <= maxColumn) continue;

            var component = cells[_config.Ontology.CatalogComponentColumn];
            var owner = cells[_config.Ontology.CatalogOwnerColumn];
            if (component.Length == 0
                || component.StartsWith("---", StringComparison.Ordinal)
                || StringComparer.Ordinal.Equals(component, "Component"))
            {
                continue;
            }

            components.Add(component);
            if (firstOwner is null && owner.Length > 0 && !owner.StartsWith("---", StringComparison.Ordinal))
            {
                firstOwner = owner;
            }
        }

        return new CatalogData(firstOwner, components);
    }

    private DiagnosticReport ValidateMarkdown(string markdown)
    {
        var document = Parse(markdown);
        var diagnostics = new DiagnosticReport();
        ValidateFrontmatter(document, diagnostics);
        var catalog = ReadCatalog();
        foreach (var section in document.Sections)
        {
            ValidateSectionStructure(section, diagnostics);
            if (!section.HasTable)
            {
                diagnostics.Add(Error($"Glossary term '{section.Term}' must contain the managed sense table."));
                continue;
            }

            foreach (var row in section.Rows)
            {
                if (!TryParseStatus(row.StatusText, out var status))
                {
                    diagnostics.Add(Error($"Glossary sense '{row.Id}' has unknown status '{row.StatusText}'."));
                }
                else if (status == GlossarySenseStatus.Approved)
                {
                    ValidateApprovedSense(row, catalog.Components, diagnostics);
                }
            }
        }

        return diagnostics;
    }

    private void ValidateFrontmatter(ParsedDocument document, DiagnosticReport diagnostics)
    {
        if (!document.FrontmatterValid)
        {
            diagnostics.Add(Error(
                "The managed glossary frontmatter is invalid: " +
                (document.FrontmatterError ?? "unknown frontmatter error")));
            return;
        }

        if (!StringComparer.Ordinal.Equals(document.Frontmatter.GetValueOrDefault("doc-type"), "reference"))
        {
            diagnostics.Add(Error("The managed glossary must use doc-type 'reference'."));
        }

        if (string.IsNullOrWhiteSpace(document.Frontmatter.GetValueOrDefault("id")))
        {
            diagnostics.Add(Error("The managed glossary must declare a document id."));
        }

        if (string.IsNullOrWhiteSpace(document.Frontmatter.GetValueOrDefault("title")))
        {
            diagnostics.Add(Error("The managed glossary must declare a title."));
        }

        if (!document.Frontmatter.ContainsKey("owner")
            || string.IsNullOrWhiteSpace(document.Frontmatter["owner"]))
        {
            diagnostics.Add(Error("The managed glossary must declare an owner."));
        }

        if (!document.Frontmatter.TryGetValue("last-reviewed", out var lastReviewed)
            || !DateOnly.TryParseExact(
                lastReviewed,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            diagnostics.Add(Error(
                "The managed glossary must preserve an ISO yyyy-MM-dd last-reviewed date."));
        }

        var status = document.Frontmatter.GetValueOrDefault("status");
        if (status is not ("current" or "needs-review"))
        {
            diagnostics.Add(Error(
                "The managed glossary status must be 'current' or 'needs-review'."));
        }
    }

    private void ValidateSectionStructure(ParsedSection section, DiagnosticReport diagnostics)
    {
        if (section.MalformedRows > 0)
        {
            diagnostics.Add(Error(
                $"Glossary term '{section.Term}' contains {section.MalformedRows} malformed sense row(s)."));
        }

        if (section.MalformedEvidence)
        {
            diagnostics.Add(Error(
                $"Glossary term '{section.Term}' contains malformed or unbalanced generated evidence markup."));
        }

        var rowIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in section.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id))
            {
                diagnostics.Add(Error($"Glossary term '{section.Term}' contains a sense without an ID."));
            }
            else if (!rowIds.Add(row.Id))
            {
                diagnostics.Add(Error($"Glossary term '{section.Term}' repeats sense ID '{row.Id}'."));
            }
        }

        foreach (var evidenceId in section.Evidence.Keys)
        {
            if (!rowIds.Contains(evidenceId))
            {
                diagnostics.Add(Error(
                    $"Glossary term '{section.Term}' has generated evidence for unknown sense '{evidenceId}'."));
            }
        }
    }

    private void ValidateApprovedSense(
        ParsedRow row,
        IReadOnlySet<string> components,
        DiagnosticReport diagnostics)
    {
        if (string.IsNullOrWhiteSpace(row.Definition))
        {
            diagnostics.Add(Error($"Approved glossary sense '{row.Id}' requires a definition."));
        }

        if (row.Scopes.Count == 0)
        {
            diagnostics.Add(Error($"Approved glossary sense '{row.Id}' requires at least one scope."));
            return;
        }

        foreach (var scope in row.Scopes)
        {
            if (scope.StartsWith("component:", StringComparison.Ordinal))
            {
                var component = scope["component:".Length..];
                if (component.Length == 0 || !components.Contains(component))
                {
                    diagnostics.Add(Error(
                        $"Approved glossary sense '{row.Id}' references unknown component scope '{scope}'."));
                }
            }
            else if (scope.StartsWith("code-ref:", StringComparison.Ordinal))
            {
                if (scope["code-ref:".Length..].Length == 0)
                {
                    diagnostics.Add(Error(
                        $"Approved glossary sense '{row.Id}' has an empty code-ref scope."));
                }
            }
            else
            {
                diagnostics.Add(Error(
                    $"Approved glossary sense '{row.Id}' has unsupported scope '{scope}'."));
            }
        }
    }

    private Diagnostic Error(string message) => new(
        ValidationRuleCode,
        Severity.Error,
        message,
        "managed glossary",
        _relativePath,
        "Use the managed glossary table shape and approved component:<catalog value> or code-ref:<symbol> scopes.");

    private static ParsedDocument Parse(string markdown)
    {
        var frontmatter = new Dictionary<string, string>(StringComparer.Ordinal);
        var frontmatterValid = true;
        string? frontmatterError = null;
        var read = MarkdownFrontmatterReader.Read(markdown);
        if (!read.HasFrontmatter)
        {
            frontmatterValid = false;
            frontmatterError = "The YAML frontmatter block is missing or unterminated.";
        }
        else
        {
            try
            {
                frontmatter = MarkdownFrontmatterReader.Deserializer
                    .Deserialize<Dictionary<string, string>>(read.Yaml)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception exception)
            {
                frontmatterValid = false;
                frontmatterError = exception.Message;
            }
        }

        var lines = SplitLines(markdown);
        var bodyStart = read.HasFrontmatter ? Math.Max(0, read.BodyStartLine - 1) : 0;
        return new ParsedDocument(
            frontmatter,
            ParseSections(lines, bodyStart),
            frontmatterValid,
            frontmatterError);
    }

    private static ParsedDocument Parse(List<string> lines) => new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        ParseSections(lines, 0),
        true,
        null);

    private static IReadOnlyList<ParsedSection> ParseSections(List<string> lines, int bodyStart)
    {
        var body = string.Join('\n', lines.Skip(bodyStart));
        var syntax = Markdown.Parse(body, MarkdownPipeline);
        var headings = syntax.Descendants<HeadingBlock>()
            .Where(heading => heading.Level == 2)
            .Select(heading =>
            {
                var line = heading.Line + bodyStart;
                var term = HeadingTerm(lines[line]);
                return (Line: line, Term: term);
            })
            .ToList();

        var sections = new List<ParsedSection>();
        for (var index = 0; index < headings.Count; index++)
        {
            var start = headings[index].Line;
            var end = index + 1 < headings.Count ? headings[index + 1].Line : lines.Count;
            sections.Add(ParseSection(headings[index].Term, lines, start, end));
        }

        return sections;
    }

    private static ParsedSection ParseSection(string term, IReadOnlyList<string> lines, int start, int end)
    {
        var fencedLines = FencedLineIndexes(lines, start, end);
        var headerLine = -1;
        for (var index = start; index < end; index++)
        {
            if (!fencedLines.Contains(index)
                && StringComparer.Ordinal.Equals(lines[index].Trim(), Header))
            {
                headerLine = index;
                break;
            }
        }

        var separatorLine = headerLine >= 0 && headerLine + 1 < end ? headerLine + 1 : -1;
        var rows = new List<ParsedRow>();
        var malformedRows = 0;
        if (separatorLine >= 0 && IsTableSeparator(lines[separatorLine]))
        {
            for (var index = separatorLine + 1; index < end; index++)
            {
                if (fencedLines.Contains(index))
                {
                    malformedRows++;
                    break;
                }

                if (lines[index].Trim().Length == 0) break;
                if (lines[index].TrimStart().StartsWith(EvidenceStart, StringComparison.Ordinal)) break;
                if (!lines[index].TrimStart().StartsWith('|'))
                {
                    malformedRows++;
                    break;
                }

                var cells = SplitTableRow(lines[index]);
                if (cells.Count == 5)
                {
                    rows.Add(new ParsedRow(
                        cells[0],
                        cells[1],
                        cells[2],
                        SplitSemicolon(cells[3]),
                        SplitSemicolon(cells[4]),
                        index,
                        lines[index]));
                }
                else
                {
                    malformedRows++;
                }
            }
        }

        var evidence = new Dictionary<string, ParsedEvidence>(StringComparer.Ordinal);
        var malformedEvidence = false;
        for (var index = start; index < end; index++)
        {
            if (fencedLines.Contains(index)) continue;
            var trimmed = lines[index].Trim();
            if (!trimmed.StartsWith(EvidenceStart, StringComparison.Ordinal)) continue;
            var id = Attribute(trimmed, "sense");
            if (id is null)
            {
                malformedEvidence = true;
                continue;
            }
            var blockEnd = index + 1;
            while (blockEnd < end && !StringComparer.Ordinal.Equals(lines[blockEnd].Trim(), EvidenceEnd)) blockEnd++;
            if (blockEnd < end)
            {
                blockEnd++;
            }
            else
            {
                malformedEvidence = true;
            }

            if (evidence.ContainsKey(id)) malformedEvidence = true;
            var evidenceIds = lines
                .Skip(index + 1)
                .Take(Math.Max(0, blockEnd - index - 1))
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .Select(line => line[2..].Trim())
                .Where(idValue => idValue.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var evidenceLines = lines
                .Skip(index + 1)
                .Take(Math.Max(0, blockEnd - index - 2))
                .ToArray();
            evidence[id] = new ParsedEvidence(
                index,
                blockEnd,
                Attribute(trimmed, "fingerprint"),
                evidenceIds,
                evidenceLines);
            index = blockEnd - 1;
        }

        return new ParsedSection(
            term,
            start,
            end,
            headerLine >= 0 && separatorLine >= 0 && IsTableSeparator(lines[separatorLine]),
            separatorLine,
            rows,
            evidence,
            malformedRows,
            malformedEvidence);
    }

    private static IReadOnlySet<int> FencedLineIndexes(
        IReadOnlyList<string> lines,
        int start,
        int end)
    {
        var fenced = new HashSet<int>();
        char? marker = null;
        var openingLength = 0;

        for (var index = start; index < end; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (marker is not null)
            {
                fenced.Add(index);
                var length = MarkerLength(trimmed, marker.Value);
                if (length >= openingLength && trimmed[length..].Trim().Length == 0)
                {
                    marker = null;
                    openingLength = 0;
                }

                continue;
            }

            var candidate = trimmed.Length > 0 ? trimmed[0] : '\0';
            if (candidate is not ('`' or '~')) continue;
            var candidateLength = MarkerLength(trimmed, candidate);
            if (candidateLength < 3) continue;

            marker = candidate;
            openingLength = candidateLength;
            fenced.Add(index);
        }

        return fenced;
    }

    private static int MarkerLength(string line, char marker)
    {
        var length = 0;
        while (length < line.Length && line[length] == marker) length++;
        return length;
    }

    private static GlossarySense ToSense(
        ParsedRow row,
        IReadOnlyDictionary<string, ParsedEvidence> evidence)
    {
        _ = TryParseStatus(row.StatusText, out var status);
        return new GlossarySense(
            row.Id,
            status,
            row.Definition,
            row.Scopes,
            row.Aliases,
            evidence.TryGetValue(row.Id, out var block) ? block.EvidenceIds : []);
    }

    private static bool IsUntouchedGenerated(ParsedRow row, ParsedEvidence evidence)
    {
        return evidence.Fingerprint is not null
               && StringComparer.Ordinal.Equals(
                   evidence.Fingerprint,
                   OwnershipFingerprint(row.RawLine, evidence.EvidenceLines));
    }

    private static string RowMarkdown(string id, GlossaryProposal proposal) =>
        $"| {EscapeCell(id)} | proposed | {EscapeCell(proposal.Definition)} | " +
        $"{EscapeCell(string.Join("; ", proposal.Scopes))} | {EscapeCell(string.Join("; ", proposal.Aliases))} |";

    private static IReadOnlyList<string> EvidenceLines(string id, GlossaryProposal proposal)
    {
        var row = RowMarkdown(id, proposal);
        var evidenceLines = proposal.EvidenceIds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(evidenceId => $"- {EscapeEvidence(evidenceId)}")
            .ToArray();
        var lines = new List<string>
        {
            $"{EvidenceStart} sense=\"{EscapeAttribute(id)}\" fingerprint=\"{OwnershipFingerprint(row, evidenceLines)}\" -->"
        };
        lines.AddRange(evidenceLines);
        lines.Add(EvidenceEnd);
        return lines;
    }

    private static string SenseId(GlossaryProposal proposal)
    {
        var slug = new string(proposal.Term.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');
        if (slug.Length == 0) slug = "term";
        var identity = string.Join('\n',
            proposal.Term.Trim().ToLowerInvariant(),
            string.Join(';', proposal.Scopes.Order(StringComparer.Ordinal)),
            string.Join(';', proposal.Aliases.Order(StringComparer.Ordinal)));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..8];
        return $"{slug}-{hash}";
    }

    private static string OwnershipFingerprint(string row, IReadOnlyList<string> evidenceLines)
    {
        var content = string.Join('\n',
            row,
            string.Join("\n", evidenceLines));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static string DemoteToNeedsReview(string markdown)
    {
        var newline = markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(markdown);
        var frontmatterEnd = lines.Count > 0 && StringComparer.Ordinal.Equals(lines[0], "---")
            ? lines.FindIndex(1, line => StringComparer.Ordinal.Equals(line, "---"))
            : -1;
        for (var index = 1; index < frontmatterEnd; index++)
        {
            if (lines[index].StartsWith("status:", StringComparison.Ordinal))
            {
                lines[index] = "status: needs-review";
                break;
            }
        }

        return string.Join(newline, lines) + newline;
    }

    private void AtomicWrite(string markdown)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The glossary path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, markdown);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void ValidateProposals(IReadOnlyList<GlossaryProposal> proposals)
    {
        foreach (var proposal in proposals)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(proposal.Term);
            ArgumentNullException.ThrowIfNull(proposal.Scopes);
            ArgumentNullException.ThrowIfNull(proposal.Aliases);
            ArgumentNullException.ThrowIfNull(proposal.EvidenceIds);
            if (proposal.Scopes.Count == 0)
            {
                throw new ArgumentException("Glossary proposals require at least one scope.", nameof(proposals));
            }

            RequireSingleLine(proposal.Term, "term", proposals);
            RequireSingleLine(proposal.Definition, "definition", proposals);
            foreach (var scope in proposal.Scopes)
            {
                RequireSingleLine(scope, "scope", proposals);
                if (scope.Contains(';', StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Each glossary proposal scope must be a single component: or code-ref: value.",
                        nameof(proposals));
                }
            }

            foreach (var alias in proposal.Aliases)
            {
                RequireSingleLine(alias, "alias", proposals);
                if (alias.Contains(';', StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Each glossary proposal alias must be supplied as a separate value.",
                        nameof(proposals));
                }
            }
        }
    }

    private static void RequireSingleLine(
        string value,
        string field,
        IReadOnlyList<GlossaryProposal> proposals)
    {
        if (value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Glossary proposal {field} values must fit on one Markdown table line.",
                nameof(proposals));
        }
    }

    private static bool TryParseStatus(string value, out GlossarySenseStatus status)
    {
        status = value switch
        {
            "proposed" => GlossarySenseStatus.Proposed,
            "approved" => GlossarySenseStatus.Approved,
            "rejected" => GlossarySenseStatus.Rejected,
            _ => default
        };
        return value is "proposed" or "approved" or "rejected";
    }

    private static bool SameScopes(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count
        && left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string ResolveContainedPath(string repositoryRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("The glossary path must be repository-relative.", nameof(relativePath));
        }

        var resolved = Path.GetFullPath(
            relativePath.Replace('/', Path.DirectorySeparatorChar),
            repositoryRoot);
        var prefix = repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? repositoryRoot
            : repositoryRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new ArgumentException("The glossary path must remain inside the repository.", nameof(relativePath));
        }

        return resolved;
    }

    private static void RejectLinkedPath(string repositoryRoot, string targetPath)
    {
        var relative = Path.GetRelativePath(repositoryRoot, targetPath);
        var current = repositoryRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || new FileInfo(current).LinkTarget is not null
                || new DirectoryInfo(current).LinkTarget is not null)
            {
                throw new InvalidOperationException(
                    $"The managed glossary path contains a symbolic link or reparse point: '{current}'.");
            }
        }
    }

    private static bool IsTableSeparator(string line)
    {
        var cells = SplitTableRow(line);
        return cells.Count == 5 && cells.All(cell => cell.Length >= 3 && cell.All(character => character == '-'));
    }

    private static List<string> SplitTableRow(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var trimmed = line.Trim();
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (character == '\\' && index + 1 < trimmed.Length && trimmed[index + 1] == '|')
            {
                current.Append('|');
                index++;
            }
            else if (character == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0) cells.Add(current.ToString().Trim());
        if (cells.Count > 0 && cells[0].Length == 0) cells.RemoveAt(0);
        return cells;
    }

    private static string HeadingTerm(string line)
    {
        var trimmed = line.TrimStart();
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
        return trimmed[hashes..].Trim().TrimEnd('#').TrimEnd();
    }

    private static IReadOnlyList<string> SplitSemicolon(string value) =>
        value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? Attribute(string marker, string name)
    {
        var prefix = name + "=\"";
        var start = marker.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += prefix.Length;
        var end = marker.IndexOf('"', start);
        return end < 0 ? null : marker[start..end];
    }

    private static List<string> SplitLines(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string EscapeCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
    private static string EscapeAttribute(string value) => value.Replace("\"", "&quot;", StringComparison.Ordinal);
    private static string EscapeEvidence(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private sealed record ParsedDocument(
        IReadOnlyDictionary<string, string> Frontmatter,
        IReadOnlyList<ParsedSection> Sections,
        bool FrontmatterValid,
        string? FrontmatterError);

    private sealed record ParsedSection(
        string Term,
        int StartLine,
        int EndLine,
        bool HasTable,
        int TableSeparatorLine,
        IReadOnlyList<ParsedRow> Rows,
        IReadOnlyDictionary<string, ParsedEvidence> Evidence,
        int MalformedRows,
        bool MalformedEvidence);

    private sealed record ParsedRow(
        string Id,
        string StatusText,
        string Definition,
        IReadOnlyList<string> Scopes,
        IReadOnlyList<string> Aliases,
        int LineIndex,
        string RawLine);

    private sealed record ParsedEvidence(
        int StartLine,
        int EndLine,
        string? Fingerprint,
        IReadOnlyList<string> EvidenceIds,
        IReadOnlyList<string> EvidenceLines);
    private sealed record CatalogData(string? FirstOwner, IReadOnlySet<string> Components);
}

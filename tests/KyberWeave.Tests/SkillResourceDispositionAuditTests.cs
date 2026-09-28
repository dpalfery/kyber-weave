using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the eight-part content-preservation audit contract for the 66 skill resources under
/// <c>products/kyber-squad/skills/</c>: every resource has exactly one disposition row, the
/// disposition and delivery vocabularies are closed and self-consistent with the policy-line
/// ledger, every ledger destination names a real template section containing its anchor, every
/// non-retained excerpt is gone from its source while every retained one remains, every
/// policy-bearing resource points at its destination standard, every relative local link in a
/// skill resource resolves, and every ledger anchor is seeded by <c>docs init</c>.
/// </summary>
/// <remarks>
/// This is the RED half of a test-first task: the audit document
/// <c>docs/kyber-squad/skill-resource-dispositions.md</c> does not exist yet, so A1 through A6
/// and A8 fail here by design, each naming the missing document. A7 is the exception — it checks
/// the real skill tree directly, not the audit doc, so its real-tree case is a passing baseline
/// rather than a RED failure. Its fixture case, <see cref="ADanglingLocalLinkInASkillResourceIsReported"/>,
/// proves the shared checker actually detects a dangling link rather than passing vacuously
/// because the real tree at the base commit has none.
/// </remarks>
public sealed class SkillResourceDispositionAuditTests
{
    private const string RelativeAuditPath = "docs/kyber-squad/skill-resource-dispositions.md";
    private const string NoDestination = "—";
    private const string DestinationSeparator = " § ";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .Build();

    private static readonly HashSet<string> ContentClassVocabulary = new(StringComparer.Ordinal)
    {
        "policy-bearing", "technique", "procedure", "lens-criteria", "review-pointer",
        "provider-guidance", "script", "template", "inventory", "metadata"
    };

    private static readonly HashSet<string> DispositionVocabulary = new(StringComparer.Ordinal)
    {
        "retain-in-place", "policy-migrated-to-template", "superseded-intentionally", "pointer-already-#126"
    };

    private static readonly HashSet<string> DeliveryVocabulary = new(StringComparer.Ordinal)
    {
        "rendered", "packaged-only"
    };

    private static readonly HashSet<string> LedgerDispositionVocabulary = new(StringComparer.Ordinal)
    {
        "migrated", "duplicate", "superseded", "retained"
    };

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.None);
    private static readonly Regex CodingStandardToken = new(@"<([a-z][a-z0-9-]*)-coding-standard>", RegexOptions.None);
    private static readonly Regex UriScheme = new(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:", RegexOptions.None);

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string AuditDocumentPath =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "docs", "kyber-squad", "skill-resource-dispositions.md");

    /// <summary>
    /// Prevents a resource from being silently dropped, duplicated, or invented in the audit: the
    /// table's Resource column must equal, one-for-one, the 66 files on disk under
    /// <c>products/kyber-squad/skills/**</c> other than <c>SKILL.md</c>.
    /// </summary>
    [Fact]
    public void EveryCanonicalSkillResourceHasExactlyOneDispositionRow()
    {
        string[] onDisk = OnDiskSkillResourcePaths();

        if (!File.Exists(AuditDocumentPath))
        {
            Assert.Fail(
                $"Audit document '{RelativeAuditPath}' is missing. Expected {onDisk.Length} resource rows:\n" +
                string.Join("\n", onDisk));
        }

        IReadOnlyList<DispositionRow> rows = ParseDispositionRows(File.ReadAllText(AuditDocumentPath));

        string[] duplicates = rows
            .Select(row => row.Resource)
            .GroupBy(resource => resource, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            duplicates.Length == 0,
            "'Resource dispositions' has duplicate Resource rows: " + string.Join(", ", duplicates));

        HashSet<string> tableSet = rows.Select(row => row.Resource).ToHashSet(StringComparer.Ordinal);
        HashSet<string> diskSet = onDisk.ToHashSet(StringComparer.Ordinal);
        string[] missing = diskSet.Where(path => !tableSet.Contains(path)).Order(StringComparer.Ordinal).ToArray();
        string[] extra = tableSet.Where(path => !diskSet.Contains(path)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            $"'Resource dispositions' does not match disk. Missing: [{string.Join(", ", missing)}]. " +
            $"Extra: [{string.Join(", ", extra)}].");
        Assert.Equal(66, rows.Count);
    }

    /// <summary>
    /// Prevents an unrecognized content class, disposition, or delivery value from entering the
    /// audit, an empty Verification cell, and a disposition that does not follow from its own
    /// policy-line ledger rows per the derivation table (D5).
    /// </summary>
    [Fact]
    public void DispositionsUseClosedVocabulariesAndAgreeWithTheLedger()
    {
        List<string> findings = [];
        if (!File.Exists(AuditDocumentPath))
        {
            findings.Add($"Audit document '{RelativeAuditPath}' is missing.");
        }
        else
        {
            string markdown = File.ReadAllText(AuditDocumentPath);
            IReadOnlyList<DispositionRow> dispositions = ParseDispositionRows(markdown);
            IReadOnlyList<LedgerRow> ledger = ParseLedgerRows(markdown);

            foreach (DispositionRow row in dispositions)
            {
                if (!ContentClassVocabulary.Contains(row.ContentClass))
                {
                    findings.Add($"{row.Resource}: unknown content class '{row.ContentClass}'.");
                }

                if (!DispositionVocabulary.Contains(row.Disposition))
                {
                    findings.Add($"{row.Resource}: unknown disposition '{row.Disposition}'.");
                }

                if (!DeliveryVocabulary.Contains(row.Delivery))
                {
                    findings.Add($"{row.Resource}: unknown delivery '{row.Delivery}'.");
                }

                if (string.IsNullOrWhiteSpace(row.Verification))
                {
                    findings.Add($"{row.Resource}: Verification is empty.");
                }
            }

            HashSet<string> resources = dispositions.Select(row => row.Resource).ToHashSet(StringComparer.Ordinal);
            foreach (LedgerRow row in ledger)
            {
                if (!LedgerDispositionVocabulary.Contains(row.Disposition))
                {
                    findings.Add($"{row.Source}:{row.Line}: unknown ledger disposition '{row.Disposition}'.");
                }

                if (!resources.Contains(row.Source))
                {
                    findings.Add($"{row.Source}:{row.Line}: ledger Source is not a 'Resource dispositions' row.");
                }

                // A moved line must say where it went, or A4 and A8 have nothing to check it against.
                bool moved = row.Disposition is "migrated" or "duplicate";
                if (moved && (SplitDestination(row.Destination).Technology.Length == 0 || row.Anchor.Length == 0))
                {
                    findings.Add(
                        $"{row.Source}:{row.Line}: '{row.Disposition}' needs a '<technology> § <heading>' " +
                        "Destination and an Anchor.");
                }

                if (row.Disposition is "superseded" or "retained" &&
                    (row.Reason.Length == 0 || string.Equals(row.Reason, NoDestination, StringComparison.Ordinal)))
                {
                    findings.Add($"{row.Source}:{row.Line}: '{row.Disposition}' needs a Reason.");
                }
            }

            ILookup<string, LedgerRow> ledgerBySource = ledger.ToLookup(row => row.Source, StringComparer.Ordinal);

            // A policy-bearing resource with no ledger rows would let every excerpt check pass vacuously.
            foreach (DispositionRow row in dispositions.Where(candidate => candidate.ContentClass == "policy-bearing"))
            {
                if (!ledgerBySource[row.Resource].Any())
                {
                    findings.Add($"{row.Resource}: is policy-bearing but has no Policy-line ledger rows.");
                }
            }
            foreach (DispositionRow row in dispositions)
            {
                string expected = DeriveDisposition(row.Resource, ledgerBySource[row.Resource]);
                if (!string.Equals(expected, row.Disposition, StringComparison.Ordinal))
                {
                    findings.Add(
                        $"{row.Resource}: disposition '{row.Disposition}' does not follow from its ledger rows " +
                        $"(expected '{expected}').");
                }
            }
        }

        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    /// <summary>
    /// Prevents the Delivery column from claiming a resource is <c>rendered</c> when
    /// <see cref="SquadSourceLoader.Load"/>'s resource closure does not carry it for that skill,
    /// or <c>packaged-only</c> when the closure does — the two must describe the same reality.
    /// </summary>
    [Fact]
    public void DeliveryColumnMatchesTheRenderedResourceClosure()
    {
        List<string> findings = [];
        if (!File.Exists(AuditDocumentPath))
        {
            findings.Add($"Audit document '{RelativeAuditPath}' is missing.");
        }
        else
        {
            IReadOnlyList<DispositionRow> dispositions = ParseDispositionRows(File.ReadAllText(AuditDocumentPath));
            SquadSource source = SquadSourceLoader.Load(ProductRoot);
            Dictionary<string, HashSet<string>> renderedBySkill = source.Skills.ToDictionary(
                skill => skill.Name,
                skill => skill.Resources.Select(resource => resource.RelativePath).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

            foreach (DispositionRow row in dispositions)
            {
                (string skillName, string withinSkillPath) = SplitSkillResource(row.Resource);
                bool rendered = renderedBySkill.TryGetValue(skillName, out HashSet<string>? resources) &&
                                 resources.Contains(withinSkillPath);
                string expected = rendered ? "rendered" : "packaged-only";
                if (!string.Equals(expected, row.Delivery, StringComparison.Ordinal))
                {
                    findings.Add(
                        $"{row.Resource}: Delivery is '{row.Delivery}', but SquadSourceLoader's render " +
                        $"closure says '{expected}'.");
                }
            }
        }

        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    /// <summary>
    /// Prevents a ledger row from pointing at a template section or anchor text that does not
    /// actually exist: the Destination's <c>##</c>/<c>###</c> heading must exist in that
    /// technology's standard, and the heading's own section must contain the Anchor verbatim.
    /// </summary>
    [Fact]
    public void LedgerDestinationsNameExistingTemplateSectionsContainingTheirAnchor()
    {
        List<string> findings = [];
        if (!File.Exists(AuditDocumentPath))
        {
            findings.Add($"Audit document '{RelativeAuditPath}' is missing.");
        }
        else
        {
            IReadOnlyList<LedgerRow> ledger = ParseLedgerRows(File.ReadAllText(AuditDocumentPath));
            Dictionary<string, Section[]?> sectionsByTechnology = new(StringComparer.Ordinal);

            foreach (LedgerRow row in ledger)
            {
                (string technology, string heading) = SplitDestination(row.Destination);
                if (technology.Length == 0)
                {
                    continue;
                }

                if (!sectionsByTechnology.TryGetValue(technology, out Section[]? sections))
                {
                    string standardPath = Path.Combine(ProductRoot, "standards", technology, "README.md");
                    sections = File.Exists(standardPath) ? HeadingSections(File.ReadAllText(standardPath)) : null;
                    sectionsByTechnology[technology] = sections;
                }

                if (sections is null)
                {
                    findings.Add(
                        $"{row.Source}:{row.Line}: destination technology '{technology}' has no " +
                        $"'standards/{technology}/README.md'.");
                    continue;
                }

                Section? section = sections.FirstOrDefault(candidate =>
                    string.Equals(candidate.Heading, heading, StringComparison.Ordinal));
                if (section is null)
                {
                    findings.Add(
                        $"{row.Source}:{row.Line}: no '## {heading}' or '### {heading}' heading in " +
                        $"standards/{technology}/README.md.");
                    continue;
                }

                if (row.Anchor.Length == 0)
                {
                    findings.Add($"{row.Source}:{row.Line}: Destination is set but Anchor is empty.");
                    continue;
                }

                if (!CollapsedWhitespace(section.Body).Contains(CollapsedWhitespace(row.Anchor), StringComparison.Ordinal))
                {
                    findings.Add(
                        $"{row.Source}:{row.Line}: Anchor '{row.Anchor}' does not appear in " +
                        $"standards/{technology}/README.md § {heading}.");
                }
            }
        }

        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    /// <summary>
    /// Prevents a <c>migrated</c>, <c>duplicate</c>, or <c>superseded</c> ledger row from leaving
    /// its excerpt behind in the source file, and prevents a <c>retained</c> row from claiming an
    /// excerpt that is no longer there — the excerpts are records of what changed, not aspiration.
    /// </summary>
    [Fact]
    public void MovedAndSupersededExcerptsAreGoneAndRetainedExcerptsRemain()
    {
        List<string> findings = [];
        if (!File.Exists(AuditDocumentPath))
        {
            findings.Add($"Audit document '{RelativeAuditPath}' is missing.");
        }
        else
        {
            IReadOnlyList<LedgerRow> ledger = ParseLedgerRows(File.ReadAllText(AuditDocumentPath));
            Dictionary<string, string?> sourceContent = new(StringComparer.Ordinal);

            foreach (LedgerRow row in ledger)
            {
                if (!sourceContent.TryGetValue(row.Source, out string? content))
                {
                    string sourcePath = ResourceFilePath(row.Source);
                    content = File.Exists(sourcePath) ? File.ReadAllText(sourcePath) : null;
                    sourceContent[row.Source] = content;
                }

                if (content is null)
                {
                    findings.Add($"{row.Source}:{row.Line}: source file does not exist.");
                    continue;
                }

                bool present = CollapsedWhitespace(content).Contains(CollapsedWhitespace(row.Excerpt), StringComparison.Ordinal);
                bool shouldBeGone = row.Disposition is "migrated" or "duplicate" or "superseded";
                if (shouldBeGone && present)
                {
                    findings.Add(
                        $"{row.Source}:{row.Line}: excerpt '{row.Excerpt}' is disposition '{row.Disposition}' " +
                        "but still appears in the source.");
                }
                else if (!shouldBeGone && !present)
                {
                    findings.Add(
                        $"{row.Source}:{row.Line}: excerpt '{row.Excerpt}' is 'retained' but no longer " +
                        "appears in the source.");
                }
            }
        }

        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    /// <summary>
    /// Prevents a <c>policy-bearing</c> resource from pointing readers at its template without a
    /// registry token they can follow, and — independent of the audit doc — prevents any
    /// <c>&lt;x-coding-standard&gt;</c> token under <c>skills/**</c> from naming a technology with
    /// no matching <c>standards/&lt;x&gt;/</c> directory.
    /// </summary>
    [Fact]
    public void PolicyBearingResourcesPointAtEveryDestinationStandard()
    {
        List<string> findings = [];
        if (!File.Exists(AuditDocumentPath))
        {
            findings.Add($"Audit document '{RelativeAuditPath}' is missing.");
        }
        else
        {
            string markdown = File.ReadAllText(AuditDocumentPath);
            IReadOnlyList<DispositionRow> dispositions = ParseDispositionRows(markdown);
            IReadOnlyList<LedgerRow> ledger = ParseLedgerRows(markdown);
            ILookup<string, LedgerRow> ledgerBySource = ledger.ToLookup(row => row.Source, StringComparer.Ordinal);

            foreach (DispositionRow row in dispositions.Where(candidate => candidate.ContentClass == "policy-bearing"))
            {
                string[] technologies = ledgerBySource[row.Resource]
                    .Select(row2 => SplitDestination(row2.Destination).Technology)
                    .Where(technology => technology.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (technologies.Length == 0)
                {
                    continue;
                }

                string content = File.ReadAllText(ResourceFilePath(row.Resource));
                foreach (string technology in technologies)
                {
                    string token = $"<{technology}-coding-standard>";
                    if (!content.Contains(token, StringComparison.Ordinal))
                    {
                        findings.Add($"{row.Resource}: does not contain the token '{token}' for its ledger destination '{technology}'.");
                    }
                }
            }
        }

        findings.AddRange(DanglingCodingStandardTokens());
        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    /// <summary>
    /// Prevents a skill resource from shipping a relative Markdown link that resolves nowhere —
    /// the failure mode of a file that names a sibling resource in prose but never updates the
    /// link when that sibling moves or is renamed.
    /// </summary>
    [Fact]
    public void EveryMarkdownSkillResourceResolvesItsLocalLinks()
    {
        string[] markdownFiles = OnDiskSkillResourcePaths()
            .Where(path => path.EndsWith(".md", StringComparison.Ordinal))
            .Select(ResourceFilePath)
            .ToArray();

        IReadOnlyList<string> findings = DanglingLocalLinks(markdownFiles);

        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    /// <summary>
    /// Proves <see cref="DanglingLocalLinks"/> actually detects a dangling relative link, rather
    /// than the real-tree case above passing vacuously because the tree at the base commit
    /// happens to have none. A checker that reports nothing would let a truly broken link ship.
    /// </summary>
    [Fact]
    public void ADanglingLocalLinkInASkillResourceIsReported()
    {
        using TempDirectory temp = new();
        string resourcePath = Path.Combine(temp.Path, "reference.md");
        File.WriteAllText(resourcePath, "See [missing](missing-file.md) for detail.\n");

        IReadOnlyList<string> findings = DanglingLocalLinks([resourcePath]);

        Assert.True(
            findings.Count == 1 && findings[0].Contains("missing-file.md", StringComparison.Ordinal),
            "Expected exactly one finding naming 'missing-file.md', got: " + string.Join(" | ", findings));
    }

    /// <summary>
    /// Prevents a ledger Anchor from citing template wording that <c>docs init --kyber-standards</c>
    /// never actually seeds: the embedded <see cref="KyberStandardsTemplates.Render"/> output for
    /// the anchor's technology must contain it verbatim.
    /// </summary>
    [Fact]
    public void DocsInitSeedsEveryLedgerTemplateAnchor()
    {
        List<string> findings = [];
        if (!File.Exists(AuditDocumentPath))
        {
            findings.Add($"Audit document '{RelativeAuditPath}' is missing.");
        }
        else
        {
            IReadOnlyList<LedgerRow> ledger = ParseLedgerRows(File.ReadAllText(AuditDocumentPath));
            Dictionary<string, string> rendered = new(StringComparer.Ordinal);

            foreach (LedgerRow row in ledger.Where(candidate => candidate.Anchor.Length > 0))
            {
                (string technology, _) = SplitDestination(row.Destination);
                if (technology.Length == 0)
                {
                    findings.Add($"{row.Source}:{row.Line}: Anchor '{row.Anchor}' has no Destination technology to render.");
                    continue;
                }

                if (!rendered.TryGetValue(technology, out string? template))
                {
                    try
                    {
                        template = KyberStandardsTemplates.Render(technology, "dpalfery", "2026-09-28");
                    }
                    catch (ArgumentException exception)
                    {
                        findings.Add($"{row.Source}:{row.Line}: {exception.Message}");
                        continue;
                    }

                    rendered[technology] = template;
                }

                if (!CollapsedWhitespace(template).Contains(CollapsedWhitespace(row.Anchor), StringComparison.Ordinal))
                {
                    findings.Add(
                        $"{row.Source}:{row.Line}: Anchor '{row.Anchor}' does not appear in " +
                        $"KyberStandardsTemplates.Render(\"{technology}\", ...).");
                }
            }
        }

        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    private static string[] OnDiskSkillResourcePaths() =>
        Directory.EnumerateFiles(Path.Combine(ProductRoot, "skills"), "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(ProductRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ResourceFilePath(string productRelativeResource) =>
        Path.Combine(ProductRoot, productRelativeResource.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsNonLensCodeReviewReference(string resource) =>
        resource.StartsWith("skills/code-review/references/", StringComparison.Ordinal) &&
        !resource.StartsWith("skills/code-review/references/lenses/", StringComparison.Ordinal);

    /// <summary>Implements the D5 disposition-derivation table.</summary>
    private static string DeriveDisposition(string resource, IEnumerable<LedgerRow> ledgerRowsForResource)
    {
        LedgerRow[] rows = ledgerRowsForResource.ToArray();
        if (rows.Any(row => row.Disposition is "migrated" or "duplicate"))
        {
            return "policy-migrated-to-template";
        }

        if (rows.Any(row => row.Disposition == "superseded"))
        {
            return "superseded-intentionally";
        }

        return IsNonLensCodeReviewReference(resource) ? "pointer-already-#126" : "retain-in-place";
    }

    private static (string SkillName, string RelativePath) SplitSkillResource(string resource)
    {
        string[] segments = resource.Split('/');
        Assert.True(
            segments.Length >= 3 && string.Equals(segments[0], "skills", StringComparison.Ordinal),
            $"'{resource}' is not a 'skills/<name>/...' resource path.");
        return (segments[1], string.Join('/', segments.Skip(2)));
    }

    private static (string Technology, string Heading) SplitDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || string.Equals(destination, NoDestination, StringComparison.Ordinal))
        {
            return (string.Empty, string.Empty);
        }

        int separator = destination.IndexOf(DestinationSeparator, StringComparison.Ordinal);
        return separator < 0
            ? (string.Empty, string.Empty)
            : (destination[..separator].Trim(), destination[(separator + DestinationSeparator.Length)..].Trim());
    }

    private static string CollapsedWhitespace(string text) => WhitespaceRun.Replace(text, " ").Trim();

    private static IReadOnlyList<string> DanglingCodingStandardTokens()
    {
        List<string> findings = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(ProductRoot, "skills"), "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(ProductRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            foreach (Match match in CodingStandardToken.Matches(File.ReadAllText(file)))
            {
                string technology = match.Groups[1].Value;
                if (!Directory.Exists(Path.Combine(ProductRoot, "standards", technology)))
                {
                    findings.Add(
                        $"{relativePath}: token '<{technology}-coding-standard>' names no " +
                        $"'standards/{technology}/' directory.");
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Reports every relative local link in <paramref name="markdownFiles"/> that does not resolve
    /// to an existing file, resolved against each file's own directory.
    /// </summary>
    /// <remarks>
    /// A link naming a URI scheme (<c>https:</c>, <c>mailto:</c>, ...), a fragment-only link, or a
    /// bare <c>&lt;token&gt;</c> placeholder is not a local file reference and is skipped. Markdig
    /// never produces a <see cref="LinkInline"/> for a link written inside a code span or fenced
    /// code block, so code is excluded for free by walking the parsed inline tree instead of the
    /// raw text.
    /// </remarks>
    private static IReadOnlyList<string> DanglingLocalLinks(IEnumerable<string> markdownFiles)
    {
        List<string> findings = [];
        foreach (string file in markdownFiles)
        {
            string directory = Path.GetDirectoryName(file) ?? string.Empty;
            string markdown = File.ReadAllText(file);
            foreach (LinkInline link in Markdown.Parse(markdown, Pipeline).Descendants<LinkInline>())
            {
                string? url = link.Url;
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (UriScheme.IsMatch(url) || url.StartsWith('#') || (url.StartsWith('<') && url.EndsWith('>')))
                {
                    continue;
                }

                string target = url.Split('#')[0];
                if (target.Length == 0)
                {
                    continue;
                }

                string resolved = Path.GetFullPath(Path.Combine(directory, target.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(resolved))
                {
                    findings.Add($"{file}: link '{url}' does not resolve to an existing file.");
                }
            }
        }

        return findings;
    }

    private static IReadOnlyList<DispositionRow> ParseDispositionRows(string auditMarkdown)
    {
        Section section = H2Sections(auditMarkdown).Single(candidate => candidate.Heading == "Resource dispositions");
        List<DispositionRow> result = [];
        foreach (TableCell[] cells in TableCellRows(section.Body))
        {
            Assert.True(cells.Length == 6, $"A 'Resource dispositions' row has {cells.Length} cells, expected 6.");
            string resource = RequireCodeSpan(cells[0], "Resource", "Resource dispositions");
            result.Add(new DispositionRow(
                resource,
                CellText(cells[1]),
                CellText(cells[2]),
                CellText(cells[3]),
                CellText(cells[4]),
                CellText(cells[5])));
        }

        return result;
    }

    private static IReadOnlyList<LedgerRow> ParseLedgerRows(string auditMarkdown)
    {
        Section section = H2Sections(auditMarkdown).Single(candidate => candidate.Heading == "Policy-line ledger");
        List<LedgerRow> result = [];
        foreach (TableCell[] cells in TableCellRows(section.Body))
        {
            Assert.True(cells.Length == 7, $"A 'Policy-line ledger' row has {cells.Length} cells, expected 7.");
            string source = CellText(cells[0]);
            string line = CellText(cells[1]);
            string excerpt = RequireCodeSpan(cells[2], "Excerpt", $"{source}:{line}");
            string anchorText = CellText(cells[5]);
            string anchor = anchorText.Length == 0 ? string.Empty : RequireCodeSpan(cells[5], "Anchor", $"{source}:{line}");
            result.Add(new LedgerRow(
                source,
                line,
                excerpt,
                CellText(cells[3]),
                CellText(cells[4]),
                anchor,
                CellText(cells[6])));
        }

        return result;
    }

    private static TableCell[][] TableCellRows(string sectionMarkdown)
    {
        Table? table = Markdown.Parse(sectionMarkdown, Pipeline).Descendants<Table>().FirstOrDefault();
        if (table is null)
        {
            return [];
        }

        return table.OfType<TableRow>()
            .Where(row => !row.IsHeader)
            .Select(row => row.OfType<TableCell>().ToArray())
            .ToArray();
    }

    /// <summary>
    /// Concatenates a table cell's literal and code-span text in document order, so a cell
    /// written as plain prose and a cell written as a single code span both yield their exact
    /// content — Markdig's own parse, not a regex re-guess at pipe-table syntax.
    /// </summary>
    private static string CellText(TableCell cell)
    {
        StringBuilder builder = new();
        foreach (MarkdownObject node in cell.Descendants())
        {
            switch (node)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    private static string RequireCodeSpan(TableCell cell, string column, string rowLabel)
    {
        CodeInline? code = cell.Descendants<CodeInline>().FirstOrDefault();
        Assert.True(code is not null, $"{rowLabel}: '{column}' cell is not a code span ('{CellText(cell)}').");
        return code!.Content;
    }

    /// <summary>Splits Markdown into level-2 sections, ignoring headings inside code.</summary>
    private static Section[] H2Sections(string markdown)
    {
        HeadingBlock[] headings = Markdown.Parse(markdown, Pipeline)
            .Descendants<HeadingBlock>()
            .Where(heading => heading.Level == 2)
            .ToArray();
        Section[] sections = new Section[headings.Length];
        for (int index = 0; index < headings.Length; index++)
        {
            int start = headings[index].Span.Start;
            int end = index + 1 < headings.Length ? headings[index + 1].Span.Start : markdown.Length;
            sections[index] = new Section(HeadingText(headings[index]), markdown[start..end]);
        }

        return sections;
    }

    /// <summary>
    /// Splits Markdown into level-2 and level-3 sections. A heading's section runs until the next
    /// heading at the same or a shallower level, so a <c>##</c> section's body includes any nested
    /// <c>###</c> subsections while a <c>###</c> section's body stops at the next heading of either level.
    /// </summary>
    private static Section[] HeadingSections(string markdown)
    {
        HeadingBlock[] headings = Markdown.Parse(markdown, Pipeline)
            .Descendants<HeadingBlock>()
            .Where(heading => heading.Level is 2 or 3)
            .ToArray();
        Section[] sections = new Section[headings.Length];
        for (int index = 0; index < headings.Length; index++)
        {
            int start = headings[index].Span.Start;
            int end = markdown.Length;
            for (int next = index + 1; next < headings.Length; next++)
            {
                if (headings[next].Level <= headings[index].Level)
                {
                    end = headings[next].Span.Start;
                    break;
                }
            }

            sections[index] = new Section(HeadingText(headings[index]), markdown[start..end]);
        }

        return sections;
    }

    private static string HeadingText(HeadingBlock heading) =>
        string.Concat(heading.Inline?
            .Descendants<LiteralInline>()
            .Select(literal => literal.Content.ToString()) ?? []).Trim();

    private sealed record DispositionRow(
        string Resource,
        string ContentClass,
        string Disposition,
        string Delivery,
        string Destination,
        string Verification);

    private sealed record LedgerRow(
        string Source,
        string Line,
        string Excerpt,
        string Disposition,
        string Destination,
        string Anchor,
        string Reason);

    private sealed record Section(string Heading, string Body);
}

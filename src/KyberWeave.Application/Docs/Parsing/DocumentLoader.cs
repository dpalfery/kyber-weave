using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Parsing;
using KyberWeave.Domain.FileSystem;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KyberWeave.Core.Docs.Parsing;

/// <summary>
///     Discovers and parses the in-scope documentation corpus, and reads the catalog
///     vocabularies that frontmatter values are validated against.
/// </summary>
public sealed partial class DocumentLoader : IDocumentSource
{
    private readonly IRepositoryFileStore _files;
    private readonly string _catalogPath;
    private readonly OntologyConfig _config;
    private readonly IReadOnlyList<string> _docsRoots;

    private readonly string _repoRoot;
    private readonly IPathResolver _paths;

    public DocumentLoader(
        IRepositoryFileStore files,
        string repoRoot,
        IPathResolver paths,
        IProcessEnvironment environment,
        string docsRelativeRoot = OntologyConfig.DefaultDocsRoot)
        : this(files, repoRoot, paths, environment, OntologyConfig.ProductDefaults.WithDocsRoot(docsRelativeRoot))
    {
    }

    public DocumentLoader(
        IRepositoryFileStore files,
        string repoRoot,
        IPathResolver paths,
        IProcessEnvironment environment,
        OntologyConfig config)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        _files = files;
        ArgumentNullException.ThrowIfNull(config);
        _paths = paths;
        _repoRoot = paths.GetFullPath(repoRoot, environment.CurrentDirectory);
        _config = config;
        _docsRoots = config.DocsRoots;
        _catalogPath = Path.Combine(
            _repoRoot, config.ResolvedCatalogPath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    ///     Path identity follows the same rules as <see cref="DocsRootPath.PathComparer" />,
    ///     so a root the normalizer kept as distinct is not later collapsed when walking files.
    /// </summary>
    private static StringComparer PathComparer => DocsRootPath.PathComparer;

    /// <summary>
    ///     Walks every documentation root in configured order, then folds in the catalog when
    ///     it lives outside all of them.
    /// </summary>
    /// <remarks>
    ///     A document is loaded once even when the roots overlap — a module root nested inside
    ///     a wider one is a reasonable thing for a host to write, and the same file appearing
    ///     twice would report every finding twice and fail its own id-uniqueness check.
    /// </remarks>
    public DocumentSet Load()
    {
        List<DocumentModel> documents = new();
        HashSet<string> visited = new(PathComparer);

        foreach (string docsRoot in _docsRoots)
        {
            string absoluteRoot = Absolute(docsRoot);
            if (!_files.DirectoryExists(absoluteRoot)) continue;

            // Use EnumerateContainedFiles to walk the tree manually one level at a time,
            // skipping symlinks entirely. Directory.EnumerateFiles with SearchOption.AllDirectories
            // invariably follows symlinked directories, creating the link-escaping vulnerability
            // addressed by issue #124 / D3. See IRepositoryFileStore.EnumerateContainedFiles for details.
            foreach (string file in _files
                         .EnumerateContainedFiles(absoluteRoot, "*.md")
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!visited.Add(file)) continue;

                string relative = ToRelative(file);
                if (IsExcluded(relative)) continue;
                documents.Add(Parse(file, relative));
            }
        }

        // The catalog is a governed document wherever it sits. A host that keeps it outside
        // the roots — because no single root is the natural home for it — would otherwise
        // trade frontmatter validation and retrievability for that placement.
        // Skip the catalog if it is, or sits beneath, a symlink — the same containment
        // principle that guards the root walks (D3: never follow a link).
        if (!visited.Contains(_catalogPath) && IsCatalogContained())
            documents.Add(Parse(_catalogPath, ToRelative(_catalogPath)));

        (IReadOnlySet<string>? components, IReadOnlySet<string>? owners) = ReadCatalogVocabularies();

        return new DocumentSet
        {
            Documents = documents,
            Components = components,
            Owners = owners
        };
    }

    private bool IsExcluded(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        if (segments.Any(s => _config.ExcludedPathSegments.Contains(s, StringComparer.OrdinalIgnoreCase))) return true;

        // The KW skill pool is not corpus: its SKILL.md files carry the Agent Skills frontmatter, not
        // a governed document's, and the skill catalog reads them itself.
        if (IsInSkillsPool(relativePath)) return true;

        return _config.ExcludedFiles.Contains(BeneathRoot(relativePath), StringComparer.OrdinalIgnoreCase);
    }

    private bool IsInSkillsPool(string relativePath)
    {
        foreach (string root in _docsRoots)
        {
            string prefix = DocsLayout.SkillsPool(root == "." ? string.Empty : root).TrimStart('/') + "/";
            if (relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    ///     Strips the documentation root a path sits under, because exclusion entries are
    ///     recorded relative to a root rather than to the repository. The longest matching root
    ///     wins, so a nested root's entries stay relative to the nested root.
    /// </summary>
    private string BeneathRoot(string relativePath)
    {
        string? longest = null;
        foreach (string root in _docsRoots)
        {
            if (root == ".")
            {
                longest ??= string.Empty;
                continue;
            }

            string prefix = root + "/";
            if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (longest is null || prefix.Length > longest.Length) longest = prefix;
        }

        return longest is null ? relativePath : relativePath[longest.Length..];
    }

    private string Absolute(string relativePath)
    {
        return relativePath == "."
            ? _repoRoot
            : Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    ///     True when the configured catalog exists and neither it nor a directory beneath its
    ///     root is a symbolic link; see <see cref="IRepositoryFileStore.IsContainedFile" />.
    /// </summary>
    private bool IsCatalogContained()
    {
        return _files.IsContainedFile(_repoRoot, _config.ResolvedCatalogPath, _docsRoots);
    }

    /// <summary>
    ///     Reads one Markdown file into a <see cref="DocumentModel" />, with its frontmatter when it
    ///     has any; a file without frontmatter keeps its whole text as the body.
    /// </summary>
    /// <remarks>
    ///     The corpus walk excludes the archive subtree, so the closeout gate reads an archived
    ///     entry through this narrow entry point instead: one named file, no directory walk,
    ///     no exclusion bypass beyond that file. The named file is held to the same
    ///     containment rule as the walk: an absolute path, a <c>..</c> escape, or any
    ///     symlink on the path fails closed rather than following the link. The refusal is
    ///     an <see cref="ArchivedEntryRefusalException" /> so the closeout gate can record a
    ///     finding from the type. Matching the message would let a reword escape as an
    ///     unhandled error.
    /// </remarks>
    public DocumentModel LoadArchivedDocument(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string normalized = relativePath.Replace('\\', '/');
        string absolute =
            _paths.GetFullPath(normalized.Replace('/', Path.DirectorySeparatorChar), _repoRoot);
        StringComparison comparison = ReferenceEquals(DocsRootPath.PathComparer, StringComparer.OrdinalIgnoreCase)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!absolute.Equals(_repoRoot, comparison)
            && !absolute.StartsWith(_repoRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArchivedEntryRefusalException(
                ArchivedEntryRefusalReason.EscapesRepositoryRoot,
                $"Archived entry '{relativePath}' escapes the repository root.");

        if (!_files.IsContainedFile(_repoRoot, normalized, _docsRoots))
            throw new ArchivedEntryRefusalException(
                ArchivedEntryRefusalReason.CrossesSymbolicLink,
                $"Archived entry '{relativePath}' is not reachable without crossing a symbolic link.");

        return Parse(absolute, normalized);
    }

    /// <summary>
    ///     Reads one Markdown file into a <see cref="DocumentModel" />, with its frontmatter when it
    ///     has any; a file without frontmatter keeps its whole text as the body.
    /// </summary>
    private DocumentModel Parse(string absolutePath, string relativePath)
    {
        string raw = _files.ReadAllText(absolutePath);
        FrontmatterReadResult read = MarkdownFrontmatterReader.Read(raw);

        if (!read.HasFrontmatter)
            return new DocumentModel
            {
                RelativePath = relativePath,
                FilePath = absolutePath,
                HasFrontmatter = false,
                BodyLinks = ExtractRelativeLinks(raw),
                LinkTargets = ExtractBodyLinks(raw),
                Leaves = SplitLeaves(raw, relativePath),
                Body = raw,
                RawMarkdown = raw,
                BodyStartLine = read.BodyStartLine,
                Sections = SplitSections(raw)
            };

        DocumentFrontmatter? frontmatter = null;
        string? parseError = null;
        DocumentFrontmatter sectionOverlay = new();
        string? sectionFrontmatterError = SectionFrontmatterParser.Apply(read.Yaml, sectionOverlay);
        try
        {
            frontmatter = MarkdownFrontmatterReader.Deserializer
                .Deserialize<DocumentFrontmatter>(read.Yaml);
        }
        catch (Exception ex)
        {
            parseError = ex.Message;
        }

        frontmatter ??= new DocumentFrontmatter();
        if (parseError is null && sectionFrontmatterError is null)
        {
            if (!string.IsNullOrWhiteSpace(sectionOverlay.Lead))
            {
                frontmatter.Lead = sectionOverlay.Lead;
            }

            if (sectionOverlay.Sections is not null)
            {
                frontmatter.Sections = sectionOverlay.Sections;
            }
        }

        return new DocumentModel
        {
            RelativePath = relativePath,
            FilePath = absolutePath,
            HasFrontmatter = true,
            ParseError = parseError,
            SectionFrontmatterError = sectionFrontmatterError,
            Frontmatter = frontmatter,
            DocType = ParseDocType(frontmatter.DocType),
            Status = ParseStatus(frontmatter.Status),
            BodyLinks = ExtractRelativeLinks(read.Body),
            LinkTargets = ExtractBodyLinks(read.Body),
            Leaves = SplitLeaves(read.Body, ResolveDocId(frontmatter, relativePath)),
            Body = read.Body,
            RawMarkdown = raw,
            BodyStartLine = read.BodyStartLine,
            Sections = SplitSections(read.Body)
        };
    }

    public static string ResolveDocId(DocumentFrontmatter frontmatter, string relativePath) =>
        DocumentMarkdown.ResolveDocId(frontmatter, relativePath);

    /// <summary>
    ///     GitHub slug fragments declared by headings in <paramref name="body"/>, using the
    ///     same AutoIdentifiers pipeline as retrieval leaves.
    /// </summary>
    public static IReadOnlySet<string> CollectHeadingFragmentSlugs(string body, string docId) =>
        DocumentMarkdown.CollectHeadingFragmentSlugs(body, docId);

    /// <summary>
    ///     Splits a body on <c>##</c> headings. Deeper headings stay inside their parent
    ///     section: a retrieval unit is a topic, and <c>###</c> is a subdivision of one.
    ///     Fenced code blocks are tracked so that a <c>##</c> comment inside a shell fence
    ///     does not open a section.
    /// </summary>
    public static IReadOnlyList<DocumentSection> SplitSections(string body) =>
        DocumentMarkdown.SplitSections(body);

    /// <summary>True when the lines contain something other than headings and blanks.</summary>
    private static bool HasProse(IEnumerable<string> lines)
    {
        return lines.Any(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));
    }

    /// <summary>
    ///     Splits a body into <c>###</c> retrieval leaves with GitHub slug ids. Deeper headings
    ///     stay inside their parent <c>###</c> leaf. Fenced blocks are ordinary content — a
    ///     <c>##</c> line inside a fence is not a section boundary.
    /// </summary>
    public static IReadOnlyList<DocumentLeaf> SplitLeaves(string body, string docId) =>
        DocumentMarkdown.SplitLeaves(body, docId);

    /// <summary>
    ///     Reads relative Markdown links with fragments, anchor text, and 1-based source lines.
    /// </summary>
    public static IReadOnlyList<BodyLink> ExtractBodyLinks(string markdown) =>
        DocumentMarkdown.ExtractBodyLinks(markdown);

    private static Dictionary<HeadingBlock, string> BuildHeadingIds(MarkdownDocument document, string docId)
    {
        Dictionary<HeadingBlock, string> ids = new();
        foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
        {
            string? attributeId = heading.GetAttributes().Id;
            string slug = attributeId ?? SlugifyHeading(PlainText(heading.Inline));
            ids[heading] = $"{docId}#{slug}";
        }

        return ids;
    }

    private static void AppendWithNewline(StringBuilder? buffer, string text)
    {
        if (buffer is null || text.Length == 0) return;

        if (buffer.Length > 0)
        {
            if (buffer[buffer.Length - 1] != '\n') buffer.Append('\n');

            buffer.Append('\n');
        }

        buffer.Append(text);
    }

    private static string Slice(string source, SourceSpan span)
    {
        return source.Substring(span.Start, span.Length);
    }

    private static bool HasProseLines(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.TrimStart().StartsWith('#')) continue;

            return true;
        }

        return false;
    }

    private static string LinkPlainText(LinkInline link)
    {
        StringBuilder builder = new();
        foreach (Inline child in link)
            if (child is LiteralInline literal)
                builder.Append(literal.Content.ToString());

        return builder.ToString();
    }

    private static string PlainText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;

        StringBuilder builder = new();
        foreach (Inline child in inline)
            if (child is LiteralInline literal)
                builder.Append(literal.Content.ToString());

        return builder.ToString();
    }

    private static string SlugifyHeading(string heading)
    {
        StringBuilder builder = new();
        bool lastWasDash = false;
        foreach (char ch in heading.Normalize(NormalizationForm.FormKD))
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }

        return builder.ToString().Trim('-');
    }

    private string ToRelative(string absolutePath)
    {
        return Path.GetRelativePath(_repoRoot, absolutePath).Replace('\\', '/');
    }

    public static IReadOnlyList<string> ExtractRelativeLinks(string markdown) =>
        DocumentMarkdown.ExtractRelativeLinks(markdown);

    /// <summary>
    ///     Reads the Component and Owner columns from the catalog. The catalog is the
    ///     authoritative vocabulary for both; this reader does not duplicate the values.
    /// </summary>
    /// <remarks>
    ///     One file supplies both vocabularies, no matter how many documentation roots the
    ///     repository has. A <c>catalog.md</c> in a second root is an ordinary document:
    ///     merging them would mean a component invented in one module's table is silently
    ///     valid in every other, which is the drift <c>KW-DOC-SPEC-004</c> exists to stop.
    /// </remarks>
    private (IReadOnlySet<string> Components, IReadOnlySet<string> Owners) ReadCatalogVocabularies()
    {
        HashSet<string> components = new(StringComparer.Ordinal);
        HashSet<string> owners = new(StringComparer.Ordinal);

        // Skip reading vocabularies from a catalog that is, or sits beneath, a symlink
        // (D3: never follow a link).
        if (!IsCatalogContained()) return (components, owners);

        foreach (string line in _files.ReadLines(_catalogPath))
        {
            if (!line.StartsWith('|')) continue;

            string[] cells = line.Split('|')
                .Select(c => c.Trim())
                .ToArray();

            // | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            // Split on a leading and trailing pipe yields a leading and trailing empty cell.
            int maxColumn = Math.Max(_config.CatalogComponentColumn, _config.CatalogOwnerColumn);
            if (cells.Length <= maxColumn) continue;

            string component = cells[_config.CatalogComponentColumn];
            string owner = cells[_config.CatalogOwnerColumn];

            if (component.Length == 0 || component.StartsWith("---", StringComparison.Ordinal)) continue;
            if (component.Equals("Component", StringComparison.Ordinal)) continue;

            components.Add(component);
            if (owner.Length > 0 && !owner.StartsWith("---", StringComparison.Ordinal)) owners.Add(owner);
        }

        return (components, owners);
    }


#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
    private static DocType ParseDocType(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "architecture" => DocType.Architecture,
            "onboarding" => DocType.Onboarding,
            "requirements" => DocType.Requirements,
            "adr" => DocType.Adr,
            "plan" => DocType.Plan,
            "spec" => DocType.Spec,
            "todo" => DocType.Todo,
            "runbook" => DocType.Runbook,
            "reference" => DocType.Reference,
            "rule" => DocType.Rule,
            "governance" => DocType.Governance,
            "index" => DocType.Index,
            "coding-standard" => DocType.CodingStandard,
            "skill" => DocType.Skill,
            _ => DocType.Unknown
        };
    }

#pragma warning restore CA1308

    private DocStatus ParseStatus(string? value)
    {
        string? key = StatusKey(value);
        if (key is null)
            return DocStatus.Unknown;

        foreach (string status in _config.Statuses)
        {
            if (string.Equals(StatusKey(status), key, StringComparison.Ordinal))
                return Enum.TryParse(key, ignoreCase: true, out DocStatus parsed) ? parsed : DocStatus.Unknown;
        }

        return DocStatus.Unknown;
    }

    private static string? StatusKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}

using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Parsing;

namespace KyberWeave.Core.Docs.Parsing;

/// <summary>
/// Discovers and parses the in-scope documentation corpus, and reads the catalog
/// vocabularies that frontmatter values are validated against.
/// </summary>
public sealed partial class DocumentLoader
{
    private readonly string _repoRoot;
    private readonly string _docsRoot;
    private readonly OntologyConfig _config;

    public DocumentLoader(string repoRoot, string docsRelativeRoot = "6-Docs")
        : this(repoRoot, OntologyConfig.ProductDefaults.WithDocsRoot(docsRelativeRoot))
    {
    }

    public DocumentLoader(string repoRoot, OntologyConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _docsRoot = Path.Combine(_repoRoot, config.DocsRoot);
    }

    public DocumentSet Load()
    {
        var documents = new List<DocumentModel>();

        if (Directory.Exists(_docsRoot))
        {
            foreach (var file in Directory
                         .EnumerateFiles(_docsRoot, "*.md", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var relative = ToRelative(file);
                if (IsExcluded(relative)) continue;
                documents.Add(Parse(file, relative));
            }
        }

        var (components, owners) = ReadCatalogVocabularies();

        return new DocumentSet
        {
            Documents = documents,
            Components = components,
            Owners = owners
        };
    }

    internal bool IsExcluded(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(s => _config.ExcludedPathSegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Exclusion file paths are recorded relative to the docs root.
        var prefix = _config.DocsRoot.TrimEnd('/') + "/";
        var beneathDocs = relativePath.StartsWith(prefix, StringComparison.Ordinal)
            ? relativePath[prefix.Length..]
            : relativePath;

        return _config.ExcludedFiles.Contains(beneathDocs, StringComparer.OrdinalIgnoreCase);
    }

    private DocumentModel Parse(string absolutePath, string relativePath)
    {
        var raw = File.ReadAllText(absolutePath);
        var read = MarkdownFrontmatterReader.Read(raw);

        if (!read.HasFrontmatter)
        {
            return new DocumentModel
            {
                RelativePath = relativePath,
                FilePath = absolutePath,
                HasFrontmatter = false,
                BodyLinks = ExtractRelativeLinks(raw),
                Body = raw,
                Sections = SplitSections(raw)
            };
        }

        DocumentFrontmatter? frontmatter = null;
        string? parseError = null;
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

        return new DocumentModel
        {
            RelativePath = relativePath,
            FilePath = absolutePath,
            HasFrontmatter = true,
            ParseError = parseError,
            Frontmatter = frontmatter,
            DocType = ParseDocType(frontmatter.DocType),
            Status = ParseStatus(frontmatter.Status),
            BodyLinks = ExtractRelativeLinks(read.Body),
            Body = read.Body,
            Sections = SplitSections(read.Body)
        };
    }

    /// <summary>
    /// Splits a body on <c>##</c> headings. Deeper headings stay inside their parent
    /// section: a retrieval unit is a topic, and <c>###</c> is a subdivision of one.
    /// Fenced code blocks are tracked so that a <c>##</c> comment inside a shell fence
    /// does not open a section.
    /// </summary>
    internal static IReadOnlyList<DocumentSection> SplitSections(string body)
    {
        var sections = new List<DocumentSection>();
        var lines = body.Replace("\r\n", "\n").Split('\n');

        string? heading = null;
        var headingLine = 1;
        var buffer = new List<string>();
        var inFence = false;

        void Flush()
        {
            // The leading run before the first '##' is usually nothing but the H1, which
            // the caller already has as the document title. Emitting it as a retrievable
            // section makes a title-only stub compete for relevance against real prose —
            // and win, because a two-word section scores high on cosine similarity.
            if (heading is null && !HasProse(buffer)) return;

            sections.Add(new DocumentSection(
                heading ?? string.Empty,
                string.Join("\n", buffer).Trim('\n'),
                headingLine));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
                headingLine = i + 1;
                buffer = [];
                continue;
            }

            buffer.Add(line);
        }

        Flush();
        return sections;
    }

    /// <summary>True when the lines contain something other than headings and blanks.</summary>
    private static bool HasProse(IEnumerable<string> lines) =>
        lines.Any(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));

    internal static DocType ParseDocType(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "architecture" => DocType.Architecture,
            "onboarding" => DocType.Onboarding,
            "requirements" => DocType.Requirements,
            "adr" => DocType.Adr,
            "plan" => DocType.Plan,
            "spec" => DocType.Spec,
            "runbook" => DocType.Runbook,
            "reference" => DocType.Reference,
            "rule" => DocType.Rule,
            "governance" => DocType.Governance,
            "index" => DocType.Index,
            _ => DocType.Unknown
        };

    internal static DocStatus ParseStatus(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "current" => DocStatus.Current,
            "draft" => DocStatus.Draft,
            "needs-review" => DocStatus.NeedsReview,
            "superseded" => DocStatus.Superseded,
            _ => DocStatus.Unknown
        };

    private string ToRelative(string absolutePath) =>
        Path.GetRelativePath(_repoRoot, absolutePath).Replace('\\', '/');

    internal static IReadOnlyList<string> ExtractRelativeLinks(string markdown)
    {
        var links = new List<string>();
        foreach (Match match in LinkPattern().Matches(markdown))
        {
            var target = match.Groups[1].Value.Trim().Trim('<', '>');
            var hashIndex = target.IndexOf('#', StringComparison.Ordinal);
            if (hashIndex >= 0) target = target[..hashIndex];
            if (target.Length == 0) continue;
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
            if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            if (target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (target.StartsWith('/')) continue;
            links.Add(target);
        }

        return links;
    }

    /// <summary>
    /// Reads the Component and Owner columns from the catalog. The catalog is the
    /// authoritative vocabulary for both; this reader does not duplicate the values.
    /// </summary>
    private (IReadOnlySet<string> Components, IReadOnlySet<string> Owners) ReadCatalogVocabularies()
    {
        var components = new HashSet<string>(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);

        var catalogPath = Path.Combine(_docsRoot, "catalog.md");
        if (!File.Exists(catalogPath))
        {
            return (components, owners);
        }

        foreach (var line in File.ReadLines(catalogPath))
        {
            if (!line.StartsWith('|')) continue;

            var cells = line.Split('|', StringSplitOptions.None)
                .Select(c => c.Trim())
                .ToArray();

            // | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            // Split on a leading and trailing pipe yields a leading and trailing empty cell.
            var maxColumn = Math.Max(_config.CatalogComponentColumn, _config.CatalogOwnerColumn);
            if (cells.Length <= maxColumn) continue;

            var component = cells[_config.CatalogComponentColumn];
            var owner = cells[_config.CatalogOwnerColumn];

            if (component.Length == 0 || component.StartsWith("---", StringComparison.Ordinal)) continue;
            if (component.Equals("Component", StringComparison.Ordinal)) continue;

            components.Add(component);
            if (owner.Length > 0 && !owner.StartsWith("---", StringComparison.Ordinal))
            {
                owners.Add(owner);
            }
        }

        return (components, owners);
    }

    [GeneratedRegex(@"\]\(([^)]*)\)")]
    private static partial Regex LinkPattern();
}

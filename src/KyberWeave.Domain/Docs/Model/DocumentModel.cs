using System.Collections.ObjectModel;
using System.Globalization;

namespace KyberWeave.Core.Docs.Model;

/// <summary>Document currency. Distinct from a plan's implementation lifecycle.</summary>
public enum DocStatus
{
    Unknown = 0,
    Current,
    Draft,
    NeedsReview,
    Superseded
}

/// <summary>
/// Frontmatter as authored, before validation. Every field is nullable so that a missing
/// or malformed key is reported as a diagnostic rather than throwing during parse.
/// </summary>
public sealed record DocumentFrontmatter
{
    /// <summary>Creates an empty frontmatter; deserialization starts here, so every field stays nullable.</summary>
    public DocumentFrontmatter()
    {
    }

    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? DocType { get; init; }
    public string? Status { get; init; }
    public string? Component { get; init; }
    public string? SourceRoot { get; init; }
    public string? Owner { get; init; }
    public string? LastReviewed { get; init; }
    public string? Technology { get; init; }

    /// <summary>The role a rules or skills document governs, declared in <c>ontology.roles</c>.</summary>
    public string? Role { get; init; }

    /// <summary>The one technology this template builds on, whose rules and skills it also receives.</summary>
    public string? BuildsOn { get; init; }

    public Collection<string>? CodeRefs { get; init; }
    public Collection<string>? ApiEndpoints { get; init; }
    public Collection<string>? DecidedBy { get; init; }
    public Collection<string>? Supersedes { get; init; }

    /// <summary>Domain synonyms, acronyms, or search aliases for discovery.</summary>
    public Collection<string>? Keywords { get; init; }

    /// <summary>Synonym for keywords, accepted for backwards compatibility.</summary>
    public Collection<string>? Aliases { get; init; }

    /// <summary>How this document is affected by the change under review, when declared.</summary>
    public string? DocumentationImpact { get; init; }

    /// <summary>Components this document's change touches, for closeout evidence.</summary>
    public Collection<string>? ImpactComponents { get; init; }

    /// <summary>
    /// Heading slug of the leaf an exact document-id query returns; default is the preamble
    /// (<c>{doc-id}#</c>).
    /// </summary>
    public string? Lead { get; init; }

    /// <summary>
    /// Per-section keywords and code-refs keyed by heading slug; preamble uses
    /// <c>#</c>.
    /// </summary>
    public Dictionary<string, DocumentSectionVocabulary>? Sections { get; init; }
}

/// <summary>Keywords and code-refs declared for one section slug.</summary>
public sealed class DocumentSectionVocabulary
{
    public Collection<string>? Keywords { get; init; }

    public Collection<string>? CodeRefs { get; init; }
}

/// <summary>One <c>##</c> section of a document body.</summary>
/// <param name="Heading">The heading text, without the leading hashes.</param>
/// <param name="Body">Everything under the heading up to the next <c>##</c>.</param>
/// <param name="LineNumber">1-based line of the heading within the file.</param>
/// <param name="BodyLine">
/// 1-based line of <paramref name="Body"/>'s first line. The split trims blank lines from the
/// body's edges, so this is not always <paramref name="LineNumber"/> + 1; 0 when unknown.
/// </param>
public sealed record DocumentSection(string Heading, string Body, int LineNumber, int BodyLine = 0);

/// <summary>
/// One retrievable <c>###</c> leaf (or preamble / section lead-in) with a stable
/// <c>{doc-id}#{github-slug}</c> identity.
/// </summary>
/// <param name="Id">The leaf id, including the document id prefix.</param>
/// <param name="Body">Prose under the leaf boundary, without the declaring heading line.</param>
public sealed record DocumentLeaf(string Id, string Body);

/// <summary>
/// A relative Markdown link in the body, with fragment, visible text, and source line.
/// </summary>
public sealed record BodyLink(string Path, string Fragment, string Text, int Line);

/// <summary>
/// One parsed documentation file: its frontmatter, its resolved enum values, and the
/// relative links found in its body.
/// </summary>
public sealed class DocumentModel
{
    /// <summary>Repository-relative path, forward-slashed.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Absolute path on disk.</summary>
    public required string FilePath { get; init; }

    /// <summary>True when a frontmatter block was present at all.</summary>
    public required bool HasFrontmatter { get; init; }

    /// <summary>Set when the frontmatter block was present but could not be deserialized.</summary>
    public string? ParseError { get; init; }

    /// <summary>Set when optional <c>lead</c> or <c>sections</c> frontmatter could not be parsed.</summary>
    public string? SectionFrontmatterError { get; init; }

    public DocumentFrontmatter Frontmatter { get; init; } = new();

    public DocType DocType { get; init; }

    public DocStatus Status { get; init; }

    /// <summary>Relative Markdown links found in the body, for LINKS_TO edges.</summary>
    public IReadOnlyList<string> BodyLinks { get; init; } = [];

    /// <summary>
    /// Relative links with fragments, anchor text, and source lines. Same-document
    /// <c>#fragment</c> links use an empty <see cref="BodyLink.Path"/>.
    /// </summary>
    public IReadOnlyList<BodyLink> LinkTargets { get; init; } = [];

    /// <summary>
    /// <c>###</c> retrieval leaves with GitHub slug ids, including preamble and
    /// section lead-ins before the first <c>###</c>.
    /// </summary>
    public IReadOnlyList<DocumentLeaf> Leaves { get; init; } = [];

    /// <summary>
    /// The Markdown body, frontmatter removed. Retrieval returns prose, so the corpus is
    /// held in memory — roughly 600 KB across the in-scope documents.
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// The source Markdown exactly as read from disk. Analysis uses this to keep source
    /// locations and frontmatter boundaries while retrieval continues to consume
    /// <see cref="Body"/>.
    /// </summary>
    public string RawMarkdown { get; init; } = string.Empty;

    /// <summary>1-based source line on which <see cref="Body"/> begins.</summary>
    public int BodyStartLine { get; init; } = 1;

    /// <summary>
    /// The body split on <c>##</c> headings. Retrieval returns the one relevant section
    /// rather than the whole file, which is the difference between an answer and a dump.
    /// </summary>
    public IReadOnlyList<DocumentSection> Sections { get; init; } = [];

    /// <summary>
    /// The document id, or the relative path when no id was authored. Diagnostics need a
    /// subject even for a document that failed to declare one.
    /// </summary>
    public string Subject =>
        string.IsNullOrWhiteSpace(Frontmatter.Id) ? RelativePath : Frontmatter.Id!;

    public IReadOnlyList<string> ApiEndpoints => Frontmatter.ApiEndpoints ?? [];
    public IReadOnlyList<string> DecidedBy => Frontmatter.DecidedBy ?? [];
    public IReadOnlyList<string> Supersedes => Frontmatter.Supersedes ?? [];

    /// <summary>
    /// Leaf id that receives the exact document-id lead bonus; preamble when
    /// <see cref="DocumentFrontmatter.Lead"/> is absent.
    /// </summary>
    public string LeadLeafId
    {
        get
        {
            string docId = string.IsNullOrWhiteSpace(Frontmatter.Id) ? RelativePath : Frontmatter.Id!;
            string? slug = Frontmatter.Lead?.Trim();
            if (string.IsNullOrEmpty(slug))
            {
                return string.Create(CultureInfo.InvariantCulture, $"{docId}#");
            }

            return string.Create(CultureInfo.InvariantCulture, $"{docId}#{slug}");
        }
    }
}

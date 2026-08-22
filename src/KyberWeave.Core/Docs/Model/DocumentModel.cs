using System.Collections.ObjectModel;

namespace KyberWeave.Core.Docs.Model;

/// <summary>
/// The closed set of document shapes. Adding a member is a change to the documentation
/// ontology, not an authoring decision.
/// </summary>
public enum DocType
{
    Unknown = 0,
    Architecture,
    Onboarding,
    Requirements,
    Adr,
    Plan,
    Spec,
    Todo,
    Runbook,
    Reference,
    Rule,
    Governance,
    Index,

    /// <summary>
    /// A technology's coding standard: how code of one stack is written in this repository.
    /// Distinct from <see cref="Rule"/>, which governs the repository as a whole, because a
    /// standard is scoped by the <c>technology</c> key and is what a portable agent looks up
    /// when it needs the local answer rather than the one it shipped with.
    /// </summary>
    CodingStandard
}

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
public sealed class DocumentFrontmatter
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? DocType { get; set; }
    public string? Status { get; set; }
    public string? Component { get; set; }
    public string? SourceRoot { get; set; }
    public string? Owner { get; set; }
    public string? LastReviewed { get; set; }
    public string? Technology { get; set; }
    public Collection<string>? CodeRefs { get; set; }
    public Collection<string>? ApiEndpoints { get; set; }
    public Collection<string>? DecidedBy { get; set; }
    public Collection<string>? Supersedes { get; set; }
}

/// <summary>One <c>##</c> section of a document body.</summary>
/// <param name="Heading">The heading text, without the leading hashes.</param>
/// <param name="Body">Everything under the heading up to the next <c>##</c>.</param>
/// <param name="LineNumber">1-based line of the heading within the file.</param>
public sealed record DocumentSection(string Heading, string Body, int LineNumber);

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

    public DocumentFrontmatter Frontmatter { get; init; } = new();

    public DocType DocType { get; init; }

    public DocStatus Status { get; init; }

    /// <summary>Relative Markdown links found in the body, for LINKS_TO edges.</summary>
    public IReadOnlyList<string> BodyLinks { get; init; } = [];

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

    public IReadOnlyList<string> CodeRefs => Frontmatter.CodeRefs ?? [];
    public IReadOnlyList<string> ApiEndpoints => Frontmatter.ApiEndpoints ?? [];
    public IReadOnlyList<string> DecidedBy => Frontmatter.DecidedBy ?? [];
    public IReadOnlyList<string> Supersedes => Frontmatter.Supersedes ?? [];
}

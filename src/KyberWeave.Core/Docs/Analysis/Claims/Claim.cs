namespace KyberWeave.Core.Docs.Analysis.Claims;

/// <summary>The Markdown structure from which a documentation claim was extracted.</summary>
public enum ClaimKind
{
    Paragraph,
    ListItem,
    TableRow,
    CodeBlock
}

/// <summary>Analysis rules explicitly suppressed for one source claim.</summary>
[Flags]
public enum IgnoreRule
{
    None = 0,
    Duplicate = 1,
    Conflict = 2,
    Terminology = 4,
    All = Duplicate | Conflict | Terminology
}

/// <summary>One line-addressable unit used by documentation analysis.</summary>
public sealed record Claim(
    ClaimKind Kind,
    string Text,
    string ContextualText,
    string ContentHash,
    string ContextualHash,
    string DocumentIdentity,
    string Component,
    string Section,
    string FilePath,
    int StartLine,
    int EndLine,
    IgnoreRule IgnoreRules,
    string? FenceInfo = null,
    IReadOnlyList<string>? CodeRefs = null)
{
    public IReadOnlyList<string> CodeRefs { get; } = CodeRefs ?? [];
}

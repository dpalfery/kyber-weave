using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Core.Docs.Analysis.Glossary;

/// <summary>The exact review states supported by a managed glossary row.</summary>
public enum GlossarySenseStatus
{
    Proposed,
    Approved,
    Rejected
}

/// <summary>A generated glossary proposal and the claims that support it.</summary>
public sealed record GlossaryProposal(
    string Term,
    string Definition,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> EvidenceIds);

/// <summary>One parsed sense returned by glossary lookup.</summary>
public sealed record GlossarySense(
    string Id,
    GlossarySenseStatus Status,
    string Definition,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string>? EvidenceIds = null)
{
    /// <summary>Opaque claim identities from the managed generated-evidence block.</summary>
    public IReadOnlyList<string> EvidenceIds { get; } = EvidenceIds ?? [];
}

/// <summary>All managed senses declared for one glossary term.</summary>
public sealed record GlossaryLookupResult(string Term, IReadOnlyList<GlossarySense> Senses);

/// <summary>Parsed glossary data used by analysis and conversational lookup.</summary>
public sealed record ManagedGlossaryLoadResult(
    AnalysisGlossary AnalysisGlossary,
    IReadOnlyList<GlossaryLookupResult> Terms);

/// <summary>The result of previewing or writing a conservative glossary merge.</summary>
public sealed record GlossaryUpdateResult(
    string RelativePath,
    string Markdown,
    bool Changed,
    bool Written,
    DiagnosticReport Diagnostics);

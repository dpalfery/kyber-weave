namespace KyberWeave.Core.Docs.Search;

/// <summary>
/// One resolved join from a document's frontmatter to the code graph.
/// </summary>
/// <param name="Reference">The <c>code-refs</c> or <c>api-endpoints</c> entry as authored.</param>
/// <param name="Kind">The indexed node kind, or <c>unresolved</c>.</param>
/// <param name="Location">"file:line"-style location, or an empty string when unresolved.</param>
/// <param name="InSourceRoot">
/// True when the chosen symbol lives beneath the document's declared <c>source-root</c>.
/// False means the name resolved only outside the component the document describes, which
/// is weak evidence: bare symbol names collide freely across projects and languages.
/// </param>
/// <param name="OtherCandidates">
/// How many further symbols share this bare name. Non-zero means the join is a best guess,
/// and callers should say so rather than present it as fact.
/// </param>
public sealed record CodeJoin(
    string Reference,
    string Kind,
    string Location,
    bool InSourceRoot = true,
    int OtherCandidates = 0);

namespace KyberWeave.Core.Arbiter;

/// <summary>One bot review comment to classify.</summary>
/// <param name="Path">The repository-relative path the comment cites.</param>
/// <param name="Line">The 1-based line the comment cites.</param>
/// <param name="Body">The comment text.</param>
/// <param name="CommitSha">The commit the comment was made on, when known; recorded with the decision as evidence.</param>
public sealed record CommentTriageRequest(string Path, int Line, string Body, string? CommitSha);

/// <summary>The class of one bot review comment and the decision it came from.</summary>
/// <param name="Class">The class the caller acts on: <c>real</c>, <c>nitpick</c> or <c>already-fixed</c>. Always <c>real</c> while the rule is in shadow.</param>
/// <param name="Confidence">The provider's confidence in the class, when it reported one.</param>
/// <param name="DecisionId">The id of the decision record written for this classification.</param>
/// <param name="Shadow">Whether the rule is in shadow, so <paramref name="Class"/> is log-only and not the model's answer.</param>
/// <param name="WouldClass">In shadow, the class the model would have delivered without shadow; null when not in shadow or when the model gave no class.</param>
/// <param name="Status">The provider status the decision carries: <c>answered</c>, or <c>not-evaluated</c> when no question was asked (a disabled or absent rule, or no provider).</param>
public sealed record CommentTriageResult(string Class, double? Confidence, string DecisionId, bool Shadow, string? WouldClass, string Status);

/// <summary>A bot's PR-level summary comment to classify row by row.</summary>
/// <param name="Body">The summary text, which may carry a findings table.</param>
/// <param name="Author">The login that posted the summary, when known; an absent author is never assumed to be a bot.</param>
public sealed record CommentSummaryRequest(string Body, string? Author);

/// <summary>One row of a summary's findings table and the class it was given.</summary>
/// <param name="Path">The path the row cites.</param>
/// <param name="Line">The start line the row cites.</param>
/// <param name="Issue">The issue text the row states.</param>
/// <param name="Class">One of <c>real</c>, <c>nitpick</c> or <c>already-fixed</c>.</param>
/// <param name="Confidence">The provider's confidence, when the row was sent to it and it reported one.</param>
/// <param name="DecisionId">The decision record the row was classified under; empty when the row was never sent.</param>
/// <param name="Shadow">Whether the rule is in shadow, so <paramref name="Class"/> is log-only; reported for every row, sent or not.</param>
/// <param name="WouldClass">In shadow, the class the model would have delivered for the row; null when the row was not answered.</param>
/// <param name="Status">The provider status of the row's decision; <c>not-evaluated</c> when the row was never asked.</param>
public sealed record CommentSummaryItem(
    string Path,
    int Line,
    string Issue,
    string Class,
    double? Confidence,
    string DecisionId,
    bool Shadow,
    string? WouldClass,
    string Status);

/// <summary>The classified rows of one summary comment, in table order.</summary>
/// <param name="Items">One item per table row; empty when the comment has no table or a row was malformed.</param>
public sealed record CommentSummaryResult(IReadOnlyList<CommentSummaryItem> Items);

using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Search;

/// <summary>One <c>docs_answer_question_from_repo_documentation</c> answer: a whole leaf, confidence, and optional continuation.</summary>
public sealed record ExploreAnswer(
    bool IsMiss,
    bool IsInvalidCursor,
    string? InvalidCursorMessage,
    DocumentModel? SourceDocument,
    string? SourceHeading,
    string Prose,
    double Confidence,
    bool Incomplete,
    string? CursorToken,
    int DocumentsConsidered,
    bool MoreAvailable = false)
{
    /// <summary>Hard ceiling on characters of leaf prose returned in one call: the limit of an explicit continuation.</summary>
    public const int AnswerCharCap = 24000;

    /// <summary>
    /// Characters a first answer aims for: the best-matching paragraphs with enough neighbouring
    /// context to stand alone. A window that answers is a complete answer, however long its section.
    /// </summary>
    public const int WindowCharBudget = 3000;

    public static ExploreAnswer Miss(int documentsConsidered)
    {
        return new ExploreAnswer(
            true,
            false,
            null,
            null,
            null,
            string.Empty,
            0,
            false,
            null,
            documentsConsidered);
    }

    public static ExploreAnswer InvalidCursor(string message)
    {
        return new ExploreAnswer(
            false,
            true,
            message,
            null,
            null,
            string.Empty,
            0,
            false,
            null,
            0);
    }
}

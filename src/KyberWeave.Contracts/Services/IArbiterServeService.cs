using KyberWeave.Arbiter.Mcp;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// What the Arbiter's serve surface answers: the same decisions the hook makes, offered for a caller that no harness
/// hook gates. Each method returns the text the MCP tool of the same name hands back; the two fail-safe answer tools
/// await their provider call, so they complete as a task.
/// </summary>
public interface IArbiterServeService
{
    /// <summary>Evaluates one event and returns the outcome the hook would return for it.</summary>
    /// <param name="trigger">The trigger the event classifies as.</param>
    /// <param name="facts">The facts the caller asserts about the event.</param>
    /// <returns>The provenance line, the outcome and, for a non-allow, the escalation envelope or note.</returns>
    string Evaluate(string trigger, ArbiterFacts facts);

    /// <summary>Lists the effective rules.</summary>
    /// <param name="trigger">A trigger to list the rules of, or null for every trigger.</param>
    /// <returns>The provenance line and one row per rule.</returns>
    string Rules(string? trigger = null);

    /// <summary>Reports whether the Arbiter is ready: root, rule set, provider and whether its key resolves.</summary>
    /// <param name="cancellationToken">Cancels the credential-store lookup.</param>
    /// <returns>The provenance line and the status.</returns>
    Task<string> StatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the sections of one repository document a task needs.</summary>
    /// <param name="path">The repository-relative document path.</param>
    /// <param name="task">The task the sections are selected for.</param>
    /// <param name="maxSections">The most sections to list; null lists every selected section.</param>
    /// <param name="cancellationToken">Cancels the read and the provider call.</param>
    /// <returns>JSON with the sections, or an error.</returns>
    Task<string> SelectRelevantDocumentSectionsAsync(string path, string task, int? maxSections = null, CancellationToken cancellationToken = default);

    /// <summary>Classifies a bot review comment, or each row of a bot's summary, as real, a nitpick or already fixed.</summary>
    /// <param name="path">The repository-relative path the comment cites.</param>
    /// <param name="line">The 1-based line the comment cites.</param>
    /// <param name="body">The comment text, or the summary text.</param>
    /// <param name="commitSha">The commit the comment was made on, when known.</param>
    /// <param name="summary">True when the body is a PR-level summary with a findings table.</param>
    /// <param name="author">The login that posted the summary, when known.</param>
    /// <param name="cancellationToken">Cancels the provider call.</param>
    /// <returns>JSON with the classification, or an error.</returns>
    Task<string> ClassifyBotReviewCommentAsync(string path, int line, string body, string? commitSha = null, bool summary = false, string? author = null, CancellationToken cancellationToken = default);
}

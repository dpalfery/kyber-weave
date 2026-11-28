using System.ComponentModel;
using KyberWeave.Contracts.Services;
using KyberWeave.Arbiter.Mcp;
using ModelContextProtocol.Server;

namespace KyberWeave.Arbiter.Mcp;

/// <summary>
/// The Arbiter tools the serve surface answers over stdio: the three decision tools for the D4 fallback (evaluate an
/// event, list the rules, report readiness), and two fail-safe answer tools (select relevant document sections,
/// classify a bot review comment). Each tool names itself and its parameters for the client and hands the call to the
/// serve service, which decides and formats the answer.
/// </summary>
[McpServerToolType]
public sealed class ArbiterTools(IArbiterServeService service)
{
    [McpServerTool(Name = "arbiter_evaluate_agent_dispatch_or_return", ReadOnly = false, OpenWorld = false)]
    [Description("""
        Evaluates one Arbiter event for the D4 fallback, where no harness hook fires, and returns the outcome the hook would return for the same event: allow, an escalation envelope, or a review note. Use when the conductor is about to dispatch a Squad agent or has received a return, and when code-reviewer is about to fan out lenses or refute.spawn refutations. Pass the trigger the event classifies as and the asserted facts: target, plan-file, task, prompt, the lens names as lens, the findings to be refuted as finding, the phase and the output. A lens call or a finding call needs no target: the tool routes both to review-lens and synthesizes the routing header block (KYBER-ARBITER, LENS, REFUTE) itself, exactly as the hook path would see it. The facts are asserted by the calling agent, so the decision is advisory. Each call appends to the decision ledger and log.
        """)]
    public async Task<string> EvaluateAsync(
        [Description("The trigger the event classifies as, for example delegate, delegate.returned, lens.spawn, refute.spawn or investigate.")]
        string trigger,
        [Description("The asserted facts of the event.")]
        ArbiterFacts facts,
        CancellationToken cancellationToken = default)
        => await service.EvaluateAsync(trigger, facts, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "arbiter_list_effective_rules_by_trigger", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Lists the Arbiter rules the effective configuration sets, one row per rule, with its step, question, answers and effects. Use when asked which rules decide a trigger such as delegate, lens.spawn or gate.select, or what a named rule asks and changes. Use to explain a block before retrying a dispatch. Do not use to decide whether a live dispatch is allowed; use arbiter_evaluate_agent_dispatch_or_return for that.
        """)]
    public string Rules(
        [Description("A trigger to list the rules of. Omit to list every trigger's rules.")]
        string? trigger = null)
        => service.Rules(trigger);

    [McpServerTool(Name = "arbiter_check_configuration_and_key_readiness", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Reports whether the Arbiter is ready for this repository: the root it answers for, the rule-set hash and rule count, the provider, and whether its key resolves. Use when checking configuration before relying on arbiter_evaluate_agent_dispatch_or_return, or when a dispatch failed closed on a configuration or key error. The key value is never reported, only whether one was found.
        """)]
    public Task<string> StatusAsync(CancellationToken cancellationToken = default)
        => service.StatusAsync(cancellationToken);

    /// <summary>
    /// Lists the sections of one repository document a task needs. Returns JSON with <c>sections</c>, <c>failedSafe</c>
    /// and <c>truncated</c>, or an <c>error</c> when the document is missing or outside the root.
    /// </summary>
    /// <param name="path">The repository-relative document path.</param>
    /// <param name="task">The task the sections are selected for.</param>
    /// <param name="maxSections">The most sections to list; null lists every selected section.</param>
    [McpServerTool(Name = "arbiter_select_relevant_document_sections", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Lists the sections of one repository markdown document that a task needs, each with its id, title and line range, so a worker reads only the sections that bear on the task. Fails safe: when the Arbiter is not configured, unsure, or its provider fails, every section is listed. Returns JSON; a missing document is an error.
        """)]
    public async Task<string> SelectRelevantDocumentSectionsAsync(
        [Description("The repository-relative path of the markdown document.")]
        string path,
        [Description("The task the worker is about to do, in plain words.")]
        string task,
        [Description("The most sections to list. Omit to list every selected section.")]
        int? maxSections = null,
        CancellationToken cancellationToken = default)
        => await service.SelectRelevantDocumentSectionsAsync(path, task, maxSections, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Classifies one bot review comment as <c>real</c>, <c>nitpick</c> or <c>already-fixed</c> against the code at the
    /// cited line, and records the decision. Returns JSON with <c>class</c>, <c>confidence</c>, <c>decisionId</c>,
    /// <c>shadow</c>, <c>wouldClass</c> and <c>status</c>. While the rule is in shadow <c>class</c> is always <c>real</c>.
    /// A disabled or absent rule, or no provider, reports <c>status</c> <c>not-evaluated</c>.
    /// </summary>
    /// <param name="path">The repository-relative path the comment cites.</param>
    /// <param name="line">The 1-based line the comment cites.</param>
    /// <param name="body">The comment text.</param>
    /// <param name="commitSha">The commit the comment was made on, when known.</param>
    /// <param name="summary">When true, <paramref name="body"/> is a PR-level summary; <paramref name="path"/> and <paramref name="line"/> are ignored and each table row is classified.</param>
    /// <param name="author">The login that posted the summary, when known. Only a configured bot's summary is classified.</param>
    [McpServerTool(Name = "arbiter_classify_bot_review_comment", ReadOnly = false, OpenWorld = false)]
    [Description("""
        Classifies one bot review comment as real, nitpick or already-fixed, judging the code at the cited line as it is now. Use before fixing a bot review comment, to skip one that is already fixed or a nitpick. Fails safe to real: any answer that is unsure, below the floor, or not obtained keeps the comment real. Each call records a decision and returns its id. While the rule is in shadow (log-only), class is always real and wouldClass gives the class the model would have returned; shadow true says so. With summary true, the body is a bot's PR-level summary and each row of its findings table is classified; the answer carries items, each with the same fields. Returns JSON.
        """)]
    public async Task<string> ClassifyBotReviewCommentAsync(
        [Description("The repository-relative path the comment cites. Ignored when summary is true.")]
        string path,
        [Description("The 1-based line the comment cites. Ignored when summary is true.")]
        int line,
        [Description("The comment text, or the summary text when summary is true.")]
        string body,
        [Description("The commit the comment was made on, when known.")]
        string? commitSha = null,
        [Description("True when the body is a PR-level summary with a findings table.")]
        bool summary = false,
        [Description("The login that posted the summary, when known.")]
        string? author = null,
        CancellationToken cancellationToken = default)
        => await service.ClassifyBotReviewCommentAsync(path, line, body, commitSha, summary, author, cancellationToken).ConfigureAwait(false);
}

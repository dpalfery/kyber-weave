using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter;

/// <summary>One bot review comment to classify.</summary>
/// <param name="Path">The repository-relative path the comment cites.</param>
/// <param name="Line">The 1-based line the comment cites.</param>
/// <param name="Body">The comment text.</param>
/// <param name="CommitSha">The commit the comment was made on, when known; recorded with the decision as evidence.</param>
public sealed record CommentTriageRequest(string Path, int Line, string Body, string? CommitSha);

/// <summary>The class of one bot review comment and the decision it came from.</summary>
/// <param name="Class">One of <c>real</c>, <c>nitpick</c> or <c>already-fixed</c>.</param>
/// <param name="Confidence">The provider's confidence in the class, when it reported one.</param>
/// <param name="DecisionId">The id of the decision record written for this classification.</param>
public sealed record CommentTriageResult(string Class, double? Confidence, string DecisionId);

/// <summary>
/// Classifies one bot review comment against the code as it is now, through <c>KW-ARB-COMMENT-001</c>. Every call
/// writes a decision record. The classification fails safe to <c>real</c>: a missing cited file, an answer below the
/// rule's floor, an answer that was not sent, or a provider failure all keep the comment real, so a worker never
/// skips a comment on a shaky answer.
/// </summary>
/// <remarks>
/// The rule ships in shadow, so its effect is recorded as neutral and the class it would have produced as
/// <c>would-effect</c>. The raw answer the model gave is logged even when the floor refuses it, because threshold
/// tuning reads exactly that answer.
/// </remarks>
/// <param name="provider">The provider that answers the question; <c>NoneProvider</c> answers nothing.</param>
/// <param name="repoRoot">The repository root the cited path is read under.</param>
/// <param name="arbiterDirectory">The directory the decision log is written to.</param>
/// <param name="clock">Stamps the decision record and allocates its id.</param>
/// <param name="ruleSet">The rule-set hash the decision records, when the caller has one; empty otherwise.</param>
public sealed class CommentTriageService(
    IArbiterProvider provider,
    string repoRoot,
    string arbiterDirectory,
    TimeProvider clock,
    string? ruleSet = null)
{
    /// <summary>The shipped rule that classifies a comment.</summary>
    public const string CommentRuleId = "KW-ARB-COMMENT-001";

    private const string Real = "real";
    private const string Nitpick = "nitpick";
    private const string AlreadyFixed = "already-fixed";
    private const string TriggerName = "comment.triage";

    /// <summary>Classifies <paramref name="request"/> and records the decision.</summary>
    /// <param name="request">The comment to classify.</param>
    /// <param name="ct">Cancels the provider call.</param>
    /// <returns>The class, the confidence and the decision id; never throws for a provider failure.</returns>
    public async Task<CommentTriageResult> ClassifyAsync(CommentTriageRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = clock.GetUtcNow();
        string decisionId = ArbiterRecordId.New(now);
        ArbiterRule? rule = ArbiterConfig.ProductDefaults.Rules
            .FirstOrDefault(candidate => string.Equals(candidate.Id, CommentRuleId, StringComparison.Ordinal) && candidate.Ask is not null);
        FileExcerpt? excerpt = FileTextFacts.ReadExcerpt(repoRoot, request.Path, request.Line);

        ArbiterStep1BatchResult? batch = null;
        string status = ArbiterProviderStatuses.NotEvaluated;
        if (rule is not null && excerpt is not null)
        {
            ArbiterFactSet facts = new(new Dictionary<string, ArbiterFact>(StringComparer.Ordinal)
            {
                ["comment.body"] = new(request.Body, ArbiterFactLabel.Asserted),
                ["comment.path"] = new(request.Path, ArbiterFactLabel.Asserted),
                ["comment.line"] = new(request.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), ArbiterFactLabel.Asserted),
                ["file.text-at-line"] = new(excerpt.Text, ArbiterFactLabel.Derived),
            });
            try
            {
                batch = await provider.AskStep1Async([rule], facts, null, ct).ConfigureAwait(false);
                status = batch.RequestSent ? ArbiterProviderStatuses.Answered : ArbiterProviderStatuses.NotEvaluated;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ProviderFailures.IsAbsorbable(ex))
            {
                // Recorded on the decision as an error status, so the failure is visible in the log.
                status = ArbiterProviderStatuses.Error;
            }
        }

        string? given = null;
        ArbiterStep1Score? score = null;
        if (batch is { RequestSent: true } sent)
        {
            given = sent.AnswersByRule.TryGetValue(CommentRuleId, out string? raw) ? raw : null;
            sent.ScoresByRule?.TryGetValue(CommentRuleId, out score);
        }

        double? confidence = score?.Confidence;
        double? floor = rule?.Ask?.ConfidenceAtLeast;
        string answer = Real;
        if (given is Nitpick or AlreadyFixed
            && confidence is double cleared
            && floor is double threshold
            && cleared >= threshold)
        {
            answer = given;
        }

        await WriteDecisionAsync(decisionId, now, request, rule, batch, score, given, answer, status, ct).ConfigureAwait(false);
        return new CommentTriageResult(answer, confidence, decisionId);
    }

    private async Task WriteDecisionAsync(
        string decisionId,
        DateTimeOffset now,
        CommentTriageRequest request,
        ArbiterRule? rule,
        ArbiterStep1BatchResult? batch,
        ArbiterStep1Score? score,
        string? given,
        string answer,
        string status,
        CancellationToken ct)
    {
        // The rule's answer as the model gave it; undecidable when the floor or the provider refused it.
        string loggedAnswer = given ?? (status == ArbiterProviderStatuses.Answered ? RuleEngine.Undecidable : ArbiterStep1BatchResult.NotEvaluated);
        string mapped = rule is not null && rule.Effects.TryGetValue(loggedAnswer, out string? effect) ? effect : RuleEffects.Keep;
        bool shadow = rule?.Shadow ?? false;
        string neutral = ArbiterTriggerFamilies.NeutralEffect(ArbiterTriggerFamilies.ForTrigger(TriggerName));
        ArbiterDecisionRule ruleRecord = new(
            CommentRuleId,
            1,
            loggedAnswer,
            score?.Probabilities,
            score?.Confidence,
            score?.Threshold,
            shadow ? neutral : mapped,
            [
                new ArbiterRuleFactRef("comment.body", "asserted"),
                new ArbiterRuleFactRef("file.text-at-line", "derived"),
            ],
            request.CommitSha is null ? null : "commit " + request.CommitSha,
            shadow: shadow,
            wouldEffect: shadow ? mapped : null);
        ArbiterProviderRecord providerRecord = new(
            provider is NoneProvider ? "none" : "systemone",
            null,
            batch?.Model,
            batch?.Usage is { } usage ? new ArbiterProviderUsage(usage.InputTokens, usage.OutputTokens) : null,
            null,
            status);
        ArbiterDecisionRecord record = new(
            decisionId,
            now,
            ArbiterSources.Serve,
            "serve",
            null,
            string.Empty,
            TriggerName,
            null,
            "none",
            null,
            null,
            null,
            null,
            ruleSet ?? string.Empty,
            [ruleRecord],
            providerRecord,
            answer,
            0,
            0);
        await new DecisionLog(arbiterDirectory).AppendAsync(record, ct).ConfigureAwait(false);
    }
}

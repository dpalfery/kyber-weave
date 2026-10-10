using System.Globalization;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// One rendered <c>ANSWER</c> line value, in one of the four fixed forms:
/// <c>&lt;answer&gt; (step 0)</c>, <c>&lt;answer&gt; (p=&lt;p&gt;, step 1, &lt;model&gt;)</c>,
/// <c>undecidable (&lt;reason&gt;)</c>, or <c>error (&lt;code&gt;: &lt;message&gt;)</c>.
/// Review notes additionally use the score form with <c>c=</c>.
/// </summary>
/// <param name="Text">The exact answer text.</param>
public sealed record ArbiterAnswerText(string Text)
{
    /// <summary>A plain-code step-0 answer.</summary>
    public static ArbiterAnswerText Step0(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return new ArbiterAnswerText($"{answer} (step 0)");
    }

    /// <summary>A choice step-1 answer with model probability.</summary>
    public static ArbiterAnswerText Step1(string answer, double probability, string model)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(model);
        return new ArbiterAnswerText($"{answer} (p={FormatNumber(probability)}, step 1, {model})");
    }

    /// <summary>A score step-1 answer with model confidence (review notes).</summary>
    public static ArbiterAnswerText Step1Score(string answer, double confidence, string model)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(model);
        return new ArbiterAnswerText($"{answer} (c={FormatNumber(confidence)}, step 1, {model})");
    }

    /// <summary>An undecidable answer with its reason.</summary>
    public static ArbiterAnswerText Undecidable(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new ArbiterAnswerText($"undecidable ({reason})");
    }

    /// <summary>A hook error answer with its code and message.</summary>
    public static ArbiterAnswerText Error(string code, string message)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        return new ArbiterAnswerText($"error ({code}: {message})");
    }

    /// <summary>Renders the exact answer text.</summary>
    public string Render() => Text;

    /// <inheritdoc/>
    public override string ToString() => Text;

    private static string FormatNumber(double value) =>
        value.ToString("G", CultureInfo.InvariantCulture);
}

/// <summary>One firing rule's contribution to an escalation envelope.</summary>
/// <param name="RuleId">The firing rule's id.</param>
/// <param name="Question">The rule's question text.</param>
/// <param name="Answer">The rendered answer.</param>
/// <param name="Evidence">The evidence for the answer.</param>
public sealed record ArbiterEnvelopeEntry(
    string RuleId,
    string Question,
    ArbiterAnswerText Answer,
    string Evidence);

/// <summary>
/// The escalation envelope: a non-allow outcome on a conductor or investigate
/// trigger, in the Squad's status-handoff marker style. Field order is fixed;
/// with several firing rules, <c>RULES</c> lists them in id order and
/// <c>QUESTION</c>, <c>ANSWER</c> and <c>EVIDENCE</c> repeat once per rule.
/// </summary>
/// <param name="Trigger">The trigger that fired.</param>
/// <param name="Caller">The calling harness and role.</param>
/// <param name="Target">The dispatch target, when there is one.</param>
/// <param name="PlanFile">The plan file, when there is one.</param>
/// <param name="Task">The plan task, when there is one.</param>
/// <param name="Entries">One entry per firing rule.</param>
/// <param name="DecisionId">The decision id.</param>
/// <param name="Repeat">One plus the number of earlier matching escalations.</param>
public sealed record ArbiterEscalationEnvelope(
    string Trigger,
    string Caller,
    string? Target,
    string? PlanFile,
    string? Task,
    IReadOnlyList<ArbiterEnvelopeEntry> Entries,
    string DecisionId,
    int Repeat)
{
    /// <summary>Computes the <c>NEXT</c> line for this envelope's situation.</summary>
    public string RenderNext() => NextFor(Trigger, Caller, Repeat, Entries);

    /// <summary>Renders the envelope byte for byte: fixed field order, LF separators, no trailing newline.</summary>
    public string Render()
    {
        List<ArbiterEnvelopeEntry> ordered = [.. Entries.OrderBy(entry => entry.RuleId, StringComparer.Ordinal)];
        List<string> lines =
        [
            "STATUS: ARBITER_ESCALATION",
            $"TRIGGER: {Trigger}",
            $"CALLER: {Caller}",
        ];
        if (Target is not null)
        {
            lines.Add($"TARGET: {Target}");
        }

        if (PlanFile is not null)
        {
            lines.Add($"PLAN_FILE: {PlanFile}");
        }

        if (Task is not null)
        {
            lines.Add($"TASK: {Task}");
        }

        lines.Add($"RULES: {string.Join(", ", ordered.Select(entry => entry.RuleId))}");
        foreach (ArbiterEnvelopeEntry entry in ordered)
        {
            lines.Add($"QUESTION: {entry.Question}");
            lines.Add($"ANSWER: {entry.Answer.Render()}");
            lines.Add($"EVIDENCE: {entry.Evidence}");
        }

        lines.Add($"DECISION_ID: {DecisionId}");
        lines.Add($"REPEAT: {Repeat.ToString(CultureInfo.InvariantCulture)}");
        lines.Add($"NEXT: {RenderNext()}");
        return string.Join("\n", lines);
    }

    /// <summary>Computes the <c>NEXT</c> line for the given situation.</summary>
    public static string NextFor(
        string trigger,
        string? caller,
        int repeat,
        IReadOnlyList<ArbiterEnvelopeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(entries);
        ArbiterTriggerFamily family = ArbiterTriggerFamilies.ForTrigger(trigger);
        if (family == ArbiterTriggerFamily.Conductor)
        {
            bool plannerMalformed = entries.Any(entry =>
                string.Equals(entry.RuleId, "KW-ARB-PLANNER-001", StringComparison.Ordinal)
                && entry.Answer.Text.StartsWith("malformed", StringComparison.Ordinal));
            if (plannerMalformed)
            {
                return "re-issue this planner dispatch with a recognised marker; do not send it to architect.";
            }

            return repeat >= 2
                ? "stop: record this as a run finding; do not dispatch architect again for this task."
                : "dispatch architect with this envelope; do not retry this delegation unchanged.";
        }

        if (family == ArbiterTriggerFamily.Investigate)
        {
            if (caller is not null
                && (string.Equals(caller, "architect", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(caller, "product-owner", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(caller, "code-reviewer", StringComparison.OrdinalIgnoreCase)))
            {
                return "report this in your own result; do not retry this dispatch unchanged.";
            }

            return "the conductor dispatches architect with this envelope; any other caller reports it in its own result.";
        }

        throw new ArgumentException(
            $"No NEXT line is defined for trigger '{trigger}': escalations come from conductor and investigate triggers.",
            nameof(trigger));
    }
}

/// <summary>A <c>lens.spawn</c> denial: the lens does not apply. Not an escalation.</summary>
/// <param name="Lens">The denied lens name.</param>
/// <param name="RuleIds">The firing rule ids, rendered in id order.</param>
/// <param name="Answer">The rendered answer.</param>
/// <param name="DecisionId">The decision id.</param>
public sealed record ArbiterSkipNote(
    string Lens,
    IReadOnlyList<string> RuleIds,
    ArbiterAnswerText Answer,
    string DecisionId)
{
    /// <summary>Renders the note byte for byte.</summary>
    public string Render() => string.Join("\n",
    [
        "STATUS: ARBITER_SKIP",
        $"LENS: {Lens}",
        $"RULES: {string.Join(", ", RuleIds.OrderBy(id => id, StringComparer.Ordinal))}",
        $"ANSWER: {Answer.Render()}",
        $"DECISION_ID: {DecisionId}",
    ]);
}

/// <summary>A <c>refute.spawn</c> denial: the finding is kept. Not an escalation.</summary>
/// <param name="Refute">The refuted finding id.</param>
/// <param name="RuleIds">The firing rule ids, rendered in id order.</param>
/// <param name="Answer">The rendered answer.</param>
/// <param name="DecisionId">The decision id.</param>
public sealed record ArbiterVerifiedNote(
    string Refute,
    IReadOnlyList<string> RuleIds,
    ArbiterAnswerText Answer,
    string DecisionId)
{
    /// <summary>Renders the note byte for byte.</summary>
    public string Render() => string.Join("\n",
    [
        "STATUS: ARBITER_VERIFIED",
        $"REFUTE: {Refute}",
        $"RULES: {string.Join(", ", RuleIds.OrderBy(id => id, StringComparer.Ordinal))}",
        $"ANSWER: {Answer.Render()}",
        $"DECISION_ID: {DecisionId}",
    ]);
}

/// <summary>Post-dispatch context on <c>lens.returned</c>. Not an escalation.</summary>
/// <param name="Finding">The finding id.</param>
/// <param name="RuleIds">The firing rule ids, rendered in id order.</param>
/// <param name="Answer">The rendered answer.</param>
/// <param name="DecisionId">The decision id.</param>
public sealed record ArbiterAnnotationNote(
    string Finding,
    IReadOnlyList<string> RuleIds,
    ArbiterAnswerText Answer,
    string DecisionId)
{
    /// <summary>Renders the note byte for byte.</summary>
    public string Render() => string.Join("\n",
    [
        "STATUS: ARBITER_ANNOTATION",
        $"FINDING: {Finding}",
        $"RULES: {string.Join(", ", RuleIds.OrderBy(id => id, StringComparer.Ordinal))}",
        $"ANSWER: {Answer.Render()}",
        $"DECISION_ID: {DecisionId}",
    ]);
}

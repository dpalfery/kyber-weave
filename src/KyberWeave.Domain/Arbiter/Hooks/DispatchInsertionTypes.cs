namespace KyberWeave.Arbiter.Hooks;

/// <summary>What the hook asks the task prompt service for when a dispatch names a task or a technology.</summary>
/// <param name="TaskId">The <c>TASK</c> header's value, or null when the dispatch names no task.</param>
/// <param name="PlanFile">The <c>PLAN_FILE</c> header's value, or null when the dispatch names none.</param>
/// <param name="Technology">The <c>TECHNOLOGY</c> header's value, for a dispatch that belongs to no task.</param>
/// <param name="PriorResultsPath">The <c>PRIOR_RESULTS</c> header's value, the file a retry's earlier results live in.</param>
/// <param name="FailedAttempts">The <c>FAILED_ATTEMPTS</c> header's value; zero when the header is absent or unreadable.</param>
/// <param name="RetryText">The lines below the header block, which ride after the prompt; the budget reserves room for them.</param>
public sealed record DispatchPromptQuery(
    string? TaskId,
    string? PlanFile,
    string? Technology,
    string? PriorResultsPath,
    int FailedAttempts,
    string? RetryText = null);

/// <summary>One prompt the hook built, or why it could not be built.</summary>
/// <param name="Built">Whether <paramref name="Prompt"/> holds a prompt.</param>
/// <param name="Prompt">The full task prompt, in the service's tagged parts.</param>
/// <param name="Refusal">Why no prompt was built, naming the command that repairs it; null when one was built.</param>
public sealed record DispatchPromptExpansion(bool Built, string Prompt, string? Refusal);

/// <summary>What happened when the hook looked for a prompt to insert.</summary>
public enum DispatchInsertionKind
{
    /// <summary>The dispatch carries no task or technology, or already carries an expanded prompt.</summary>
    None,

    /// <summary>The identity line was replaced with the full prompt and every other line was kept.</summary>
    Inserted,

    /// <summary>The prompt could not be built, so the dispatch is stopped and no worker starts.</summary>
    Stopped,
}

/// <summary>The text a hook renders in place of a dispatch's identity line, or the reason it cannot.</summary>
/// <param name="Kind">Whether anything was inserted.</param>
/// <param name="Text">The dispatch's new text, for <see cref="DispatchInsertionKind.Inserted"/>.</param>
/// <param name="Reason">The repair command, for <see cref="DispatchInsertionKind.Stopped"/>.</param>
public sealed record DispatchInsertion(DispatchInsertionKind Kind, string Text = "", string? Reason = null)
{
    /// <summary>The one answer for a dispatch that needs no insertion.</summary>
    public static DispatchInsertion None { get; } = new(DispatchInsertionKind.None);
}

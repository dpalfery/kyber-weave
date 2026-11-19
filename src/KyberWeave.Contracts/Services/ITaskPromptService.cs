using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>What one caller asks the prompt service for.</summary>
/// <param name="RepoRoot">The repository root the task file, the documentation and the worktree are read from.</param>
/// <param name="TaskId">
/// The task to build a prompt for, as a worker or a conductor spells it; null for a dispatch that belongs to
/// no task, such as a `platform-researcher` request.
/// </param>
/// <param name="Plan">The plan or specification to read the task from; null to use the one active plan.</param>
/// <param name="Agent">The agent the dispatch names; null to take the role the plan assigned, then the recorded pick.</param>
/// <param name="PriorResultsPath">
/// The file holding earlier tasks' reported results, when a retry names one; null when none were supplied.
/// </param>
/// <param name="Technology">The technology a dispatch with no task names; ignored when <paramref name="TaskId"/> is set.</param>
/// <param name="FailedAttempts">How many attempts at this task already failed; the step-up reads it.</param>
/// <param name="Harness">
/// The harness the dispatch runs on. A harness whose hook cannot rewrite the dispatch gets the prompt written to
/// the run's scratch folder; null (the packet command and the hook) writes no file.
/// </param>
/// <param name="RetryText">
/// The retry's failure report, which the dispatch hook appends after the prompt. It is not part of the prompt, but
/// the budget leaves room for it so the delivered text does not overshoot.
/// </param>
public sealed record TaskPromptRequest(
    string RepoRoot,
    string? TaskId = null,
    string? Plan = null,
    string? Agent = null,
    string? PriorResultsPath = null,
    string? Technology = null,
    int FailedAttempts = 0,
    string? Harness = null,
    string? RetryText = null);

/// <summary>Why a task prompt was not built, which decides whether the dispatch may continue.</summary>
public enum TaskPromptRefusalKind
{
    /// <summary>A prompt was built, so there is no refusal.</summary>
    None,

    /// <summary>
    /// A plan, task file, task id or prior-results file that is absent or unreadable. The dispatch continues: an
    /// input the hook could not read is not a finding against the work.
    /// </summary>
    MissingInput,

    /// <summary>A positive finding about the dispatch itself, such as a path that escapes the run's scratch area.</summary>
    Finding,
}

/// <summary>The task prompt, or why no prompt was built.</summary>
/// <param name="Built">Whether <paramref name="Prompt"/> holds a prompt.</param>
/// <param name="Prompt">The whole prompt, each part inside its own named tag; empty when none was built.</param>
/// <param name="Agent">The agent to dispatch, or null when the request and the task file name none.</param>
/// <param name="Difficulty">The difficulty in use for the task, or null for a dispatch with no task.</param>
/// <param name="DifficultySource">Who rated it: <c>architect</c>, <c>default</c> or <c>decision-model</c>.</param>
/// <param name="Technology">The one technology the task or dispatch names, or null.</param>
/// <param name="LeftOut">What the budget cut, as the prompt's last part lists it; empty when nothing was cut.</param>
/// <param name="Refusal">
/// Why no prompt was built, naming the command that repairs it, or null when one was built.
/// </param>
/// <param name="PromptFile">
/// The repository-relative file the prompt was written to, for a dispatch whose harness cannot rewrite it; null
/// when the prompt is not written to a file.
/// </param>
/// <param name="Substitution">
/// The sentence reporting that a removed agent's name was replaced by the agent that works the task now, or null
/// when nothing was substituted.
/// </param>
/// <param name="RefusalKind">Whether a refusal is an unreadable input, which never blocks, or a positive finding.</param>
public sealed record TaskPromptResult(
    bool Built,
    string Prompt,
    string? Agent,
    string? Difficulty,
    string? DifficultySource,
    string? Technology,
    IReadOnlyList<string> LeftOut,
    string? Refusal,
    string? PromptFile = null,
    string? Substitution = null,
    TaskPromptRefusalKind RefusalKind = TaskPromptRefusalKind.None)
{
    /// <summary>A refusal that names the command repairing the reason no prompt was built.</summary>
    /// <param name="refusal">Why the prompt could not be built, and what repairs it.</param>
    /// <param name="kind">
    /// Whether the refusal is an input the hook could not read, which never blocks a dispatch, or a positive finding
    /// about the dispatch, which does.
    /// </param>
    public static TaskPromptResult NotBuilt(string refusal, TaskPromptRefusalKind kind = TaskPromptRefusalKind.MissingInput) => new(
        false, string.Empty, null, null, null, null, [], refusal, RefusalKind: kind);
}

/// <summary>
/// Builds one task's prompt: the task's own text, the host facts, the shared rules, the parent
/// technology's rules and skills, then the technology's rules, skills and standard, each inside a named tag.
/// </summary>
/// <remarks>
/// One service, three callers — the dispatch hook, the docs tool and <c>docs packet</c> — so the same task
/// always yields the same text and the tool can never describe a prompt the worker did not receive. Nothing
/// is written: the prompt is not stored in the task file or any tracked file, so the task file's digest is
/// unaffected.
/// </remarks>
public interface ITaskPromptService
{
    /// <summary>Builds the prompt for <paramref name="request"/>, or says why it could not be built.</summary>
    /// <param name="request">The task or technology to build for, and the retry facts when there are some.</param>
    /// <param name="config">The effective host configuration, which holds the prompt budget.</param>
    TaskPromptResult Build(TaskPromptRequest request, KyberWeaveConfig config);
}

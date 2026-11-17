using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter.Facts;

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

/// <summary>
/// Replaces a dispatch's identity line with the whole task prompt, keeping every other line the conductor wrote.
/// </summary>
/// <remarks>
/// <para>
/// The conductor dispatches a task id and a few header lines. This turns
/// <c>TASK: 2.9</c> into the prompt the task prompt service builds, so the worker
/// starts with everything and the prompt never enters the conductor's context. A retry's header lines reach the
/// prompt service and the failure report below them survives unchanged.
/// </para>
/// <para>
/// The same call runs with the Arbiter on, after the rules judged the short dispatch, and with the Arbiter off,
/// where it is the only hook action. A dispatch that already carries an expanded prompt is left alone, so a
/// second hook on the same dispatch cannot wrap the prompt in itself.
/// </para>
/// </remarks>
public static partial class DispatchPromptInsertion
{
    /// <summary>The header whose value is the task to build a prompt for.</summary>
    public const string TaskHeader = "TASK";

    /// <summary>The header that names the technology of a dispatch that belongs to no task.</summary>
    public const string TechnologyHeader = "TECHNOLOGY";

    /// <summary>The header that names the file a retry's earlier results live in.</summary>
    public const string PriorResultsHeader = "PRIOR_RESULTS";

    /// <summary>The header that names how many attempts at the task already failed.</summary>
    public const string FailedAttemptsHeader = "FAILED_ATTEMPTS";

    private static readonly string[] ExpandedTags =
    [
        "<task>",
        "<host-facts>",
        "<shared-rules>",
        "<parent-rules>",
        "<technology-rules>",
        "<skills>",
        "<standard>",
        "<left-out>",
    ];

    /// <summary>Whether a dispatch already carries the prompt the hook inserts.</summary>
    /// <remarks>
    /// The prompt opens with one of the service's own tags, so a dispatch whose first line is a header is never
    /// mistaken for one. This is what keeps insertion idempotent across the project and user-level hooks.
    /// </remarks>
    /// <param name="prompt">The dispatch text as the harness sent it.</param>
    public static bool IsExpanded(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        string first = prompt.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return ExpandedTags.Any(tag => first.StartsWith(tag, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether this harness's dispatch hook can replace the dispatch text, so the prompt travels inside the call.
    /// </summary>
    /// <remarks>
    /// From the per-harness table in <c>docs/kyber-arbiter/architecture.md</c>, which records a harness as a rewrite
    /// only where a live dispatch showed the worker received the replaced text. Every other harness, and any token
    /// this list does not know, takes the prompt file instead.
    /// </remarks>
    /// <param name="harness">The harness token, such as <c>claude</c> or <c>antigravity</c>.</param>
    public static bool CanRewriteDispatch(string? harness)
    {
        if (string.IsNullOrWhiteSpace(harness))
        {
            return false;
        }

        return RewriteHarnesses.Contains(harness.Trim());
    }

    private static readonly IReadOnlySet<string> RewriteHarnesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "claude",
        "claude-code",
        "copilot",
        "copilot-vscode",
        "copilot-cli",
        "codex",
        "cursor",
        "opencode",
        "pi",
        "kilo",
        "antigravity",
        "factory",
        "devin",
    };

    /// <summary>Expands the dispatch's identity line into the full prompt, or says why it cannot.</summary>
    /// <param name="prompt">The dispatch text as the harness sent it.</param>
    /// <param name="context">The hook context carrying the prompt service, when the host installed one.</param>
    /// <returns>
    /// <see cref="DispatchInsertionKind.None"/> when there is nothing to insert, the dispatch's new text when the
    /// prompt was built, and the repair command when it could not be.
    /// </returns>
    public static DispatchInsertion Expand(string prompt, HookContext context)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(context);

        if (IsExpanded(prompt))
        {
            return DispatchInsertion.None;
        }

        DispatchHeaders headers = DispatchHeaders.Parse(prompt);
        if (headers.TaskId is null && headers.Technology is null)
        {
            return DispatchInsertion.None;
        }

        if (context.BuildDispatchPrompt is null)
        {
            return DispatchInsertion.None;
        }

        DispatchPromptExpansion expansion;
        try
        {
            expansion = context.BuildDispatchPrompt(new DispatchPromptQuery(
                headers.TaskId,
                headers.PlanFile,
                headers.Technology,
                headers.PriorResultsPath,
                headers.FailedAttempts,
                headers.Body)) ?? new DispatchPromptExpansion(false, string.Empty, Why(headers));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            expansion = new DispatchPromptExpansion(false, string.Empty,
                $"the prompt could not be built ({ex.Message}). {Why(headers)}");
        }

        if (!expansion.Built || string.IsNullOrWhiteSpace(expansion.Prompt))
        {
            return new DispatchInsertion(
                DispatchInsertionKind.Stopped,
                string.Empty,
                string.IsNullOrWhiteSpace(expansion.Refusal) ? Why(headers) : expansion.Refusal);
        }

        string body = headers.Body;
        return new DispatchInsertion(
            DispatchInsertionKind.Inserted,
            body.Length == 0 ? expansion.Prompt : expansion.Prompt + "\n\n" + body);
    }

    /// <summary>The instruction a stopped dispatch carries, naming the command that prints the same prompt.</summary>
    /// <param name="headers">The identity lines the dispatch wrote.</param>
    private static string Why(DispatchHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        string id = headers.TaskId ?? headers.Technology ?? "<task>";
        return $"this dispatch was stopped before the worker started: no task prompt could be built for " +
            $"'{id}'. Run 'kyber-weave docs packet {id}' to see the same refusal, repair the task file it " +
            "names, then dispatch again.";
    }

    /// <summary>The leading header lines a dispatch wrote, and the text below them.</summary>
    /// <param name="TaskId">The first <c>TASK</c> value, or null.</param>
    /// <param name="PlanFile">The first <c>PLAN_FILE</c> value, or null.</param>
    /// <param name="Technology">The first <c>TECHNOLOGY</c> value, or null.</param>
    /// <param name="PriorResultsPath">The first <c>PRIOR_RESULTS</c> value, or null.</param>
    /// <param name="FailedAttempts">The first <c>FAILED_ATTEMPTS</c> value that parses, or zero.</param>
    /// <param name="Body">Every line below the header block, unchanged.</param>
    private sealed record DispatchHeaders(
        string? TaskId,
        string? PlanFile,
        string? Technology,
        string? PriorResultsPath,
        int FailedAttempts,
        string Body)
    {
        /// <summary>Reads the header block at the start of <paramref name="prompt"/>.</summary>
        /// <param name="prompt">The dispatch text as the harness sent it.</param>
        public static DispatchHeaders Parse(string prompt)
        {
            ArgumentNullException.ThrowIfNull(prompt);

            string[] lines = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            int index = 0;
            for (; index < lines.Length; index++)
            {
                Match match = HeaderLineRegex().Match(lines[index]);
                if (!match.Success)
                {
                    break;
                }

                string name = match.Groups["name"].Value;
                string value = match.Groups["value"].Value.Trim();

                // Only a real task dispatch is expanded: a task id the task files cannot have is not one, and
                // its line stays in the body with everything below it.
                if (name == TaskHeader && !HeaderBlock.IsTaskId(value))
                {
                    break;
                }

                values.TryAdd(name, value);
            }

            string body = string.Join('\n', lines.Skip(index)).TrimStart('\n');
            return new DispatchHeaders(
                Value(values, TaskHeader),
                Value(values, "PLAN_FILE"),
                Value(values, TechnologyHeader),
                Value(values, PriorResultsHeader),
                Attempts(Value(values, FailedAttemptsHeader)),
                body);
        }

        private static string? Value(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out string? value) && value.Length > 0 ? value : null;

        private static int Attempts(string? value) =>
            int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int attempts)
                ? attempts
                : 0;
    }

    // A task dispatch's header line is one of the closed names — the Arbiter's own set plus the task headers —
    // spelled exactly as the conductor writes it. The block ends at the first line that is not one, so an
    // ordinary `Task:` sentence or a retry's `Error: build failed` line below the headers is body text.
    [GeneratedRegex(
        @"^(?<name>KYBER-ARBITER|PLAN_FILE|TASK|LENS|REFUTE|REVIEW_RUN|TECHNOLOGY|PRIOR_RESULTS|FAILED_ATTEMPTS):[ \t]*(?<value>\S.*?)[ \t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLineRegex();
}

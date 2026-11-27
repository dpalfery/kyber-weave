using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Arbiter.Hooks;

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
    private static readonly string[] ExpandedTags =
    [
        "<task>",
        "<host-facts>",
        "<shared-rules>",
        "<parent-rules>",
        "<technology-rules>",
        "<kw-skills>",
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
    /// only where a live dispatch showed the worker received the replaced text. The flag lives on
    /// <see cref="HarnessRegistry"/>; every other harness, and any token the registry does not know, takes the prompt
    /// file instead.
    /// </remarks>
    /// <param name="harness">The harness token, such as <c>claude</c> or <c>antigravity</c>.</param>
    public static bool CanRewriteDispatch(string? harness) => HarnessRegistry.RewritesDispatch(harness);

    /// <summary>
    /// Every harness token recorded as rewriting the dispatch. Each one must be served by an adapter that inserts
    /// the prompt: a token listed here and served by nothing would leave the worker with no prompt and no file.
    /// </summary>
    public static IReadOnlySet<string> RewriteHarnessTokens => HarnessRegistry.RewriteTokens;

    /// <summary>Expands the dispatch's identity line into the full prompt, or says why it cannot.</summary>
    /// <param name="prompt">The dispatch text as the harness sent it.</param>
    /// <param name="context">The hook context carrying the prompt service, when the host installed one.</param>
    /// <param name="target">
    /// The dispatched agent (<c>subagent_type</c> or the harness equivalent), or null when the harness names none.
    /// A dispatch with no task or technology but a target gets a role-only prompt of that role's skills.
    /// </param>
    /// <returns>
    /// <see cref="DispatchInsertionKind.None"/> when there is nothing to insert, the dispatch's new text when the
    /// prompt was built, and the repair command when it could not be.
    /// </returns>
    public static DispatchInsertion Expand(string prompt, HookContext context, string? target = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(context);

        if (IsExpanded(prompt))
        {
            return DispatchInsertion.None;
        }

        PromptHeaders headers = PromptHeaders.Parse(prompt);
        if (headers.TaskId is null && headers.Technology is null)
        {
            return string.IsNullOrWhiteSpace(target)
                ? DispatchInsertion.None
                : ExpandRole(prompt, context, target.Trim());
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
                headers.Body,
                target)) ?? new DispatchPromptExpansion(false, string.Empty, Why(headers));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            expansion = new DispatchPromptExpansion(false, string.Empty,
                $"the prompt could not be built ({ex.Message}). {Why(headers)}");
        }

        if (!expansion.Built || string.IsNullOrWhiteSpace(expansion.Prompt))
        {
            return new DispatchInsertion(
                expansion.MissingInput ? DispatchInsertionKind.Warned : DispatchInsertionKind.Stopped,
                string.Empty,
                string.IsNullOrWhiteSpace(expansion.Refusal) ? Why(headers) : expansion.Refusal);
        }

        string body = headers.Body;
        return new DispatchInsertion(
            DispatchInsertionKind.Inserted,
            body.Length == 0 ? expansion.Prompt : expansion.Prompt + "\n\n" + body);
    }

    /// <summary>
    /// Puts a role's skills in front of a dispatch that names no task or technology. Never a stop: a role with no
    /// skills, a refusal, an unbuilt prompt or a build failure all leave the dispatch unchanged.
    /// </summary>
    /// <param name="prompt">The dispatch text, kept unchanged below the inserted block.</param>
    /// <param name="context">The hook context carrying the prompt service.</param>
    /// <param name="target">The dispatched agent.</param>
    private static DispatchInsertion ExpandRole(string prompt, HookContext context, string target)
    {
        if (context.BuildDispatchPrompt is null)
        {
            return DispatchInsertion.None;
        }

        DispatchPromptExpansion? expansion;
        try
        {
            expansion = context.BuildDispatchPrompt(
                new DispatchPromptQuery(null, null, null, null, 0, prompt, target));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            context.Log.WriteLine($"Arbiter: the role skills for '{target}' could not be built ({ex.Message}). The dispatch continues unchanged.");
            return DispatchInsertion.None;
        }

        if (expansion is not { Built: true } || string.IsNullOrWhiteSpace(expansion.Prompt))
        {
            return DispatchInsertion.None;
        }

        return new DispatchInsertion(
            DispatchInsertionKind.Inserted,
            prompt.Length == 0 ? expansion.Prompt : expansion.Prompt + "\n\n" + prompt);
    }

    /// <summary>The instruction a stopped dispatch carries, naming the command that prints the same prompt.</summary>
    /// <param name="headers">The identity lines the dispatch wrote.</param>
    private static string Why(PromptHeaders headers)
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
    private sealed record PromptHeaders(
        string? TaskId,
        string? PlanFile,
        string? Technology,
        string? PriorResultsPath,
        int FailedAttempts,
        string Body)
    {
        /// <summary>Reads the header block at the start of <paramref name="prompt"/>.</summary>
        /// <param name="prompt">The dispatch text as the harness sent it.</param>
        public static PromptHeaders Parse(string prompt)
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
                if (name == DispatchHeaders.Task && !HeaderBlock.IsTaskId(value))
                {
                    break;
                }

                values.TryAdd(name, value);
            }

            string body = string.Join('\n', lines.Skip(index)).TrimStart('\n');
            return new PromptHeaders(
                Value(values, DispatchHeaders.Task),
                Value(values, DispatchHeaders.PlanFile),
                Value(values, DispatchHeaders.Technology),
                Value(values, DispatchHeaders.PriorResults),
                Attempts(Value(values, DispatchHeaders.FailedAttempts)),
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

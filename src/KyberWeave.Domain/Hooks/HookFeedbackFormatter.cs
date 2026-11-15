using System.Text;

namespace KyberWeave.Domain.Hooks;

/// <summary>Turns rule outcomes into the short text the agent reads.</summary>
public static class HookFeedbackFormatter
{
    /// <summary>Formats feedback for failed and timed-out rules, plus config warnings.</summary>
    /// <param name="file">The repository-relative changed file.</param>
    /// <param name="outcomes">Every outcome, in run order.</param>
    /// <param name="warnings">Config warnings to append.</param>
    /// <param name="maxLines">Most output lines kept per rule.</param>
    /// <param name="maxBytes">Most output bytes kept per rule.</param>
    /// <returns>One section per rule, or an empty string when nothing needs saying.</returns>
    public static string Format(
        string file,
        IReadOnlyList<RuleOutcome> outcomes,
        IReadOnlyList<string> warnings,
        int maxLines,
        int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(warnings);

        StringBuilder text = new();
        List<RuleOutcome> problems = [.. outcomes.Where(o => o.NeedsAttention)];
        if (problems.Count > 0)
        {
            _ = text.Append("kyber-weave-hooks: ").Append(problems.Count).Append(" check(s) need attention for ").AppendLine(file);
        }

        foreach (RuleOutcome outcome in problems)
        {
            string label = outcome.Kind == HookRuleKind.Test ? "test" : "lint";
            _ = text.Append('[').Append(label).Append("] ").Append(outcome.RuleId);
            _ = text.AppendLine(outcome.TimedOut ? ": timed out, skipped" : $": failed (exit {outcome.ExitCode})");
            if (!outcome.TimedOut)
            {
                _ = text.AppendLine(Trim(outcome.Output, maxLines, maxBytes));
            }
        }

        foreach (string warning in warnings)
        {
            _ = text.Append("warning: ").AppendLine(warning);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Keeps the first lines and bytes of command output and says how much was cut.</summary>
    /// <param name="output">The raw output.</param>
    /// <param name="maxLines">Most lines to keep.</param>
    /// <param name="maxBytes">Most UTF-8 bytes to keep.</param>
    /// <returns>The trimmed output.</returns>
    public static string Trim(string output, int maxLines, int maxBytes)
    {
        string[] lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        List<string> kept = [];
        int bytes = 0;
        foreach (string line in lines)
        {
            string trimmed = line.TrimEnd();
            int size = Encoding.UTF8.GetByteCount(trimmed) + 1;
            if (kept.Count >= maxLines || bytes + size > maxBytes)
            {
                break;
            }

            kept.Add(trimmed);
            bytes += size;
        }

        int cut = lines.Length - kept.Count;
        if (cut > 0)
        {
            kept.Add($"... {cut} more line(s) not shown");
        }

        return string.Join('\n', kept);
    }
}

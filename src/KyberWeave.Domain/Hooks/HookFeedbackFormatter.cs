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
    /// <param name="maxLines">Most lines to keep; zero or less keeps none.</param>
    /// <param name="maxBytes">Most UTF-8 bytes to keep; zero or less keeps none.</param>
    /// <returns>The trimmed output.</returns>
    /// <remarks>Only a bounded window at the start of <paramref name="output"/> is copied, so a huge output is not duplicated.</remarks>
    public static string Trim(string output, int maxLines, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(output);

        ReadOnlySpan<char> all = output.AsSpan();
        if (maxLines <= 0 || maxBytes <= 0)
        {
            int total = CountLines(all);
            return total > 0 ? $"... {total} more line(s) not shown" : string.Empty;
        }

        int window = (int)Math.Min(all.Length, (2L * maxBytes) + 2);
        ReadOnlySpan<char> head = all[..window];
        List<string> kept = [];
        int bytes = 0;
        int position = 0;
        while (position < window && kept.Count < maxLines)
        {
            int relative = head[position..].IndexOf('\n');
            if (relative < 0 && window < all.Length)
            {
                break;
            }

            int end = relative < 0 ? window : position + relative;
            ReadOnlySpan<char> line = head[position..end];
            int next = relative < 0 ? end : end + 1;
            if (IsBlank(line))
            {
                position = next;
                continue;
            }

            ReadOnlySpan<char> trimmed = line.TrimEnd();
            int size = Encoding.UTF8.GetByteCount(trimmed) + 1;
            if (bytes + size > maxBytes)
            {
                break;
            }

            kept.Add(trimmed.ToString());
            bytes += size;
            position = next;
        }

        if (kept.Count == 0)
        {
            position = SkipBlankLines(all, position);
            if (position < all.Length)
            {
                int relative = all[position..].IndexOf('\n');
                int end = relative < 0 ? all.Length : position + relative;
                string prefix = PrefixWithin(all[position..end].TrimEnd(), maxBytes - 1);
                if (prefix.Length > 0)
                {
                    kept.Add(prefix + "...");
                }

                position = relative < 0 ? all.Length : end + 1;
            }
        }

        int cut = CountLines(all[position..]);
        if (cut > 0)
        {
            kept.Add($"... {cut} more line(s) not shown");
        }

        return string.Join('\n', kept);
    }

    private static bool IsBlank(ReadOnlySpan<char> line) => line.Length == 0 || (line.Length == 1 && line[0] == '\r');

    private static int SkipBlankLines(ReadOnlySpan<char> text, int position)
    {
        while (position < text.Length)
        {
            int relative = text[position..].IndexOf('\n');
            int end = relative < 0 ? text.Length : position + relative;
            if (!IsBlank(text[position..end]))
            {
                break;
            }

            position = relative < 0 ? text.Length : end + 1;
        }

        return position;
    }

    private static int CountLines(ReadOnlySpan<char> text)
    {
        int count = 0;
        while (!text.IsEmpty)
        {
            int relative = text.IndexOf('\n');
            ReadOnlySpan<char> line = relative < 0 ? text : text[..relative];
            if (!IsBlank(line))
            {
                count++;
            }

            text = relative < 0 ? default : text[(relative + 1)..];
        }

        return count;
    }

    private static string PrefixWithin(ReadOnlySpan<char> text, int maxBytes)
    {
        int length = 0;
        int bytes = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > maxBytes)
            {
                break;
            }

            length += rune.Utf16SequenceLength;
        }

        return text[..length].ToString();
    }
}

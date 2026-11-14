using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// The host repository's facts a worker would otherwise open the root <c>AGENTS.md</c> for, assembled by the machine
/// and appended to a task packet at print time: the Config Reg paths, the task gates declared for the task's role,
/// and the host's own rule section.
/// </summary>
/// <remarks>
/// <para>
/// Measured runs showed nearly every worker reading the host <c>AGENTS.md</c>, once per dispatch, for a handful of
/// values the configuration already holds. The section is appended when the packet is printed and never written into
/// the task file, so the execution digest and the staleness check are unaffected.
/// </para>
/// <para>
/// The host's rules are prose, so they are copied, never parsed: the first section whose heading names rules,
/// constraints, prohibitions or non-negotiables is included verbatim when it is short, and otherwise only its
/// first-level bullets, with a line saying what was left out.
/// </para>
/// </remarks>
public static partial class PacketHostFacts
{
    /// <summary>The heading the appended section starts with.</summary>
    public const string Heading = "## Host facts";

    /// <summary>The longest rule section copied whole.</summary>
    public const int RuleSectionCap = 1500;

    private const string ScratchpadRoot = ".agent_scratchpad";

    /// <summary>Builds the section for one task.</summary>
    /// <param name="repoRoot">The repository root, where the host <c>AGENTS.md</c> is read.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="role">The agent working the task: the worker's own name, else the one the plan assigned; null when neither is known.</param>
    /// <param name="taskId">The task id, for the scratchpad path.</param>
    /// <returns>The section, starting with <see cref="Heading" />.</returns>
    public static string Render(string repoRoot, KyberWeaveConfig config, string? role, string? taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);

        StringBuilder text = new();
        text.AppendLine(Heading);
        text.AppendLine();
        text.AppendLine("Assembled from this repository's configuration and root AGENTS.md. Open AGENTS.md only for something this section does not answer.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Scratchpad for this task: `{ScratchpadRoot}/{taskId ?? "<task-id>"}/`");
        foreach (ConfigRegEntry entry in config.ConfigReg.Resolve(config.Ontology))
        {
            if (entry.Kind == ConfigRegEntryKind.Path)
                text.AppendLine(CultureInfo.InvariantCulture, $"- <{entry.Name}>: `{entry.Path}`");
        }

        AppendGates(text, config, role);
        AppendRules(text, repoRoot);
        return text.ToString().TrimEnd();
    }

    private static void AppendGates(StringBuilder text, KyberWeaveConfig config, string? role)
    {
        if (role is null)
        {
            text.AppendLine("- Task gates: `review.task-gates` lists them per agent; print this packet again with `--agent <your agent name>` to see yours.");
            return;
        }

        if (!config.Review.TaskGates.TryGetValue(role, out IReadOnlyList<ReviewTaskGate>? gates) || gates.Count == 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Task gates for {role}: none declared under review.task-gates.");
            return;
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"- Task gates for {role} (review.task-gates; `review gates --agent {role}` runs them):");
        foreach (ReviewTaskGate gate in gates)
            text.AppendLine(CultureInfo.InvariantCulture, $"  - `{gate.Id}`: `{string.Join(' ', gate.Run)}`");
    }

    private static void AppendRules(StringBuilder text, string repoRoot)
    {
        string path = Path.Combine(repoRoot, "AGENTS.md");
        string markdown;
        try
        {
            if (!File.Exists(path))
                return;

            markdown = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Host rules: AGENTS.md could not be read ({ex.Message}).");
            return;
        }

        if (RuleSection(markdown) is not { } section)
            return;

        text.AppendLine();
        if (section.Body.Length <= RuleSectionCap)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Host rules, verbatim from AGENTS.md § {section.Heading}:");
            text.AppendLine();
            text.AppendLine(section.Body);
            return;
        }

        string[] bullets = [.. section.Body.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal))];
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"Host rules, the first-level bullet lines of AGENTS.md § {section.Heading} ({section.Body.Length} characters, over the cap; continuation lines and nested items were left out):");
        text.AppendLine();
        foreach (string bullet in bullets)
            text.AppendLine(bullet.TrimEnd());
    }

    /// <summary>The first section whose heading names the host's hard rules, without its heading line.</summary>
    internal static (string Heading, string Body)? RuleSection(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // A '#' line inside a fenced code block is a shell comment, not a heading.
        bool[] isHeading = new bool[lines.Length];
        bool fenced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                fenced = !fenced;
            else
                isHeading[i] = !fenced && HeadingLine().IsMatch(lines[i]);
        }

        for (int i = 0; i < lines.Length; i++)
        {
            if (!isHeading[i])
                continue;

            Match heading = HeadingLine().Match(lines[i]);
            if (!RuleHeading().IsMatch(heading.Groups["title"].Value))
                continue;

            int level = heading.Groups["hashes"].Value.Length;
            int end = i + 1;
            while (end < lines.Length
                   && !(isHeading[end] && HeadingLine().Match(lines[end]).Groups["hashes"].Value.Length <= level))
                end++;

            string body = string.Join('\n', lines[(i + 1)..end]).Trim();
            return body.Length == 0 ? null : (heading.Groups["title"].Value.Trim(), body);
        }

        return null;
    }

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"\b(rules|constraints|prohibitions|non-negotiables?|hard rules)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuleHeading();
}

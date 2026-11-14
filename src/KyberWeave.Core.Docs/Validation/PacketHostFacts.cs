using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Review;

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
/// The host's rules are prose, so they are copied, never parsed: every section whose heading names rules (rules,
/// non-negotiables, mandatory, must or never, policies, constraints, guardrails) is included verbatim in document
/// order, so the packet does not depend on which comes first or how a heading is spelled. A matched section with no
/// rule text is skipped and logged, the generated CodeGraph and Config Reg blocks are never copied, and the total is
/// capped at <see cref="RulesBudget" /> characters with a line naming the headings left out. The rules folder is
/// reached through one pointer line to its index, never copied.
/// </para>
/// </remarks>
public static partial class PacketHostFacts
{
    /// <summary>The heading the appended section starts with.</summary>
    public const string Heading = "## Host facts";

    /// <summary>The most characters of host rules a packet carries, matching the plan-content budget of a task prompt.</summary>
    public const int RulesBudget = 6000;

    private static readonly (string Start, string End)[] GeneratedBlocks =
    [
        (ConfigRegMarkdown.StartMarker, ConfigRegMarkdown.EndMarker),
        ("<!-- CODEGRAPH_START -->", "<!-- CODEGRAPH_END -->"),
    ];

    private const string ScratchpadRoot = ".agent_scratchpad";

    /// <summary>Builds the section for one task.</summary>
    /// <param name="repoRoot">The repository root, where the host <c>AGENTS.md</c> is read.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="role">The agent working the task: the worker's own name, else the one the plan assigned; null when neither is known.</param>
    /// <param name="taskId">The task id, for the scratchpad path.</param>
    /// <param name="diagnostics">Receives notes for the log, such as an empty rules section that was skipped; they are never part of the returned text.</param>
    /// <returns>The section, starting with <see cref="Heading" />.</returns>
    public static string Render(string repoRoot, KyberWeaveConfig config, string? role, string? taskId, IList<string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);

        StringBuilder text = new();
        text.AppendLine(Heading);
        text.AppendLine();
        text.AppendLine("Assembled from this repository's configuration and root AGENTS.md. Open AGENTS.md only for something this section does not answer.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Scratchpad for this task: `{ScratchpadRoot}/{(string.IsNullOrWhiteSpace(taskId) ? "<task-id>" : TaskSlug.Normalise(taskId))}/` (the task gate uses this same folder, so a build you run by hand with `--artifacts-path` here reuses its output)");
        foreach (ConfigRegEntry entry in config.ConfigReg.Resolve(config.Ontology))
        {
            if (entry.Kind == ConfigRegEntryKind.Path)
                text.AppendLine(CultureInfo.InvariantCulture, $"- <{entry.Name}>: `{entry.Path}`");
        }

        AppendGates(text, config, role);
        AppendRules(text, repoRoot, config, diagnostics);
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Commit, for this run (it applies after the host rules above): when your task gate passes, run `git add <the task's files and its tests>` then `git commit -m \"<type>(<scope>): <summary>\" -m \"Task: {taskId ?? "<task-id>"}\" -- <the same paths>`, and return the sha as COMMIT. You work on this run's branch and the run approves commits there, so a host rule that asks for approval before committing, or grants it to autonomous runs, is met for this commit. That approval covers nothing else: never push, and never reset, restore, check out, clean or rebase.");
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

    private static void AppendRules(StringBuilder text, string repoRoot, KyberWeaveConfig config, IList<string>? diagnostics)
    {
        string path = Path.Combine(repoRoot, "AGENTS.md");
        string markdown;
        try
        {
            if (!File.Exists(path))
            {
                text.AppendLine();
                text.AppendLine("Host rules: this repository has no root AGENTS.md.");
                AppendRulesFolderPointer(text, repoRoot, config);
                return;
            }

            markdown = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Host rules: AGENTS.md could not be read ({ex.Message}).");
            return;
        }

        RuleSections found = RuleSectionsOf(markdown);
        foreach (string heading in found.Skipped)
            diagnostics?.Add($"Skipped the empty rules section '{heading}' in AGENTS.md: it has no list items and ends on a lead-in colon.");

        text.AppendLine();
        if (found.Included.Count == 0)
        {
            text.AppendLine(found.Skipped.Count == 0
                ? "Host rules: no heading in AGENTS.md names rules, so none are copied here. Open AGENTS.md if your task needs the repository's rules."
                : "Host rules: every rules heading in AGENTS.md is empty, so none are copied here. Open AGENTS.md if your task needs the repository's rules.");
            AppendRulesFolderPointer(text, repoRoot, config);
            return;
        }

        int used = 0;
        List<string> left = [];
        foreach ((string heading, string body) in found.Included)
        {
            string? shown = left.Count > 0 ? null : Fit(body, RulesBudget - used);
            if (shown is null)
            {
                left.Add(heading);
                continue;
            }

            used += shown.Length;
            text.AppendLine(shown.Length < body.Length
                ? $"Host rules, the first-level bullet lines of AGENTS.md § {heading} ({body.Length} characters, over the {RulesBudget}-character budget; continuation lines and nested items were left out):"
                : $"Host rules, verbatim from AGENTS.md § {heading}:");
            text.AppendLine();
            text.AppendLine(shown);
            text.AppendLine();
        }

        if (left.Count > 0)
            text.AppendLine(CultureInfo.InvariantCulture, $"Not copied here, over the {RulesBudget}-character budget: AGENTS.md § {string.Join("; § ", left)}. Read those sections in AGENTS.md before acting.");

        AppendRulesFolderPointer(text, repoRoot, config);
    }

    private static string? Fit(string body, int room)
    {
        if (body.Length <= room)
            return body;

        string bullets = string.Join('\n', body.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line.TrimEnd()));
        return bullets.Length > 0 && bullets.Length <= room ? bullets : null;
    }

    private static void AppendRulesFolderPointer(StringBuilder text, string repoRoot, KyberWeaveConfig config)
    {
        ConfigRegEntry? index = config.ConfigReg.Resolve(config.Ontology)
            .FirstOrDefault(entry => entry.Kind == ConfigRegEntryKind.Path && entry.Name == ConfigRegConfig.RulesIndexProperty);
        if (index?.Path is not { Length: > 0 } relative || !File.Exists(Path.Combine(repoRoot, relative)))
            return;

        text.AppendLine(CultureInfo.InvariantCulture, $"Rules folder: `{relative}` indexes this repository's per-topic rules. It is not copied here; open it when your task touches a topic it lists.");
    }

    /// <summary>The root file's rules sections in document order, split into those with rule text and those with none.</summary>
    internal sealed record RuleSections(IReadOnlyList<(string Heading, string Body)> Included, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Every section whose heading names rules, in document order, without its heading line and without the generated
    /// CodeGraph and Config Reg blocks. A section with no list item whose text ends on a lead-in colon, or with no
    /// text at all, promises rules it does not carry, so it is reported as skipped rather than included.
    /// </summary>
    internal static RuleSections RuleSectionsOf(string markdown)
    {
        string[] lines = WithoutGeneratedBlocks(markdown.Replace("\r\n", "\n", StringComparison.Ordinal)).Split('\n');

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

        List<(string, string)> included = [];
        List<string> skipped = [];
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

            string title = heading.Groups["title"].Value.Trim();
            string body = string.Join('\n', lines[(i + 1)..end]).Trim();
            if (HasRuleText(body))
                included.Add((title, body));
            else
                skipped.Add(title);

            i = end - 1;
        }

        return new RuleSections(included, skipped);
    }

    private static bool HasRuleText(string body)
    {
        if (body.Length == 0)
            return false;

        return ListItem().IsMatch(body) || !body.TrimEnd().EndsWith(':');
    }

    private static string WithoutGeneratedBlocks(string markdown)
    {
        foreach ((string start, string end) in GeneratedBlocks)
        {
            int from;
            while ((from = markdown.IndexOf(start, StringComparison.Ordinal)) >= 0)
            {
                int to = markdown.IndexOf(end, from, StringComparison.Ordinal);
                markdown = to < 0 ? markdown[..from] : markdown.Remove(from, to + end.Length - from);
            }
        }

        return markdown;
    }

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"\b(rules?|non-?negotiables?|mandatory|must|never|polic(y|ies)|constraints?|guardrails?|prohibitions?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuleHeading();

    [GeneratedRegex(@"^\s*([-*+]|\d+[.)])\s+\S", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();
}

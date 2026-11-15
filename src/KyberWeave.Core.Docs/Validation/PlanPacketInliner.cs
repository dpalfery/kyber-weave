using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>The plan content copied into one task's packet, and what could not be.</summary>
/// <param name="Text">The block to put in the prompt; empty when the task cites nothing.</param>
/// <param name="Findings">One warning per reference the packet could not carry.</param>
internal sealed record InlinedPlanContent(string Text, IReadOnlyList<Diagnostic> Findings);

/// <summary>
/// Copies into a task's packet the plan content the task depends on, verbatim: its Test contract row, every Decisions
/// row it or that row cites, and every plan section it names. A worker sees only the packet, so a reference the
/// packet does not resolve is a gap, reported when the packet is generated.
/// </summary>
internal static partial class PlanPacketInliner
{
    /// <summary>The most characters of plan content one packet carries; the cited rows go in first.</summary>
    internal const int Budget = 6000;

    /// <summary>Builds the block for one task.</summary>
    /// <param name="task">The task.</param>
    /// <param name="body">The task's own text.</param>
    /// <param name="context">The plan's tables and sections.</param>
    /// <param name="artifactPath">The plan, for findings.</param>
    /// <param name="fixCommand">The command that regenerates the task file, for hints.</param>
    public static InlinedPlanContent Build(PlanTask task, string body, PlanPacketContext context, string artifactPath, string fixCommand)
    {
        List<Diagnostic> findings = [];
        List<(string Name, string Title, string Text)> units = [];

        IReadOnlyList<(PlanTable Table, PlanTableRow Row)> contract = context.ContractRowsFor(task.Id);
        if (contract.Count > 0)
        {
            foreach (IGrouping<PlanTable, (PlanTable Table, PlanTableRow Row)> group in contract.GroupBy(entry => entry.Table))
            {
                units.Add(($"test contract row {task.Id}", $"Test contract row for {task.Id}", PlanPacketContext.Render(group.Key, group.Select(entry => entry.Row))));
            }
        }
        else if (ContractWordRegex().IsMatch(body))
        {
            findings.Add(Finding(
                PlanTaskFileValidator.UnresolvedReference,
                $"Task '{task.Id}' refers to the test contract, but the plan's test contract has no row for '{task.Id}', so the packet cannot carry one.",
                task.Id,
                artifactPath,
                $"Add a row for {task.Id} to the Test contract table, for example '| {task.Id} | <test project or file> | <runner filter> | <observable behaviour> | <RED evidence> | <GREEN acceptance> |', then run {fixCommand} again."));
        }

        string cited = body + "\n" + string.Join('\n', contract.Select(entry => entry.Row.Raw));
        List<PlanDecision> decisions = [];
        foreach (string id in PlanPacketContext.CitedDecisionIds(cited))
        {
            if (context.TryGetDecision(id, out PlanDecision? decision))
            {
                decisions.Add(decision);
            }
            else
            {
                findings.Add(Finding(
                    PlanTaskFileValidator.UnresolvedReference,
                    $"Task '{task.Id}' cites decision {id}, but the plan's Decisions table has no row for {id}, so the packet cannot carry it.",
                    task.Id,
                    artifactPath,
                    $"Add a row for {id} to the Decisions table, for example '| {id} | <decision> | <rationale> |', or remove the reference from task '{task.Id}', then run {fixCommand} again."));
            }
        }

        foreach (IGrouping<PlanTable, PlanDecision> group in decisions.GroupBy(decision => decision.Table))
        {
            units.Add((
                $"decision {string.Join(", ", group.Select(decision => decision.Id))}",
                "Decisions this task cites",
                PlanPacketContext.Render(group.Key, group.Select(decision => decision.Row))));
        }

        HashSet<string> resolvedHeadings = new(StringComparer.OrdinalIgnoreCase);
        foreach (PlanSection section in NamedSections(context, body))
        {
            resolvedHeadings.Add(section.Heading);
            units.Add(($"section '{section.Heading}'", $"Plan section: {section.Heading}", section.Body));
        }

        foreach (string phrase in VagueReferences(body, decisions.Count > 0 || resolvedHeadings.Count > 0))
        {
            findings.Add(Finding(
                PlanTaskFileValidator.VagueReference,
                $"Task '{task.Id}' says \"{phrase}\", but names no decision, test contract row or plan section the packet can carry, and a worker cannot open the plan.",
                task.Id,
                artifactPath,
                $"State the detail in task '{task.Id}' itself, or cite it by id or by heading, for example 'see decision D5' or 'see the \"Error states\" section', then run {fixCommand} again."));
        }

        if (units.Count == 0)
        {
            return new InlinedPlanContent(string.Empty, findings);
        }

        StringBuilder text = new("Reference content for this task, quoted exactly from its source document and complete as given:\n");
        int used = 0;
        List<string> leftOut = [];
        string? lastTitle = null;
        foreach ((string name, string title, string unit) in units)
        {
            bool isContract = title.StartsWith("Test contract", StringComparison.Ordinal);
            if (!isContract && used + unit.Length > Budget)
            {
                leftOut.Add(name);
                continue;
            }

            used += unit.Length;
            if (!string.Equals(title, lastTitle, StringComparison.Ordinal))
            {
                text.Append("\n#### ").Append(title).Append('\n');
                lastTitle = title;
            }

            text.Append(unit).Append('\n');
        }

        if (leftOut.Count > 0)
        {
            text.Append("\nLeft out because the packet is limited to ").Append(Budget).Append(" characters of plan content: ")
                .Append(string.Join("; ", leftOut)).Append(". Report it as blocked rather than guessing.\n");
            findings.Add(Finding(
                PlanTaskFileValidator.UnresolvedReference,
                $"Task '{task.Id}' cites more plan content than one packet carries ({Budget} characters); left out: {string.Join("; ", leftOut)}.",
                task.Id,
                artifactPath,
                $"Move what the worker needs into task '{task.Id}' in fewer words, split the task, or cite less, then run {fixCommand} again."));
        }

        return new InlinedPlanContent(text.ToString().TrimEnd('\n') + "\n", findings);
    }

    private static Diagnostic Finding(string code, string message, string task, string artifactPath, string hint) =>
        new(code, Severity.Warning, message, task, artifactPath, Hint: hint);

    private static List<PlanSection> NamedSections(PlanPacketContext context, string body)
    {
        List<PlanSection> tasksSections = [.. context.Sections.Where(section => IsTasksHeading(section.Heading))];
        List<PlanSection> named = [];
        foreach (PlanSection section in context.Sections)
        {
            if (section.Level < 2 || section.Body.Trim().Length == 0 || IsTasksHeading(section.Heading)
                || IsHandled(section.Heading)
                || tasksSections.Any(tasks => section.Line > tasks.Line && section.Line <= tasks.Line + 1 + tasks.Body.Split('\n').Length))
            {
                continue;
            }

            string heading = LeadingNumberRegex().Replace(section.Heading, string.Empty).Trim();
            if (heading.Length < 4)
            {
                continue;
            }

            string escaped = Regex.Escape(heading);
            if (Regex.IsMatch(
                    body,
                    $@"[""'`“‘*]\s*{escaped}\s*[""'`”’*]|\b{escaped}\s+(?:section|table)\b|\bsection\s+[""'`“]?{escaped}|§\s*{escaped}",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)))
            {
                named.Add(section);
            }
        }

        return named;
    }

    private static bool IsTasksHeading(string heading)
    {
        string normalized = heading.Replace('-', ' ').Trim();
        return normalized.Equals("tasks", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("implementation tasks", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHandled(string heading)
    {
        string normalized = heading.Replace('-', ' ');
        return normalized.Contains("decision", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("test contract", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("verification contract", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Phrases that point at plan content without saying which, in lines that resolve to nothing the packet carries.</summary>
    private static List<string> VagueReferences(string body, bool taskResolvesSomething)
    {
        List<string> phrases = [];
        bool fenced = false;
        foreach (string line in body.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            Match match = VagueRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            bool names = PlanPacketContext.CitedDecisionIds(line).Count > 0 || (taskResolvesSomething && SectionWordRegex().IsMatch(line));
            if (!names)
            {
                phrases.Add(match.Value.Trim());
            }
        }

        return phrases;
    }

    [GeneratedRegex(@"\b(?:test|verification) contract\b|\bcontract row\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContractWordRegex();

    [GeneratedRegex(@"^\s*(?:\d+[.)]|[A-Z][.)])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"\bsection\b|§|[""'`]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SectionWordRegex();

    [GeneratedRegex(
        @"\bsee (?:above|below|earlier|the plan|the design|the spec(?:ification)?)\b|\bas (?:described|specified|stated|defined|outlined|noted|listed|discussed) (?:above|earlier|previously|before|in the (?:plan|design|spec(?:ification)?))\b|\b(?:per|according to) the plan\b|\bsame as (?:above|before)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VagueRegex();
}

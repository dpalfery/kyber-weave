using System.Globalization;
using System.Text.RegularExpressions;
using KyberWeave.Core.Review;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>What an archived plan tells a pull request reviewer.</summary>
/// <param name="Title">The plan's title, or null when it has none.</param>
/// <param name="Component">The frontmatter <c>component</c>, or null.</param>
/// <param name="Changes">The plan's task titles, cleaned of task ids and test-first markers.</param>
/// <param name="LowConfidenceDecisions">Autonomous decisions that rest on no authoritative source, as question and answer.</param>
/// <param name="AcceptedRecommendations">How many other decisions were accepted on a recommendation, without a person.</param>
/// <param name="NotVerified">Every <c>NOT VERIFIED</c> item the plan records.</param>
public sealed record PlanFacts(
    string? Title,
    string? Component,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> LowConfidenceDecisions,
    int AcceptedRecommendations,
    IReadOnlyList<string> NotVerified);

/// <summary>The latest review cycle of this change, as its folder records it.</summary>
/// <param name="Cycles">How many review cycles this change went through.</param>
/// <param name="Outcome">The verdict recomputed from the cycle's findings and gates.</param>
/// <param name="Gates">The cycle's gate results.</param>
public sealed record ReviewEvidence(int Cycles, ReviewOutcome Outcome, IReadOnlyList<GateResult> Gates);

/// <summary>
/// Turns what a run recorded (the archived plan, the per-task gate reports, the last review cycle) into the short,
/// plain lines a pull request description carries. Nothing here invents a fact: a piece the run did not record is
/// reported as missing.
/// </summary>
public static partial class RunEvidence
{
    private const int MaxAnswerLength = 160;

    /// <summary>A governed document's title: its frontmatter <c>title</c>, else its first level-one heading; null when it has neither.</summary>
    public static string? DocumentTitle(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        string? title = Frontmatter(normalized, "title")
                        ?? normalized.Split('\n').Select(line => H1().Match(line)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }

    /// <summary>Reads the reviewer-facing facts out of a plan's text.</summary>
    public static PlanFacts ReadPlan(string planText)
    {
        ArgumentNullException.ThrowIfNull(planText);
        string text = planText.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = text.Split('\n');

        string? title = Frontmatter(text, "title") ?? lines.Select(line => H1().Match(line)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        string? component = Frontmatter(text, "component");

        List<string> changes = [];
        foreach (string line in lines)
        {
            Match task = TaskHeading().Match(line);
            if (!task.Success)
                continue;
            string cleaned = Plain(TestFirstMarker().Replace(task.Groups["title"].Value, string.Empty)).Trim().TrimEnd('.');
            if (cleaned.Length == 0)
                continue;
            cleaned = char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
            if (!changes.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                changes.Add(cleaned);
        }

        (List<string> lowConfidence, int accepted) = Decisions(lines);

        List<string> notVerified = [];
        foreach (string line in lines)
        {
            Match item = NotVerifiedLine().Match(line);
            if (!item.Success)
                continue;
            string cleaned = Plain(item.Groups["what"].Value).Trim().TrimEnd('.');
            if (cleaned.Length > 0 && !cleaned.StartsWith('<') && !notVerified.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                notVerified.Add(cleaned);
        }

        return new PlanFacts(title?.Trim(), component?.Trim(), changes, lowConfidence, accepted, notVerified);
    }

    /// <summary>
    /// One line summarizing the per-task gate reports, and the not-verified lines for gates that did not run to the end.
    /// </summary>
    /// <param name="reports">The latest gate report of each task.</param>
    public static (string? Line, IReadOnlyList<string> NotVerified) SummarizeTaskGates(IReadOnlyList<GateReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        if (reports.Count == 0)
            return (null, []);

        List<GateResult> applicable = [.. reports.SelectMany(report => report.Gates).Where(gate => !gate.NotApplicable)];
        string ran = string.Join(", ", applicable.Select(gate => gate.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        string tasks = reports.Count == 1 ? "1 task" : $"{reports.Count.ToString(CultureInfo.InvariantCulture)} tasks";
        List<string> failed = [.. reports
            .SelectMany(report => report.Gates.Where(gate => !gate.NotApplicable && !gate.Passed && gate.Outcome == GateOutcome.Completed))
            .Select(gate => gate.Id).Distinct(StringComparer.Ordinal)];
        List<string> notVerified = [.. reports
            .SelectMany(report => report.Gates.Where(gate => !gate.NotApplicable && gate.Outcome != GateOutcome.Completed))
            .GroupBy(gate => gate.Id, StringComparer.Ordinal)
            .Select(group => $"The {group.Key} check did not run to the end in {Count(group.Count(), "task")} ({Describe(group.First().Outcome)}).")];

        string line = failed.Count == 0
            ? $"Checks per task: {tasks}, all passed ({ran})."
            : $"Checks per task: {tasks} ({ran}); failed: {string.Join(", ", failed)}.";
        return (line, notVerified);
    }

    /// <summary>The validation line for the review, and the open findings a reviewer should look at.</summary>
    public static (string Line, IReadOnlyList<string> Pending) SummarizeReview(ReviewEvidence? review)
    {
        if (review is null)
            return ("Review: no review result was recorded for this branch.", []);

        List<string> pending = [.. review.Outcome.Accepted.Select(finding =>
            $"{Capitalized(finding.Severity.ToString())}: {Plain(finding.Claim ?? finding.Id).TrimEnd('.')} ({finding.File}:{finding.Line.ToString(CultureInfo.InvariantCulture)}).")];
        int failedGates = review.Gates.Count(gate => gate.Blocking && !gate.NotApplicable && !gate.Passed);
        int ranGates = review.Gates.Count(gate => !gate.NotApplicable);
        string rounds = review.Cycles == 1 ? "1 round" : $"{review.Cycles.ToString(CultureInfo.InvariantCulture)} rounds";
        string verdict = review.Outcome.Verdict == ReviewVerdict.Approve ? "approved" : "changes still requested";
        string gates = ranGates == 0 ? string.Empty
            : failedGates == 0 ? $"; its {Count(ranGates, "check")} passed"
            : $"; {failedGates.ToString(CultureInfo.InvariantCulture)} of its {Count(ranGates, "check")} failed";
        string findings = pending.Count == 0 ? "; no open findings"
            : review.Outcome.Verdict == ReviewVerdict.Approve ? $"; {Count(pending.Count, "minor finding")} left for you, listed below"
            : $"; {Count(pending.Count, "finding")} open, listed below";
        return ($"Review: {verdict} after {rounds}{gates}{findings}.", pending);
    }

    private static (List<string> LowConfidence, int Accepted) Decisions(string[] lines)
    {
        List<string> lowConfidence = [];
        int accepted = 0;
        int heading = Array.FindIndex(lines, line => DecisionsHeading().IsMatch(line));
        if (heading < 0)
            return (lowConfidence, accepted);

        int header = Array.FindIndex(lines, heading + 1, line => line.TrimStart().StartsWith('|'));
        if (header < 0)
            return (lowConfidence, accepted);

        string[] columns = Cells(lines[header]);
        int question = Array.FindIndex(columns, cell => cell.Contains("question", StringComparison.OrdinalIgnoreCase));
        if (question < 0)
            question = 1;
        int answer = Array.FindIndex(columns, cell => cell.Contains("answer", StringComparison.OrdinalIgnoreCase) || cell.Contains("decision", StringComparison.OrdinalIgnoreCase));
        for (int index = header + 2; index < lines.Length && lines[index].TrimStart().StartsWith('|'); index++)
        {
            string[] cells = Cells(lines[index]);
            if (cells.Length < 2 || cells[0].StartsWith("AP", StringComparison.OrdinalIgnoreCase))
                continue;

            string status = cells[^1];
            if (LowConfidence().IsMatch(status))
            {
                string q = Plain(cells[Math.Min(question, cells.Length - 1)]).TrimEnd('?', '.');
                string a = answer >= 0 && answer < cells.Length ? FirstSentence(Plain(cells[answer])) : string.Empty;
                lowConfidence.Add(a.Length == 0 ? q + "." : $"{q}: {a}");
            }
            else if (status.Contains("autonomous", StringComparison.OrdinalIgnoreCase))
            {
                accepted++;
            }
        }

        return (lowConfidence, accepted);
    }

    private static string FirstSentence(string text)
    {
        Match sentence = Sentence().Match(text);
        string first = (sentence.Success ? sentence.Value : text).Trim();
        return first.Length <= MaxAnswerLength ? first : first[..MaxAnswerLength].TrimEnd() + "…";
    }

    /// <summary>Markdown links and code spans reduced to their text, and escaped pipes restored.</summary>
    private static string Plain(string text) =>
        MarkdownLink().Replace(text, "$1").Replace("\\|", "|", StringComparison.Ordinal).Replace("**", string.Empty, StringComparison.Ordinal).Trim();

    private static string[] Cells(string row)
    {
        string trimmed = row.Trim();
        if (trimmed.StartsWith('|'))
            trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
            trimmed = trimmed[..^1];
        return [.. CellSeparator().Split(trimmed).Select(cell => cell.Trim())];
    }

    private static string? Frontmatter(string text, string key)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal))
            return null;
        int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return null;
        Match match = new Regex($@"^{Regex.Escape(key)}:[ \t]*(.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Match(text[..end]);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }

    private static string Count(int count, string noun) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string Capitalized(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    private static string Describe(GateOutcome outcome) => outcome switch
    {
        GateOutcome.TimedOut => "it ran past its time limit",
        GateOutcome.NotStarted => "its program could not be started",
        GateOutcome.DeadlinePassed => "the review deadline stopped it",
        _ => "it did not finish",
    };

    [GeneratedRegex(@"^#[ \t]+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex H1();

    [GeneratedRegex(@"^###[ \t]+(?:T[\w.-]*|Task[ \t]+[\w.-]+)[ \t]*[:—–-][ \t]*(?<title>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskHeading();

    [GeneratedRegex(@"^(?:(?:RED|GREEN)\b(?:[ \t]+for\b)?[ \t]*[:—–-]?[ \t]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestFirstMarker();

    [GeneratedRegex(@"NOT VERIFIED:[ \t]*`?(?<what>[^`\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex NotVerifiedLine();

    [GeneratedRegex(@"^##[ \t]+Decisions[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionsHeading();

    [GeneratedRegex(@"autonomous:[ \t]*low-confidence", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LowConfidence();

    [GeneratedRegex(@"^.+?[.!?](?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Sentence();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex CellSeparator();
}

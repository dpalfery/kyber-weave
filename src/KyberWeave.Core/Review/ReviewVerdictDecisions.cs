using System.Text;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Review;

/// <summary>Findings the decision model merged as one defect: the one kept and the ids folded into it.</summary>
/// <param name="Kept">The finding that stays: the highest severity, then the highest confidence, then the most evidence.</param>
/// <param name="Merged">The findings folded into it, each with the confidence of the answer that joined it.</param>
public sealed record MergedFindings(ReviewFinding Kept, IReadOnlyList<(ReviewFinding Finding, double? Confidence)> Merged);

/// <summary>A refutation candidate after the claim check: whether a refuter is still needed, and why.</summary>
/// <param name="Finding">The critical or major finding.</param>
/// <param name="NeedsRefuter">False only when the model confidently found the cited code supports the claim.</param>
/// <param name="Note">What the check found, for the report; null when the model gave no confident answer.</param>
public sealed record ClaimCheck(ReviewFinding Finding, bool NeedsRefuter, string? Note);

/// <summary>
/// The verdict command's four decision-model steps, each asked on the seat files' own text: duplicates are merged and
/// minor nitpicks dropped before the verdict is computed, then each refutation candidate's claim is checked against the
/// cited code and each finding's severity is compared with its stated impact. Only the first two change what the
/// verdict counts, and every change is reported; the last two never change a verdict.
/// </summary>
public static partial class ReviewVerdictDecisions
{
    /// <summary>The diagnostic code every decision-model line of the verdict report carries.</summary>
    public const string Code = "KW-REVIEW-055";

    /// <summary>Does this finding name a nitpick or a substantive defect.</summary>
    public const string NitpickRule = "KW-ARB-NITPICK-001";

    /// <summary>Do two findings name the same defect.</summary>
    public const string DuplicateRule = "KW-ARB-DUP-001";

    /// <summary>Does the cited code support the finding's claim.</summary>
    public const string ClaimRule = "KW-ARB-CLAIM-001";

    /// <summary>Does the stated severity match the stated impact.</summary>
    public const string SeverityRule = "KW-ARB-SEVERITY-001";

    private const int DuplicateWindowLines = 20;
    private const int SurroundingLines = 6;
    private const int MaxSurroundingsFileBytes = 1 << 20;
    private const int MaxEvidenceLines = 12;

    /// <summary>
    /// Asks, for each pair of findings on the same file within 20 lines, whether they name the same defect, and folds
    /// every confident pair into one finding. A rule in shadow logs its answers and merges nothing.
    /// </summary>
    public static async Task<(List<ReviewFinding> Kept, List<MergedFindings> Merged)> MergeDuplicatesAsync(
        ReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(findings);
        ArbiterRule? rule = model.Rule(DuplicateRule);
        if (!model.Configured || rule is null || findings.Count < 2)
            return ([.. findings], []);

        List<(int A, int B)> pairs = [];
        List<ReviewQuestion> questions = [];
        for (int a = 0; a < findings.Count; a++)
        {
            for (int b = a + 1; b < findings.Count; b++)
            {
                if (!string.Equals(Normalize(findings[a].File), Normalize(findings[b].File), StringComparison.Ordinal)
                    || Math.Abs(findings[a].Line - findings[b].Line) > DuplicateWindowLines)
                {
                    continue;
                }

                pairs.Add((a, b));
                questions.Add(new ReviewQuestion(
                    PairSubject(findings[a], findings[b]),
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["finding.excerpt"] = ReviewDecisionModel.FindingText(findings[a]),
                        ["candidate.text"] = ReviewDecisionModel.FindingText(findings[b]),
                    }));
            }
        }

        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer == "same-defect" ? rule.Shadow ? "shadow: would merge" : "merged" : "no effect",
            cancellationToken).ConfigureAwait(false);

        int[] parent = [.. Enumerable.Range(0, findings.Count)];
        Dictionary<int, double?> joinedAt = [];
        foreach ((int a, int b) in pairs)
        {
            ReviewAnswer answer = answers[PairSubject(findings[a], findings[b])];
            if (rule.Shadow || answer.Answer != "same-defect")
                continue;

            parent[Find(parent, b)] = Find(parent, a);
            joinedAt[b] = answer.Score;
            joinedAt.TryAdd(a, answer.Score);
        }

        List<ReviewFinding> kept = [];
        List<MergedFindings> merged = [];
        foreach (IGrouping<int, int> group in Enumerable.Range(0, findings.Count).GroupBy(index => Find(parent, index)))
        {
            int[] members = [.. group];
            int winner = members
                .OrderByDescending(index => findings[index].Severity)
                .ThenByDescending(index => findings[index].Confidence)
                .ThenByDescending(index => (findings[index].Evidence ?? string.Empty).Length)
                .ThenBy(index => index)
                .First();
            kept.Add(findings[winner]);
            if (members.Length > 1)
            {
                merged.Add(new MergedFindings(
                    findings[winner],
                    [.. members.Where(index => index != winner).Select(index => (findings[index], joinedAt.GetValueOrDefault(index)))]));
            }
        }

        // The verdict keeps seat order, so the kept list is put back in the order the seats gave.
        kept.Sort((left, right) => IndexOf(findings, left).CompareTo(IndexOf(findings, right)));
        return (kept, merged);
    }

    /// <summary>
    /// Asks whether each minor finding is a nitpick and drops the confident ones. Critical and major findings are never
    /// asked. A rule in shadow logs its answers and drops nothing.
    /// </summary>
    public static async Task<(List<ReviewFinding> Kept, List<(ReviewFinding Finding, double? Confidence)> Nitpicks)> DropNitpicksAsync(
        ReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(findings);
        ArbiterRule? rule = model.Rule(NitpickRule);
        if (!model.Configured || rule is null)
            return ([.. findings], []);

        List<ReviewQuestion> questions = [.. findings
            .Where(finding => finding.Severity == ReviewSeverity.Minor)
            .Select(finding => new ReviewQuestion(
                finding.Id,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["finding.excerpt"] = ReviewDecisionModel.FindingText(finding) }))];
        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer == "nitpick" ? rule.Shadow ? "shadow: would drop" : "dropped as nitpick" : "no effect",
            cancellationToken).ConfigureAwait(false);

        List<ReviewFinding> kept = [];
        List<(ReviewFinding, double?)> nitpicks = [];
        foreach (ReviewFinding finding in findings)
        {
            if (!rule.Shadow
                && finding.Severity == ReviewSeverity.Minor
                && answers.TryGetValue(finding.Id, out ReviewAnswer? answer)
                && answer.Answer == "nitpick")
            {
                nitpicks.Add((finding, answer.Score));
            }
            else
            {
                kept.Add(finding);
            }
        }

        return (kept, nitpicks);
    }

    /// <summary>
    /// Checks each refutation candidate before a refuter is sent. The excerpt must be in the cited lines of the file as
    /// it is now; then the model is asked whether those lines support the claim. Only a confident "supports" spares the
    /// refuter: a confident "contradicts" still goes to one, because a major finding is never lost on this answer alone.
    /// </summary>
    public static async Task<IReadOnlyList<ClaimCheck>> CheckClaimsAsync(
        ReviewDecisionModel model, string repositoryRoot, IReadOnlyList<ReviewFinding> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(candidates);
        ArbiterRule? rule = model.Rule(ClaimRule);
        if (!model.Configured || rule is null)
            return [.. candidates.Select(finding => new ClaimCheck(finding, true, null))];

        Dictionary<string, string> quoteProblems = new(StringComparer.Ordinal);
        List<ReviewQuestion> questions = [];
        foreach (ReviewFinding finding in candidates)
        {
            string? surroundings = Surroundings(repositoryRoot, finding);
            string? problem = surroundings is null ? "the cited file could not be read"
                : string.IsNullOrWhiteSpace(finding.Excerpt) ? "the finding quotes no code"
                : string.IsNullOrWhiteSpace(finding.Claim) ? "the finding states no claim"
                : !ExcerptIsIn(finding.Excerpt, surroundings) ? "its excerpt is not at the cited lines"
                : null;
            if (problem is not null)
            {
                quoteProblems[finding.Id] = problem;
                continue;
            }

            questions.Add(new ReviewQuestion(
                finding.Id,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["finding.claim"] = finding.Claim,
                    ["finding.excerpt"] = finding.Excerpt,
                    ["file.surroundings"] = surroundings + EvidenceLines(repositoryRoot, finding),
                }));
        }

        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer switch
            {
                "supports" => "claim confirmed, no refuter",
                "contradicts" => "claim contradicted, refuter still sent",
                _ => "refuter sent",
            },
            cancellationToken).ConfigureAwait(false);

        List<ClaimCheck> checks = [];
        foreach (ReviewFinding finding in candidates)
        {
            if (quoteProblems.TryGetValue(finding.Id, out string? problem))
            {
                checks.Add(new ClaimCheck(finding, true, $"not asked: {problem}"));
                continue;
            }

            ReviewAnswer answer = answers.GetValueOrDefault(finding.Id) ?? ReviewAnswer.Undecidable;
            checks.Add(answer.Answer switch
            {
                "supports" => new ClaimCheck(finding, false, $"the cited code supports the claim (confidence {ReviewDecisionModel.Format(answer.Score)})"),
                "contradicts" => new ClaimCheck(finding, true, $"the cited code contradicts the claim (confidence {ReviewDecisionModel.Format(answer.Score)}); the refuter decides"),
                _ => new ClaimCheck(finding, true, null),
            });
        }

        return checks;
    }

    /// <summary>
    /// Asks whether each finding's stated severity matches its stated impact. Advisory only: the verdict keeps the
    /// severity the seat gave, and the report notes a confident mismatch for the lead reviewer.
    /// </summary>
    public static async Task<IReadOnlyList<(ReviewFinding Finding, string Direction, double? Confidence)>> CheckSeveritiesAsync(
        ReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(findings);
        ArbiterRule? rule = model.Rule(SeverityRule);
        if (!model.Configured || rule is null)
            return [];

        List<ReviewQuestion> questions = [.. findings.Select(finding => new ReviewQuestion(
            finding.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["finding.severity"] = SeverityName(finding.Severity),
                ["finding.claim"] = finding.Claim,
                ["finding.failure-scenario"] = finding.FailureScenario,
            }))];
        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer is "too-high" or "too-low" ? $"severity note ({answer.Answer})" : "no effect",
            cancellationToken).ConfigureAwait(false);

        return [.. findings
            .Select(finding => (finding, answer: answers.GetValueOrDefault(finding.Id) ?? ReviewAnswer.Undecidable))
            .Where(pair => pair.answer.Answer is "too-high" or "too-low")
            .Select(pair => (pair.finding, pair.answer.Answer == "too-high" ? "high" : "low", pair.answer.Score))];
    }

    /// <summary>The lines around a finding's cited line, wide enough to hold a multi-line excerpt; null when unreadable.</summary>
    internal static string? Surroundings(string repositoryRoot, ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        string path = Path.GetFullPath(Path.Combine(repositoryRoot, finding.File));
        string root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path) || new FileInfo(path).Length > MaxSurroundingsFileBytes)
                return null;

            int excerptLines = (finding.Excerpt ?? string.Empty).Split('\n').Length;
            int first = Math.Max(1, finding.Line - SurroundingLines);
            string[] window = [.. File.ReadLines(path).Skip(first - 1).Take(finding.Line - first + excerptLines + SurroundingLines)];
            return window.Length == 0 ? null : string.Join('\n', window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The lines of other files the finding's evidence cites (<c>path:12</c>, <c>path:12-14</c>, <c>path line 5</c>,
    /// <c>path lines 5 and 19</c>), read from the tree as it is now. A claim that rests on another file (a document
    /// that contradicts an archived record) cannot be judged from the cited lines alone; the evidence's own citations,
    /// read here rather than taken from the seat's words, are what a reader would open next.
    /// </summary>
    internal static string EvidenceLines(string repositoryRoot, ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (string.IsNullOrWhiteSpace(finding.Evidence))
            return string.Empty;

        StringBuilder text = new();
        int added = 0;
        foreach (System.Text.RegularExpressions.Match match in EvidenceCitation().Matches(finding.Evidence))
        {
            string file = match.Groups["file"].Value;
            string? path = Resolve(repositoryRoot, file);
            if (path is null)
                continue;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (int number in CitedNumbers(match))
            {
                if (number < 1 || number > lines.Length || added >= MaxEvidenceLines)
                    continue;

                text.Append('\n').Append(file).Append(':').Append(number).Append(": ").Append(lines[number - 1].Trim());
                added++;
            }
        }

        return added == 0 ? string.Empty : "\n\nLines the finding's evidence cites, as they are in the tree now:" + text;
    }

    private static IEnumerable<int> CitedNumbers(System.Text.RegularExpressions.Match match)
    {
        if (match.Groups["from"].Success)
        {
            int from = int.Parse(match.Groups["from"].Value, System.Globalization.CultureInfo.InvariantCulture);
            int to = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value, System.Globalization.CultureInfo.InvariantCulture) : from;
            for (int number = from; number <= Math.Min(to, from + 5); number++)
                yield return number;
        }

        foreach (System.Text.RegularExpressions.Capture capture in match.Groups["n"].Captures)
            yield return int.Parse(capture.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? Resolve(string repositoryRoot, string file)
    {
        string root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(repositoryRoot, file));
        return path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path) && new FileInfo(path).Length <= MaxSurroundingsFileBytes
            ? path
            : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<file>[\w./-]+\.\w+)(?::(?<from>\d+)(?:-(?<to>\d+))?|\s+lines?\s+(?<n>\d+)(?:(?:\s*,\s*|\s+and\s+|-)(?<n>\d+))*)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex EvidenceCitation();

    /// <summary>Whether every non-blank excerpt line, trimmed, appears in the window: the quote is really there.</summary>
    internal static bool ExcerptIsIn(string excerpt, string window)
    {
        string[] lines = [.. excerpt.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
        return lines.Length > 0 && lines.All(line => window.Contains(line, StringComparison.Ordinal));
    }

    /// <summary>A severity as seat files write it.</summary>
    public static string SeverityName(ReviewSeverity severity) => severity switch
    {
        ReviewSeverity.Critical => "critical",
        ReviewSeverity.Major => "major",
        ReviewSeverity.Nitpick => "nitpick",
        _ => "minor",
    };

    private static string PairSubject(ReviewFinding a, ReviewFinding b) => $"{a.Id} ~ {b.Id}";

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
            index = parent[index] = parent[parent[index]];
        return index;
    }

    private static int IndexOf(IReadOnlyList<ReviewFinding> findings, ReviewFinding finding)
    {
        for (int index = 0; index < findings.Count; index++)
        {
            if (ReferenceEquals(findings[index], finding))
                return index;
        }

        return -1;
    }

    private static string Normalize(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}

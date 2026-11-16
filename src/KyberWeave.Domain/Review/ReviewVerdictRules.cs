using System.Text;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Review;

/// <summary>
/// The pure rules of the verdict command's decision-model steps: the question bounds, the candidate selection, the
/// quote check and the citation reading. Asking the model and reading the cited files are the application layer's.
/// </summary>
public static partial class ReviewVerdictRules
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

    /// <summary>Does the finding belong to the class a host severity override describes.</summary>
    public const string OverrideRule = "KW-ARB-OVERRIDE-001";

    /// <summary>
    /// The most decision-model questions one step asks in one command run. Pairs multiply with the findings on one
    /// file, and each question is a paid call; past this the remaining comparisons go unasked and are reported.
    /// </summary>
    public const int MaxQuestionsPerStep = 300;

    /// <summary>
    /// The most findings on one file the duplicate step compares, taken in seat order. Candidate pairs grow with the
    /// square of this, so it is the bound on the work, not only on the questions asked.
    /// </summary>
    public const int MaxFindingsPerFile = 500;

    /// <summary>The most findings in all that one step compares, taken in the order the step prefers.</summary>
    public const int MaxFindingsPerStep = 5000;

    private const int DuplicateWindowLines = 20;
    private const int SurroundingLines = 6;
    /// <summary>The most evidence lines one claim question carries.</summary>
    public const int MaxEvidenceLines = 12;
    /// <summary>The most citations of a finding's evidence read for one claim question.</summary>
    public const int MaxEvidenceCitations = 20;

    /// <summary>The most characters of a file's surroundings sent to the model with one claim question.</summary>
    private const int MaxSurroundingsCharacters = 32768;

    /// <summary>The fewest non-blank characters the strongest line of a quoted excerpt must have to verify it.</summary>
    private const int MinSignalCharacters = 8;

    /// <summary>The largest line number a citation may name; a source file is capped at one megabyte.</summary>
    private const int MaxCitedLine = 1 << 20;

    /// <summary>
    /// Groups finding indexes by normalized file, keeping at most <see cref="MaxFindingsPerFile"/> in a file and
    /// <see cref="MaxFindingsPerStep"/> in all. A file gets a group only when a finding is kept in it, so memory follows
    /// the capped total, not the number of distinct files an input names.
    /// </summary>
    /// <param name="findings">The findings, in the order the step prefers.</param>
    /// <param name="leftOut">How many findings a cap left out.</param>
    public static Dictionary<string, List<int>> GroupByFile(IReadOnlyList<ReviewFinding> findings, out int leftOut)
    {
        Dictionary<string, List<int>> byFile = new(StringComparer.Ordinal);
        int considered = 0;
        leftOut = 0;
        for (int index = 0; index < findings.Count; index++)
        {
            string file = Normalize(findings[index].File);
            byFile.TryGetValue(file, out List<int>? group);
            if ((group?.Count ?? 0) >= MaxFindingsPerFile || considered >= MaxFindingsPerStep)
            {
                leftOut++;
                continue;
            }

            // A group exists only once a finding is kept in it, so memory follows the capped total, not the file count.
            if (group is null)
            {
                group = [];
                byFile[file] = group;
            }

            group.Add(index);
            considered++;
        }

        return byFile;
    }

    /// <summary>
    /// The candidate pairs for the duplicate step: findings on the same file within the line window, the nearest and the
    /// most severe first, at most <see cref="MaxQuestionsPerStep"/>. The work is bounded, not only the result: findings
    /// are grouped by file and sorted by line so only neighbours inside the window are paired, a file contributes at
    /// most <see cref="MaxFindingsPerFile"/> findings and a step <see cref="MaxFindingsPerStep"/>, and the chosen pairs
    /// are held in a heap of the cap's size, so memory does not grow with the number of candidates.
    /// </summary>
    /// <returns>The chosen pairs, best first; how many candidates there were; how many findings were left out of the comparison.</returns>
    public static (List<(int A, int B)> Pairs, int CandidateCount, int LeftOut) NearbyPairs(IReadOnlyList<ReviewFinding> findings)
    {
        Dictionary<string, List<int>> byFile = GroupByFile(findings, out int leftOut);

        // A max-heap on the sort key, so the worst chosen pair is the one replaced when a better one turns up.
        PriorityQueue<(int A, int B), (long Distance, int Severity, int A, int B)> chosen = new(
            Comparer<(long Distance, int Severity, int A, int B)>.Create((left, right) => PairOrder(right, left)));
        int candidates = 0;
        foreach (List<int> group in byFile.Values)
        {
            int[] sorted = [.. group.OrderBy(index => findings[index].Line).ThenBy(index => index)];
            for (int first = 0; first < sorted.Length; first++)
            {
                for (int second = first + 1;
                     second < sorted.Length
                     && ReviewSeatInput.LineDistance(findings[sorted[first]].Line, findings[sorted[second]].Line) <= DuplicateWindowLines;
                     second++)
                {
                    int a = Math.Min(sorted[first], sorted[second]);
                    int b = Math.Max(sorted[first], sorted[second]);
                    candidates++;
                    (long Distance, int Severity, int A, int B) key = (
                        ReviewSeatInput.LineDistance(findings[a].Line, findings[b].Line),
                        -Math.Max((int)findings[a].Severity, (int)findings[b].Severity),
                        a,
                        b);
                    if (chosen.Count < MaxQuestionsPerStep)
                    {
                        chosen.Enqueue((a, b), key);
                    }
                    else if (chosen.TryPeek(out _, out (long Distance, int Severity, int A, int B) worst) && PairOrder(key, worst) < 0)
                    {
                        chosen.DequeueEnqueue((a, b), key);
                    }
                }
            }
        }

        List<((int A, int B) Pair, (long Distance, int Severity, int A, int B) Key)> best = [];
        while (chosen.TryDequeue(out (int A, int B) pair, out (long Distance, int Severity, int A, int B) key))
            best.Add((pair, key));

        best.Reverse();
        return ([.. best.Select(entry => entry.Pair)], candidates, leftOut);
    }

    private static int PairOrder(
        (long Distance, int Severity, int A, int B) left,
        (long Distance, int Severity, int A, int B) right)
    {
        int byDistance = left.Distance.CompareTo(right.Distance);
        if (byDistance != 0)
            return byDistance;

        int bySeverity = left.Severity.CompareTo(right.Severity);
        if (bySeverity != 0)
            return bySeverity;

        int byFirst = left.A.CompareTo(right.A);
        return byFirst != 0 ? byFirst : left.B.CompareTo(right.B);
    }

    public static IEnumerable<int> CitedNumbers(System.Text.RegularExpressions.Match match)
    {
        // The numbers come from seat-written text: one that does not parse as a plausible line number is ignored.
        if (match.Groups["from"].Success && TryLineNumber(match.Groups["from"].Value, out int from))
        {
            int to = match.Groups["to"].Success && TryLineNumber(match.Groups["to"].Value, out int parsedTo) ? parsedTo : from;
            for (int number = from; number <= Math.Min(to, from + 5); number++)
                yield return number;
        }

        foreach (System.Text.RegularExpressions.Capture capture in match.Groups["n"].Captures)
        {
            if (TryLineNumber(capture.Value, out int number))
                yield return number;
        }
    }

    public static bool TryLineNumber(string text, out int number) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number)
        && number is >= 1 and <= MaxCitedLine;

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<file>[\w./-]+\.\w+)(?::(?<from>\d+)(?:-(?<to>\d+))?|\s+lines?\s+(?<n>\d+)(?:(?:\s*,\s*|\s+and\s+|-)(?<n>\d+))*)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    public static partial System.Text.RegularExpressions.Regex EvidenceCitation();

    /// <summary>The first line (1-based) and the line count of the window around a finding's cited line, wide enough to hold a multi-line excerpt.</summary>
    /// <remarks>The excerpt is counted, never split, and capped, so the window stays a few dozen lines whatever the seat wrote.</remarks>
    public static (long First, long Count) SurroundingsRange(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        int excerptLines = Math.Min(
            (finding.Excerpt ?? string.Empty).AsSpan().Count('\n') + 1, ReviewSeatInput.MaxExcerptLines);
        long first = Math.Max(1L, (long)finding.Line - SurroundingLines);
        long count = Math.Max(0L, finding.Line - first + excerptLines + SurroundingLines);
        return (first, count);
    }

    /// <summary>The window's text, capped; null when it holds no line.</summary>
    public static string? SurroundingsText(string[] window)
    {
        ArgumentNullException.ThrowIfNull(window);
        string text = string.Join('\n', window);
        return window.Length == 0
            ? null
            : text.Length <= MaxSurroundingsCharacters ? text : text[..ReviewSeatInput.CutIndex(text, MaxSurroundingsCharacters)];
    }

    /// <summary>
    /// Whether the quote is really in the window. Lines that are only punctuation (a brace, a bracket) carry no
    /// signal and are not judged; at least one other line must have <see cref="MinSignalCharacters"/> non-blank
    /// characters, and the judged lines must appear in the order given. A weaker excerpt is "not verified".
    /// </summary>
    public static bool ExcerptIsIn(string excerpt, string window)
    {
        if (!ReviewSeatInput.ExcerptWithinLimits(excerpt))
            return false;

        string[] lines = [.. excerpt.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Any(char.IsLetterOrDigit))];
        if (!lines.Any(line => line.Count(character => !char.IsWhiteSpace(character)) >= MinSignalCharacters))
            return false;

        int from = 0;
        foreach (string line in lines)
        {
            int at = window.IndexOf(line, from, StringComparison.Ordinal);
            if (at < 0)
                return false;
            from = at + line.Length;
        }

        return true;
    }

    /// <summary>A severity as seat files write it.</summary>
    public static string SeverityName(ReviewSeverity severity) => SeverityOverrides.SeverityName(severity);

    public static string PairSubject(ReviewFinding a, ReviewFinding b) => $"{a.Id} ~ {b.Id}";

    public static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
            index = parent[index] = parent[parent[index]];
        return index;
    }

    public static string Normalize(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}

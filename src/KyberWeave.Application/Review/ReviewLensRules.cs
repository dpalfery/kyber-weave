using System.Globalization;
using System.Text;

namespace KyberWeave.Core.Review;

/// <summary>The pure rules of the lens-applicability step of <c>review seats</c>: which lenses are asked, and the summary of the change the question reads.</summary>
public static class ReviewLensRules
{
    /// <summary>The rule whose question, state and ceiling the step reuses.</summary>
    public const string Rule = "KW-ARB-LENS-001";

    /// <summary>The lenses never asked: always seated for a code change, or decided by the gate run.</summary>
    public static IReadOnlyList<string> NeverAsked { get; } =
        ["correctness", "test-adequacy", .. CouncilSeating.AfterGates];

    private const int MaxSummaryChars = 6000;

    /// <summary>
    /// The change as the question reads it: every changed file grouped by its top folder with added and removed line
    /// counts, then the totals, cut to a bounded size.
    /// </summary>
    /// <param name="changed">The changed paths.</param>
    /// <param name="patch">The unified diff of the change, from which line counts are taken; may be empty.</param>
    public static string Summarize(IReadOnlyList<string> changed, string patch)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(patch);
        Dictionary<string, (int Added, int Removed)> counts = CountLines(patch);
        StringBuilder text = new();
        int added = 0;
        int removed = 0;
        foreach (IGrouping<string, string> area in changed
                     .Order(StringComparer.Ordinal)
                     .GroupBy(path => path.Contains('/', StringComparison.Ordinal) ? path[..path.IndexOf('/', StringComparison.Ordinal)] : "(root)"))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{area.Key}/");
            foreach (string path in area)
            {
                (int plus, int minus) = counts.GetValueOrDefault(path);
                added += plus;
                removed += minus;
                text.AppendLine(CultureInfo.InvariantCulture, $"  {path} +{plus} -{minus}");
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"{changed.Count} files changed, {added} insertions(+), {removed} deletions(-)");
        string summary = text.ToString();
        return summary.Length <= MaxSummaryChars ? summary : summary[..ReviewSeatInput.CutIndex(summary, MaxSummaryChars)] + "\n(cut)";
    }

    private static Dictionary<string, (int Added, int Removed)> CountLines(string patch)
    {
        Dictionary<string, (int, int)> counts = new(StringComparer.Ordinal);
        string? current = null;
        int plus = 0;
        int minus = 0;
        foreach (string line in patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (current is not null)
                    counts[current] = (plus, minus);
                int bPath = line.LastIndexOf(" b/", StringComparison.Ordinal);
                current = bPath >= 0 ? line[(bPath + 3)..] : null;
                plus = 0;
                minus = 0;
            }
            else if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
            }
            else if (line.StartsWith('+'))
            {
                plus++;
            }
            else if (line.StartsWith('-'))
            {
                minus++;
            }
        }

        if (current is not null)
            counts[current] = (plus, minus);
        return counts;
    }
}

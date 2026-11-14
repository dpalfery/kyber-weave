using System.Globalization;
using System.Text;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Core.Review;

/// <summary>
/// The decision-model step of <c>review seats</c>: after the file-pattern step, each lens that is a matter of judgement
/// is asked the Arbiter's lens-applicability question (<c>KW-ARB-LENS-001</c>) on a compact summary of the change and
/// the lens's own Applicability text. Only a confident "does not apply" leaves a lens unseated.
/// </summary>
/// <remarks>
/// The question is only whether the lens's subject matter is in the diff at all; it is never a way to size the council
/// to the change. <c>correctness</c> and <c>test-adequacy</c> are always seated for a code change, and the lenses that
/// read a gate's output are decided by the gate run, so none of them is asked.
/// </remarks>
public static class ReviewLensApplicability
{
    /// <summary>The rule whose question, state and ceiling the step reuses.</summary>
    public const string Rule = "KW-ARB-LENS-001";

    private const int MaxSummaryChars = 6000;

    /// <summary>The lenses never asked: always seated for a code change, or decided by the gate run.</summary>
    public static IReadOnlyList<string> NeverAsked { get; } =
        ["correctness", "test-adequacy", .. CouncilSeating.AfterGates];

    /// <summary>
    /// Asks the question for each seated judgement lens and returns the lenses the model confidently ruled out, each
    /// with the reason <c>--not-seated</c> records.
    /// </summary>
    /// <param name="model">The decision model; one not configured asks nothing.</param>
    /// <param name="seated">The lenses the file-pattern step seated.</param>
    /// <param name="changeSummary">The compact change summary from <see cref="Summarize"/>.</param>
    /// <param name="cancellationToken">Cancels the provider calls.</param>
    public static async Task<IReadOnlyDictionary<string, string>> NotApplicableAsync(
        ReviewDecisionModel model, IReadOnlyList<string> seated, string changeSummary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(seated);
        Dictionary<string, string> notSeated = new(StringComparer.Ordinal);
        ArbiterRule? rule = model.Rule(Rule);
        if (!model.Configured || rule is null)
            return notSeated;

        IReadOnlyDictionary<string, ArbiterSquadLens> lenses = ArbiterSquadCatalog.LoadEmbedded().Lenses;
        List<ReviewQuestion> questions = [.. seated
            .Where(lens => !NeverAsked.Contains(lens, StringComparer.Ordinal) && lenses.ContainsKey(lens))
            .Select(lens => new ReviewQuestion(
                lens,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["lens.applicability"] = lenses[lens].Applicability,
                    ["review.diff-summary"] = changeSummary,
                },
                lens))];
        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer == "not-applicable" ? "not seated" : "seated",
            cancellationToken).ConfigureAwait(false);

        foreach ((string lens, ReviewAnswer answer) in answers)
        {
            if (answer.Answer == "not-applicable")
            {
                // A noul's score is the probability the lens applies, so the confidence of "does not apply" is its complement.
                double? confidence = answer.Score is double applies ? 1 - applies : null;
                notSeated[lens] = $"decision model: not applicable (confidence {ReviewDecisionModel.Format(confidence)})";
            }
        }

        return notSeated;
    }

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
        return summary.Length <= MaxSummaryChars ? summary : summary[..MaxSummaryChars] + "\n(cut)";
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

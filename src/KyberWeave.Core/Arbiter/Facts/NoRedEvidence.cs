using System.Text.RegularExpressions;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The ways a plan's <c>RED evidence required</c> cell or a worker's <c>RED_EVIDENCE:</c>
/// line says there is no RED evidence. The spellings are the ones real plans use.
/// </summary>
internal static partial class NoRedEvidence
{
    /// <summary>Whether <paramref name="text"/> is blank or says there is no RED evidence.</summary>
    /// <param name="text">The cell or marker value.</param>
    /// <returns>True for "none" and its spellings; false for "none of the 6 tests pass".</returns>
    public static bool IsStatedBy(string text) =>
        string.IsNullOrWhiteSpace(text) || Spelling().IsMatch(text.Trim());

    [GeneratedRegex(
        @"^[`*_]*(?:[-\u2013\u2014]+|n/?a|not[ -]applicable"
        + @"|none(?: (?:required|needed|captured|found|recorded|observed|available))?"
        + @"|not (?:required|needed|run|captured|recorded|available)"
        + @"|not a (?:test task|red product test)"
        + @"|no (?:red |failing test )?evidence(?: (?:required|captured|found|recorded|available))?"
        + @"|no failing tests?"
        + @"|no[ -]tests?(?: (?:task|required|needed|owed|failed|fail))?"
        + @"|no (?:product )?red)"
        + @"[`*_]*\s*(?:$|[-\u2013\u2014:.,;(])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Spelling();
}

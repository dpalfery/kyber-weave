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
        @"^[`*_]*(?:[-–—]+|n/?a|none(?: (?:required|needed))?|not[ -]applicable|not (?:required|needed)"
        + @"|not a (?:test task|red product test)|no[ -]tests?(?: (?:task|required|needed|owed))?|no (?:product )?red)"
        + @"[`*_]*\s*(?:$|[-–—:.,;(])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Spelling();
}

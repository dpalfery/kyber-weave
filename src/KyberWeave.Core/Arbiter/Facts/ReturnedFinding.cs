using System.Text.RegularExpressions;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>One finding a lens returned: its id, its own text, and the severity it states.</summary>
/// <param name="Id">The finding id the block names.</param>
/// <param name="Text">The block's text, from its <c>id:</c> line up to the next finding.</param>
/// <param name="Severity">The <c>severity:</c> value, or null when the block states none.</param>
public sealed record ReturnedFinding(string Id, string Text, string? Severity)
{
    /// <summary>The line prefix a stored finding carries its drop status on.</summary>
    public const string StatusPrefix = "status: ";

    /// <summary>The status a finding the nitpick filter dropped is stored with.</summary>
    public const string DroppedStatus = "dropped";

    /// <summary>The status a finding the filter kept is stored with.</summary>
    public const string KeptStatus = "kept";

    /// <summary>The text a finding is stored as: its own text, then its drop status on its own line.</summary>
    /// <param name="text">The finding's text, as returned.</param>
    /// <param name="dropped">Whether the nitpick filter dropped it.</param>
    public static string StoredText(string text, bool dropped) =>
        text + "\n" + StatusPrefix + (dropped ? DroppedStatus : KeptStatus);

    /// <summary>Splits a lens return into its findings, one per <c>id:</c> line, in order.</summary>
    /// <remarks>
    /// The same split <c>arbiter_evaluate</c> uses for refutations, so a finding is one block
    /// everywhere. A return with no <c>id:</c> line has no findings.
    /// </remarks>
    public static IReadOnlyList<ReturnedFinding> ParseAll(string? toolOutput)
    {
        if (string.IsNullOrEmpty(toolOutput))
        {
            return [];
        }

        MatchCollection starts = IdLine.Matches(toolOutput);
        List<ReturnedFinding> findings = new(starts.Count);
        for (int index = 0; index < starts.Count; index++)
        {
            int end = index + 1 < starts.Count ? starts[index + 1].Index : toolOutput.Length;
            string text = toolOutput[starts[index].Index..end];
            Match severity = SeverityLine.Match(text);
            findings.Add(new ReturnedFinding(
                starts[index].Groups["id"].Value,
                text,
                severity.Success ? severity.Groups["value"].Value.Trim() : null));
        }

        return findings;
    }

    private static readonly Regex IdLine = new(
        @"^[ \t]*-?[ \t]*id:[ \t]*(?<id>\S*)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex SeverityLine = new(
        @"^[ \t]*-?[ \t]*severity:[ \t]*(?<value>\S.*?)[ \t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
}

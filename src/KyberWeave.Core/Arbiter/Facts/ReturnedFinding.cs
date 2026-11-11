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

    /// <summary>
    /// The comparison key of a finding id: the slug after the lens in <c>&lt;lens&gt;/&lt;slug&gt;</c>, or the id itself when
    /// it has no lens. A refutation names its finding by the bare slug while the store keeps the lens-qualified id, so
    /// every comparison of ids passes both sides through this one normaliser.
    /// </summary>
    /// <param name="id">A finding id, bare or lens-qualified.</param>
    public static string IdKey(string id)
    {
        int slash = id.LastIndexOf('/');
        return slash < 0 ? id : id[(slash + 1)..];
    }

    /// <summary>Splits a lens return into its findings, one per <c>id:</c> line, in order.</summary>
    /// <remarks>
    /// The same split <c>arbiter_evaluate_agent_dispatch_or_return</c> uses for refutations, so a finding is one block
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
                severity.Success ? NormaliseSeverity(severity.Groups["value"].Value) : null));
        }

        return findings;
    }

    /// <summary>
    /// The severity in the one spelling the rules compare against: first letter capitalised, the rest lowercase, so
    /// <c>critical</c>, <c>Critical</c> and <c>CRITICAL</c> all read as <c>Critical</c>.
    /// </summary>
    /// <remarks>
    /// Normalised once, here, so every consumer (the rule's <c>equals</c> test, the stored fact) reads one form. A lens
    /// may write its severity in any case; the rule must not depend on which.
    /// </remarks>
    private static string NormaliseSeverity(string raw)
    {
        string value = raw.Trim();
        return value.Length == 0
            ? value
            : char.ToUpperInvariant(value[0]) + new string(value[1..].Select(char.ToLowerInvariant).ToArray());
    }

    private static readonly Regex IdLine = new(
        @"^[ \t]*-?[ \t]*id:[ \t]*(?<id>\S+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex SeverityLine = new(
        @"^[ \t]*-?[ \t]*severity:[ \t]*(?<value>\S.*?)[ \t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
}

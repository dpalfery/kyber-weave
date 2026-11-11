namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>What a refuter's return says about the finding it was asked to refute.</summary>
public enum RefutationVerdict
{
    /// <summary>The refuter could not defeat the finding (<c>SUPPORTED:</c>).</summary>
    Confirmed,

    /// <summary>The refuter defeated the finding (<c>REFUTED:</c>).</summary>
    Refuted,

    /// <summary>The return carries no recognised verdict. The finding is kept; nothing is inferred from it.</summary>
    Unknown,
}

/// <summary>Reads the verdict a refuter returns, as the review-lens agent states it.</summary>
/// <remarks>
/// A refuter returns one outcome line: <c>REFUTED: &lt;evidence&gt;</c> or <c>SUPPORTED: &lt;evidence&gt;</c>.
/// Anything else is <see cref="RefutationVerdict.Unknown"/>, so an unexpected return never removes a finding.
/// </remarks>
public static class RefutationOutcome
{
    private const string RefutedPrefix = "REFUTED:";
    private const string SupportedPrefix = "SUPPORTED:";

    /// <summary>Reads the verdict from the first non-blank line of a refuter's return.</summary>
    /// <param name="toolOutput">The return text, or null when the return carried none.</param>
    /// <returns>The verdict, or <see cref="RefutationVerdict.Unknown"/> when no recognised prefix leads the text.</returns>
    public static RefutationVerdict Parse(string? toolOutput)
    {
        if (string.IsNullOrWhiteSpace(toolOutput))
            return RefutationVerdict.Unknown;

        string firstLine = toolOutput
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .First(line => line.Length > 0);

        if (firstLine.StartsWith(RefutedPrefix, StringComparison.Ordinal))
            return RefutationVerdict.Refuted;
        if (firstLine.StartsWith(SupportedPrefix, StringComparison.Ordinal))
            return RefutationVerdict.Confirmed;
        return RefutationVerdict.Unknown;
    }

    /// <summary>The word a stored outcome line names its verdict with.</summary>
    /// <param name="verdict">The verdict to name.</param>
    public static string Word(RefutationVerdict verdict) => verdict switch
    {
        RefutationVerdict.Confirmed => "confirmed",
        RefutationVerdict.Refuted => "refuted",
        _ => "unknown",
    };
}

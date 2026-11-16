namespace KyberWeave.Mcp;

/// <summary>The values the corpus provenance header reports when information could not be read.</summary>
public static class CorpusProvenance
{
    /// <summary>What a field carries when the information could not be read. Never a real value.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>What a detached HEAD is reported as, which is a real state rather than a failure.</summary>
    public const string Detached = "detached";

    /// <summary>The first non-blank line of <paramref name="text"/>, trimmed, or <see cref="Unavailable"/>.</summary>
    /// <param name="text">The text to read, typically a command's standard error.</param>
    public static string FirstLine(string text)
    {
        string trimmed = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(Unavailable).Trim();
        return string.IsNullOrEmpty(trimmed) ? Unavailable : trimmed;
    }
}

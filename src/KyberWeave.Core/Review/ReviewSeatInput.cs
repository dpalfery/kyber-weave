using System.Security;

namespace KyberWeave.Core.Review;

/// <summary>
/// The limits and the one failure filter the review commands apply to what a seat wrote: its numbers, its text and the
/// paths it names. A seat is untrusted input, so every number is range-checked where it is read, every string is
/// capped before it is split, scanned or sent on, and every file read of a seat-named path answers failure the same way.
/// </summary>
internal static class ReviewSeatInput
{
    /// <summary>The largest line number a finding may cite; a source file is capped at one megabyte, far below this.</summary>
    public const int MaxLine = 10_000_000;

    /// <summary>The most characters of a finding's excerpt that are processed; a longer one is "quote not verified".</summary>
    public const int MaxExcerptCharacters = 8192;

    /// <summary>The most lines of a finding's excerpt that are processed; a longer one is "quote not verified".</summary>
    public const int MaxExcerptLines = 100;

    /// <summary>The most characters of any other seat text (claim, evidence, failure scenario, suggestion) that is scanned, sent to the model or printed.</summary>
    public const int MaxTextCharacters = 4096;

    /// <summary>The largest file read on a seat's word, in bytes.</summary>
    public const long MaxFileBytes = 1 << 20;

    /// <summary>
    /// Whether <paramref name="exception"/> is one of the ways reading a seat-named path can fail: an I/O error, a
    /// permission refusal (including the security exception a locked-down root raises), or a name the file system
    /// cannot take. Every such failure is an answer ("could not be read"), not a crash.
    /// </summary>
    public static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    /// <summary>Whether a line number is one a finding may cite: from 0 (no line) to <see cref="MaxLine"/>.</summary>
    public static bool IsLineNumber(long line) => line is >= 0 and <= MaxLine;

    /// <summary>The distance between two line numbers, computed so it cannot overflow.</summary>
    public static long LineDistance(int first, int second) => Math.Abs((long)first - second);

    /// <summary>Whether <paramref name="excerpt"/> is within the character and line limits, counted without splitting it.</summary>
    public static bool ExcerptWithinLimits(string excerpt) =>
        excerpt.Length <= MaxExcerptCharacters && excerpt.AsSpan().Count('\n') < MaxExcerptLines;

    /// <summary>The text cut to <see cref="MaxTextCharacters"/>, with a marker when it was cut; null stays null.</summary>
    /// <remarks>The cut backs off one character rather than split a surrogate pair.</remarks>
    public static string? Clip(string? text)
    {
        if (text is null || text.Length <= MaxTextCharacters)
            return text;

        int cut = char.IsHighSurrogate(text[MaxTextCharacters - 1]) ? MaxTextCharacters - 1 : MaxTextCharacters;
        return text[..cut] + "…";
    }
}

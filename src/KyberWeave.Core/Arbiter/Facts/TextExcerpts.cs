namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// Bounded excerpts of agent-written text for model questions. A question reads the head of a dispatch, where its
/// headers and instructions sit, and the tail of a report, where its digest closes it. Truncating to an excerpt
/// keeps a batched request inside the provider's budget; a request over budget would go unasked.
/// </summary>
public static class TextExcerpts
{
    /// <summary>The longest excerpt any one fact carries, in characters.</summary>
    public const int MaxChars = 3000;

    /// <summary>The marker that stands where text was cut.</summary>
    public const string CutMarker = "[…]";

    /// <summary>The first <paramref name="maxChars"/> characters of <paramref name="text"/>, or null for blank text.</summary>
    public static string? Head(string? text, int maxChars = MaxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Length <= maxChars ? text : text[..maxChars] + "\n" + CutMarker;
    }

    /// <summary>The last <paramref name="maxChars"/> characters of <paramref name="text"/>, or null for blank text.</summary>
    public static string? Tail(string? text, int maxChars = MaxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Length <= maxChars ? text : CutMarker + "\n" + text[^maxChars..];
    }

    /// <summary>The tail of a worker's return, with a harness's JSON envelope flattened to its text first.</summary>
    public static string? ReturnTail(string? output, int maxChars = MaxChars) =>
        string.IsNullOrWhiteSpace(output) ? null : Tail(ReturnMarkerParser.Flatten(output), maxChars);
}

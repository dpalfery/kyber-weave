using System.Text.Json;

namespace KyberDash.Data.Collector;

/// <summary>
/// Classifies whether a raw string payload is a standalone JSON document (object or array)
/// that must be withheld from plain-text storage.
/// </summary>
public static class JsonDocumentText
{
    /// <summary>
    /// Returns <see langword="true"/> if and only if <paramref name="text"/>, after trimming
    /// surrounding whitespace, starts with <c>{</c> or <c>[</c> and parses cleanly as a single
    /// JSON object or array with no trailing tokens.
    /// </summary>
    /// <param name="text">Raw text payload to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="text"/> is a complete JSON object or array;
    /// <see langword="false"/> for JSON scalars, empty/blank strings, malformed JSON, trailing
    /// tokens, or plain prose containing a JSON fragment.
    /// </returns>
    public static bool IsDocument(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        int start = 0;
        int end = text.Length - 1;
        while (start <= end && char.IsWhiteSpace(text[start]))
            start++;
        while (end >= start && char.IsWhiteSpace(text[end]))
            end--;

        if (start > end)
            return false;

        char first = text[start];
        if (first is not ('{' or '['))
            return false;

        try
        {
            ReadOnlyMemory<char> trimmed = text.AsMemory(start, end - start + 1);
            using JsonDocument document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

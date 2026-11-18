namespace KyberWeave.Tests.Arbiter;

/// <summary>Splits corpus text into lines the way <see cref="File.ReadAllLines(string)"/> splits a file.</summary>
internal static class CorpusLines
{
    /// <summary>Splits text into lines without their terminators.</summary>
    /// <param name="text">The text to split.</param>
    /// <returns>
    /// One entry per line; empty for empty text. A file ending in a line break has no entry for the break itself, and
    /// a trailing blank line is an empty entry, exactly as <see cref="File.ReadAllLines(string)"/> reports them.
    /// </returns>
    public static string[] Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
            return [];

        string[] lines = text.Split('\n');
        int count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        return [.. lines[..count].Select(line => line.TrimEnd('\r'))];
    }
}

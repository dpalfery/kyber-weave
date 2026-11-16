namespace KyberWeave.Core.Squad.Briefs;

/// <summary>One reference file's part in a brief.</summary>
/// <param name="File">The authored file name from the map.</param>
/// <param name="ReadFrom">The path the text was read from.</param>
/// <param name="WholeFileLeftOut">The condition that left the whole file out, or null when it was read.</param>
/// <param name="LeftOut">Headings left out, with the condition that removed each.</param>
/// <param name="Unmapped">Headings the map does not mention; their text is included.</param>
/// <param name="MissingFromFile">Headings the map names that the file does not have.</param>
/// <param name="Text">The included text, in document order.</param>
/// <param name="FullCharacters">The size of the whole file in characters.</param>
public sealed record BriefPart(
    string File,
    string ReadFrom,
    string? WholeFileLeftOut,
    IReadOnlyList<(string Heading, string Condition)> LeftOut,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> MissingFromFile,
    string Text,
    int FullCharacters);

/// <summary>A brief: the parts that apply to the facts, and what was left out.</summary>
/// <param name="Agent">The agent the brief is for.</param>
/// <param name="Facts">The facts the selection used.</param>
/// <param name="Parts">One part per file in the map, in map order.</param>
/// <param name="UnlistedFiles">Reference files present beside the mapped ones that the map does not name. They are not in the brief; read them whole.</param>
public sealed record BriefResult(string Agent, BriefFacts Facts, IReadOnlyList<BriefPart> Parts, IReadOnlyList<string> UnlistedFiles)
{
    /// <summary>The characters of every mapped reference file, whole.</summary>
    public int FullCharacters => Parts.Sum(part => part.FullCharacters);

    /// <summary>The characters of the text the brief carries.</summary>
    public int BriefCharacters => Parts.Sum(part => part.Text.Length);
}

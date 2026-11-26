using System.Text.RegularExpressions;

namespace KyberWeave.Core.Docs.Model;

/// <summary>
/// The implementation status a plan or specification declares in its body, with every accepted spelling and
/// the distinctions the lifecycle rules draw from it. One definition, so a plan's status line and every
/// validator that reads it cannot disagree about what a word means.
/// </summary>
public static partial class PlanStatus
{
    /// <summary>A plan an architect has written but not finalized.</summary>
    public const string Draft = "Draft";

    /// <summary>An approved plan whose work may start.</summary>
    public const string Ready = "Ready";

    /// <summary>A plan whose work is under way.</summary>
    public const string InProgress = "In progress";

    /// <summary>A plan whose work waits on something outside it.</summary>
    public const string Blocked = "Blocked";

    /// <summary>A plan whose work is done and is waiting for review.</summary>
    public const string ReviewRequired = "Review required";

    /// <summary>A plan whose work is done.</summary>
    public const string Completed = "Completed";

    /// <summary>A plan that has been moved into the archive.</summary>
    public const string Archived = "Archived";

    /// <summary>A plan a later plan replaces.</summary>
    public const string Superseded = "Superseded";

    /// <summary>The words that name an active plan in a message.</summary>
    public const string ActiveWords = Ready + " or " + InProgress;

    /// <summary>The words that name a plan in review in a message.</summary>
    public const string InReviewWords = ReviewRequired + " or " + Blocked;

    /// <summary>Every open status, in lifecycle order.</summary>
    public static IReadOnlyList<string> OpenWords { get; } = [Draft, Ready, InProgress, Blocked, ReviewRequired];

    /// <summary>Every closed status.</summary>
    public static IReadOnlyList<string> ClosedWords { get; } = [Completed, Archived, Superseded];

    /// <summary>A case-insensitive regular expression alternative naming every open status, hyphen forms included.</summary>
    public static string OpenPattern { get; } = Pattern(OpenWords);

    private static readonly Dictionary<string, string> Canonical = BuildCanonical();

    /// <summary>
    /// The canonical status a value names, or null when it names none. Accepts a plain status word, a
    /// hyphenated spelling such as <c>In-progress</c>, and a row value with a trailing date
    /// (<c>Ready, 2026-11-26</c>).
    /// </summary>
    public static string? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string text = value.Trim().Trim('*', '_', '`', ' ');
        int end = 0;
        while (end < text.Length && (char.IsLetter(text[end]) || text[end] is ' ' or '\t' or '-'))
            end++;

        string word = text[..end].Trim().TrimEnd('-');
        return Canonical.TryGetValue(Key(word), out string? status) ? status : null;
    }

    /// <summary>
    /// The canonical status the document body declares on its status line, or null when it has none.
    /// Reads only a line whose label is <c>**Status:**</c>, <c>**Status**:</c> or <c>**Status:</c>, so a
    /// status word quoted anywhere else in the body is not mistaken for the plan's own.
    /// </summary>
    public static string? ParseBodyStatus(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return null;

        foreach (string line in body.Split('\n'))
        {
            Match match = StatusLine().Match(line);
            if (!match.Success)
                continue;

            string? status = Parse(match.Groups["value"].Value);
            if (status is not null)
                return status;
        }

        return null;
    }

    /// <summary>True when the status is <see cref="Ready"/> or <see cref="InProgress"/>.</summary>
    public static bool IsActive(string? status) => Is(status, Ready, InProgress);

    /// <summary>True when the status is <see cref="ReviewRequired"/> or <see cref="Blocked"/>.</summary>
    public static bool IsInReview(string? status) => Is(status, ReviewRequired, Blocked);

    /// <summary>True when the status still needs work: any status other than the closed ones.</summary>
    public static bool IsOpen(string? status) => Is(status, [.. OpenWords]);

    /// <summary>True when the status is <see cref="Completed"/>, <see cref="Archived"/> or <see cref="Superseded"/>.</summary>
    public static bool IsClosed(string? status) => Is(status, [.. ClosedWords]);

    /// <summary>True when the status is <see cref="Ready"/>.</summary>
    public static bool IsReady(string? status) => Is(status, Ready);

    /// <summary>True when the status is <see cref="Draft"/>.</summary>
    public static bool IsDraft(string? status) => Is(status, Draft);

    private static bool Is(string? status, params string[] words)
    {
        string? canonical = Parse(status);
        return canonical is not null && words.Contains(canonical, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BuildCanonical()
    {
        Dictionary<string, string> canonical = new(StringComparer.Ordinal);
        foreach (string word in OpenWords.Concat(ClosedWords))
            canonical[Key(word)] = word;

        canonical[Key("In-progress")] = InProgress;
        canonical[Key("Review-required")] = ReviewRequired;
        canonical[Key("Complete")] = Completed;
        return canonical;
    }

    private static string Key(string value) =>
        string.Join(' ', value.Split([' ', '\t', '-'], StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string Pattern(IEnumerable<string> words) =>
        string.Join("|", words.Select(word => string.Join("[ -]+", word.Split(' ').Select(Regex.Escape))));

    [GeneratedRegex(@"^[ \t]*\*\*Status(?:\*\*)?[ \t]*:(?:\*\*)?[ \t]*(?<value>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatusLine();
}

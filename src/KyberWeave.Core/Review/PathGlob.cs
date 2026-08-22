using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Review;

/// <summary>Matches repository-relative paths against the glob patterns a policy declares.</summary>
/// <remarks>
/// Deliberately small and total. The patterns it evaluates decide whether a change escalates
/// to a human, so the matcher must never throw on an operator's typo and never depend on the
/// host filesystem's case rules — a policy that protects <c>Auth/</c> on one machine and not
/// another is not a policy. Matching is ordinal and case-insensitive, and separators are
/// normalized so a pattern written either way behaves the same.
/// </remarks>
public static class PathGlob
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Whether <paramref name="path"/> matches <paramref name="pattern"/>.</summary>
    public static bool IsMatch(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            return Regex.IsMatch(
                Normalize(path),
                Translate(pattern),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pattern pathological enough to time out is treated as matching. This rule
            // only ever escalates to a human, so failing towards escalation is the safe
            // direction: the cost is one unnecessary review, not an unreviewed change.
            return true;
        }
    }

    /// <summary>The first pattern that matches, or <see langword="null"/>.</summary>
    public static string? FirstMatch(IEnumerable<string> patterns, string path)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return patterns.FirstOrDefault(pattern => IsMatch(pattern, path));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string Translate(string pattern)
    {
        string normalized = Normalize(pattern);
        StringBuilder expression = new(normalized.Length * 2);
        expression.Append('^');

        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (c == '*')
            {
                bool doubled = i + 1 < normalized.Length && normalized[i + 1] == '*';
                if (doubled)
                {
                    // "**/" spans whole segments including none at all, so that "**/auth/**"
                    // matches "auth/x" as well as "src/auth/x". Written as "(?:.*/)?" rather
                    // than ".*/" precisely so the zero-segment case is covered.
                    if (i + 2 < normalized.Length && normalized[i + 2] == '/')
                    {
                        expression.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        expression.Append(".*");
                        i++;
                    }
                }
                else
                {
                    expression.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                expression.Append("[^/]");
            }
            else
            {
                expression.Append(Regex.Escape(c.ToString()));
            }
        }

        expression.Append('$');
        return expression.ToString();
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Domain.Hooks;

/// <summary>Matches repository-relative paths against globs: <c>**</c> crosses folders, <c>*</c> and <c>?</c> do not.</summary>
public static class GlobMatcher
{
    /// <summary>Tests a path against a glob.</summary>
    /// <param name="pattern">The glob, using <c>/</c> separators. A leading <c>**/</c> also matches the root folder.</param>
    /// <param name="path">The repository-relative path; backslashes are treated as <c>/</c>.</param>
    /// <returns>True when the whole path matches.</returns>
    public static bool IsMatch(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        return ToRegex(pattern).IsMatch(path.Replace('\\', '/'));
    }

    /// <summary>Tests a path against several globs.</summary>
    /// <param name="patterns">The globs.</param>
    /// <param name="path">The repository-relative path.</param>
    /// <returns>True when any glob matches.</returns>
    public static bool IsMatchAny(IEnumerable<string> patterns, string path)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return patterns.Any(p => IsMatch(p, path));
    }

    private static Regex ToRegex(string pattern)
    {
        StringBuilder builder = new("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                bool slashAfter = i + 2 < pattern.Length && pattern[i + 2] == '/';
                _ = builder.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*')
            {
                _ = builder.Append("[^/]*");
            }
            else if (c == '?')
            {
                _ = builder.Append("[^/]");
            }
            else
            {
                _ = builder.Append(Regex.Escape(c.ToString()));
            }
        }

        return new Regex(builder.Append('$').ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}

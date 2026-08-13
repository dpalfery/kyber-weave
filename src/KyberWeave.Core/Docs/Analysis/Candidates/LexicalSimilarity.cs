using System.Text.RegularExpressions;

namespace KyberWeave.Core.Docs.Analysis.Candidates;

internal static partial class LexicalSimilarity
{
    private static readonly IReadOnlyDictionary<string, string> Equivalents =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["every"] = "all",
            ["consumption"] = "usage"
        };

    internal static IReadOnlySet<string> Tokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in TokenPattern().Matches(text.ToLowerInvariant()))
        {
            var token = Stem(match.Value);
            if (Equivalents.TryGetValue(token, out var equivalent)) token = equivalent;
            if (token.Length > 0) tokens.Add(token);
        }

        return tokens;
    }

    internal static double Score(string left, string right) =>
        Score(Tokens(left), Tokens(right));

    internal static double Score(IReadOnlySet<string> leftTokens, IReadOnlySet<string> rightTokens)
    {
        if (leftTokens.Count == 0 || rightTokens.Count == 0) return 0;

        var overlap = leftTokens.Count(token => rightTokens.Contains(token));
        return (double)overlap / Math.Min(leftTokens.Count, rightTokens.Count);
    }

    private static string Stem(string token)
    {
        if (token.Length > 5 && token.EndsWith("ies", StringComparison.Ordinal))
            return token[..^3] + "y";
        if (token.Length > 4 && token.EndsWith("ing", StringComparison.Ordinal))
            return token[..^3];
        if (token.Length > 4 && token.EndsWith("ed", StringComparison.Ordinal))
            return token[..^2];
        if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
            return token[..^1];
        return token;
    }

    [GeneratedRegex("[\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

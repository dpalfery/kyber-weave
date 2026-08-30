using System.Text.RegularExpressions;

namespace KyberWeave.Core.Text;

/// <summary>
/// A small, dependency-free lexical vectorizer. Tokenizes to lowercase word stems,
/// drops stop words, and computes cosine similarity over term-frequency vectors.
/// Deterministic and offline by design — used for description-overlap detection and
/// the default routing strategy so the toolkit needs no API key in CI.
/// </summary>
public static partial class TextVectorizer
{
    private static readonly Regex Tokenizer = MyRegex();

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the","a","an","and","or","but","to","of","in","on","for","with","is","are","be",
        "this","that","it","as","at","by","from","your","you","when","use","used","using",
        "do","not","if","into","about","which","what","how","can","will","should","its"
    };

    public static Dictionary<string, double> Vectorize(string text)
    {
        Dictionary<string, double> counts = new Dictionary<string, double>();
        foreach (string token in Tokenize(text))
        {
            counts[token] = counts.TryGetValue(token, out double c) ? c + 1 : 1;
        }
        return counts;
    }

    /// <summary>
    /// The content tokens of a text, in order. Kept separate from
    /// <see cref="Vectorize"/> because adjacency carries information a bag of words
    /// discards: "Web UI" and "WebUI" are the same subject, and only a caller that can
    /// see the two tokens were neighbours can recover that. <see cref="VectorizeFused"/>
    /// is the public consumer.
    /// </summary>
#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
    private static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<string> tokens = new List<string>();
        foreach (Match m in Tokenizer.Matches(text.ToLowerInvariant()))
#pragma warning restore CA1308
        {
            string token = Normalize(m.Value);
            if (token.Length < 2 || StopWords.Contains(token)) continue;
            tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>
    /// A term-frequency vector that also carries each adjacent token pair fused into one
    /// term, so text writing "Web UI" is reachable from a query writing "WebUI".
    /// </summary>
    /// <remarks>
    /// Fused pairs are added at half weight. They are a bridge for compound names, not
    /// evidence in their own right, and counting them fully would let an incidental
    /// adjacency outweigh a real term match.
    /// </remarks>
    public static Dictionary<string, double> VectorizeFused(string text)
    {
        IReadOnlyList<string> tokens = Tokenize(text);
        Dictionary<string, double> counts = new Dictionary<string, double>();

        for (int i = 0; i < tokens.Count; i++)
        {
            counts[tokens[i]] = counts.TryGetValue(tokens[i], out double c) ? c + 1 : 1;

            if (i + 1 >= tokens.Count) continue;

            string fused = tokens[i] + tokens[i + 1];
            counts[fused] = counts.TryGetValue(fused, out double f) ? f + 0.5 : 0.5;
        }

        return counts;
    }

    /// <summary>Light normalization: strip a trailing plural 's' on longer tokens so
    /// 'errors' and 'error' match. Deliberately conservative — not a full stemmer.</summary>
    private static string Normalize(string token)
    {
        if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal) && !token.EndsWith("us", StringComparison.Ordinal))
            return token[..^1];
        return token;
    }

    public static double CosineSimilarity(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        double dot = 0;
        foreach ((string? k, double v) in a)
            if (b.TryGetValue(k, out double bv)) dot += v * bv;

        double magA = Math.Sqrt(a.Values.Sum(v => v * v));
        double magB = Math.Sqrt(b.Values.Sum(v => v * v));
        return magA == 0 || magB == 0 ? 0 : dot / (magA * magB);
    }

    public static double Similarity(string left, string right) =>
        CosineSimilarity(Vectorize(left), Vectorize(right));
    [GeneratedRegex("[a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Review;

/// <summary>One decision-model answer as the cycle's answer file keeps it.</summary>
/// <remarks>The property names are the file's format: <c>Answer</c>, <c>Score</c>, <c>Threshold</c>.</remarks>
public sealed record ReviewCachedAnswer(string Answer, double? Score, double? Threshold);

/// <summary>The key an answer is kept under: rule, subject and a digest of the text asked about.</summary>
/// <remarks>A changed finding text changes the digest, so a stale answer is never reused.</remarks>
public static class ReviewDecisionCacheKey
{
    /// <summary>The key for one rule's question.</summary>
    public static string Of(ArbiterRule rule, ReviewQuestion question)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(question);
        StringBuilder text = new();
        foreach ((string name, object? value) in question.Facts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            text.Append(name).Append('=').Append(value is null ? string.Empty : JsonSerializer.Serialize(value)).Append('\n');
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
        return $"{rule.Id}|{question.Subject}|{digest}";
    }
}

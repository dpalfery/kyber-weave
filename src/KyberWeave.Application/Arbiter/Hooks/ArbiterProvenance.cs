using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Arbiter.Mcp;

/// <summary>
/// Formats the provenance line every <c>arbiter_*</c> response leads with: the absolute
/// root, the rule-set hash and the rule count, so a caller can tell which configuration
/// answered, as the docs tools' provenance does for their corpus.
/// </summary>
/// <remarks>
/// The hash covers the effective rules as serialised, so an edited override changes it
/// even when the count does not. It is a fingerprint for comparison, not a signature.
/// </remarks>
public static class ArbiterProvenance
{
    /// <summary>The line for <paramref name="root"/> answered from <paramref name="ruleSetHash"/> over <paramref name="ruleCount"/> rules.</summary>
    public static string Line(string root, string ruleSetHash, int ruleCount) =>
        "provenance: root=" + Path.GetFullPath(root)
        + " rules=" + ruleSetHash
        + " rule-count=" + ruleCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>The provenance value used when no configuration could be loaded.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Twelve hex characters of the SHA-256 over the serialised rules, in configured order.</summary>
    public static string RuleSetHash(IReadOnlyList<ArbiterRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rules));
        return Convert.ToHexString(digest, 0, 6);
    }
}

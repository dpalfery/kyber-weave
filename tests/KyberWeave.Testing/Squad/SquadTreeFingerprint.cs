using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// A stable fingerprint of a whole tree, so a crash-safety run can tell two crash states apart.
/// The transaction id is normalised because every run mints a fresh one: two runs stopped at the
/// same logical point differ only in that id, and counting them as two states would overstate what
/// the run proves.
/// </summary>
public static class SquadTreeFingerprint
{
    private static readonly Regex TransactionId = new(
        "\"transactionId\"\\s*:\\s*\"[0-9a-fA-F.\\-]{32,64}\"",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Fingerprints every path, kind, link target and byte of a tree.</summary>
    /// <param name="tree">The tree, keyed as the fixture keys it.</param>
    /// <returns>A SHA-256 digest in lower-case hexadecimal.</returns>
    public static string Of(IReadOnlyDictionary<string, SquadTreeEntry> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        StringBuilder text = new();
        foreach ((string path, SquadTreeEntry entry) in tree.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(path).Append('\0')
                .Append((int)entry.Kind).Append('\0')
                .Append(entry.LinkTarget ?? string.Empty).Append('\0')
                .Append(entry.Content is { } content ? Normalize(content) : "-")
                .Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Normalize(ReadOnlyMemory<byte> content) =>
        TransactionId.Replace(Encoding.UTF8.GetString(content.Span), "\"transactionId\":\"<id>\"");
}

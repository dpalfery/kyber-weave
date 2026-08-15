namespace KyberWeave.Cli.Update;

/// <summary>
/// Looks up an archive's SHA-256 by exact filename in a <c>sha256sum</c> listing.
/// </summary>
/// <remarks>
/// A regex over the name would let <c>.</c> match anything and let one asset's line
/// satisfy a different asset. The install script matches the same way.
/// </remarks>
internal static class ChecksumVerifier
{
    internal static string ExpectedHex(string sha256SumsText, string archiveName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256SumsText);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveName);

        using var reader = new StringReader(sha256SumsText);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;

            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                continue;

            var hex = parts[0];
            if (hex.Length != 64 || !IsHex(hex))
                continue;

            var name = parts[1];
            if (name.StartsWith('*'))
                name = name[1..];

            var slash = name.LastIndexOfAny(['/', '\\']);
            if (slash >= 0)
                name = name[(slash + 1)..];

            if (string.Equals(name, archiveName, StringComparison.Ordinal))
                return hex.ToLowerInvariant();
        }

        throw new SelfUpdateException(
            $"SHA256SUMS.txt has no entry for {archiveName}; refusing to install an unverified asset");
    }

    internal static void Verify(string expectedHex, ReadOnlySpan<byte> actualHash, string archiveName)
    {
        var actual = Convert.ToHexString(actualHash).ToLowerInvariant();
        if (!string.Equals(actual, expectedHex, StringComparison.Ordinal))
        {
            throw new SelfUpdateException(
                $"SHA256 mismatch for {archiveName}: expected {expectedHex}, got {actual}");
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            var ok = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!ok)
                return false;
        }

        return true;
    }
}

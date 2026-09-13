using System.Globalization;

namespace KyberWeave.Cli.Update;

/// <summary>Normalizes GitHub tags and assembly informational versions for comparison.</summary>
internal static class ReleaseVersion
{
    internal static string Normalize(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        string trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        int plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            trimmed = trimmed[..plus];

        if (trimmed.Length == 0)
            throw new SelfUpdateException("version is empty after removing a leading 'v'.");

        if (trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal)
            || trimmed.Contains("..", StringComparison.Ordinal)
            || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            throw new SelfUpdateException($"refusing version '{version}': it is not a Release tag.");
        }

        return trimmed;
    }

    internal static string Tag(string normalizedVersion) => "v" + normalizedVersion;

    /// <summary>
    /// Compares two versions by SemVer 2.0.0 precedence: negative when <paramref name="left"/>
    /// sorts below <paramref name="right"/>, zero when they are equal, positive when above.
    /// </summary>
    /// <remarks>
    /// Ordinal string comparison is not a substitute — it sorts <c>rc.10</c> below <c>rc.9</c>.
    /// <c>kyber_weave_semver_compare</c> in <c>scripts/install.sh</c> carries the same rules for
    /// the first-install path; the two gates decide which releases publish a given asset, so
    /// they have to agree.
    /// </remarks>
    internal static int Compare(string left, string right)
    {
        (string leftCore, string leftPre) = SplitPreRelease(Normalize(left));
        (string rightCore, string rightPre) = SplitPreRelease(Normalize(right));

        int core = CompareCore(leftCore, rightCore);
        return core != 0 ? core : ComparePreRelease(leftPre, rightPre);
    }

    private static (string Core, string PreRelease) SplitPreRelease(string version)
    {
        int dash = version.IndexOf('-', StringComparison.Ordinal);
        return dash < 0
            ? (version, string.Empty)
            : (version[..dash], version[(dash + 1)..]);
    }

    private static int CompareCore(string left, string right)
    {
        string[] leftFields = left.Split('.');
        string[] rightFields = right.Split('.');

        // A missing field reads as zero, so 1.2 and 1.2.0 are the same release.
        for (int i = 0; i < Math.Max(leftFields.Length, rightFields.Length); i++)
        {
            long x = i < leftFields.Length ? ParseNumericField(leftFields[i]) : 0;
            long y = i < rightFields.Length ? ParseNumericField(rightFields[i]) : 0;
            if (x != y)
                return x < y ? -1 : 1;
        }

        return 0;
    }

    private static int ComparePreRelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
            return 0;

        // A pre-release ranks below the release it precedes: 0.1.7-rc.9 < 0.1.7.
        if (left.Length == 0)
            return 1;
        if (right.Length == 0)
            return -1;

        string[] leftFields = left.Split('.');
        string[] rightFields = right.Split('.');

        for (int i = 0; i < Math.Min(leftFields.Length, rightFields.Length); i++)
        {
            bool leftNumeric = TryParseNumericField(leftFields[i], out long x);
            bool rightNumeric = TryParseNumericField(rightFields[i], out long y);

            if (leftNumeric && rightNumeric)
            {
                if (x != y)
                    return x < y ? -1 : 1;
            }
            else if (leftNumeric)
            {
                // A numeric identifier always ranks below an alphanumeric one.
                return -1;
            }
            else if (rightNumeric)
            {
                return 1;
            }
            else
            {
                int ordinal = string.CompareOrdinal(leftFields[i], rightFields[i]);
                if (ordinal != 0)
                    return ordinal < 0 ? -1 : 1;
            }
        }

        // Every shared field matched, so the longer identifier list wins.
        if (leftFields.Length == rightFields.Length)
            return 0;

        return leftFields.Length < rightFields.Length ? -1 : 1;
    }

    private static bool TryParseNumericField(string field, out long value) =>
        long.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static long ParseNumericField(string field) =>
        TryParseNumericField(field, out long value) ? value : 0;
}

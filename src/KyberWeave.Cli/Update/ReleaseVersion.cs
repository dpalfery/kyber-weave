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
}

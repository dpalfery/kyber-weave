namespace KyberWeave.Core.Arbiter.ModelTargets;

/// <summary>
/// The alias rule a harness model override is written by. The resolver asks it whether a token would be
/// delivered, and the adapter that writes the model asks it which alias to write, so both answer the same way.
/// </summary>
public static class ModelTokenAliases
{
    private const string Claude = "claude";

    /// <summary>
    /// The alias a model token writes as, in the accepted set's own spelling. The token is trimmed and matched
    /// to an accepted alias ignoring case. A full model identifier (<c>claude-&lt;family&gt;-&lt;version&gt;</c>)
    /// writes its family, when the family is accepted and the version is not blank. Null when neither holds.
    /// </summary>
    /// <param name="token">The model token from the targets table.</param>
    /// <param name="accepted">The aliases the harness accepts.</param>
    public static string? AliasFor(string token, IReadOnlySet<string> accepted)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(accepted);

        string trimmed = token.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (AcceptedSpelling(trimmed, accepted) is { } alias)
        {
            return alias;
        }

        // The prefix is matched ignoring case, as the family and version are, so CLAUDE-sonnet-1 is a full model id too.
        string prefix = Claude + "-";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = trimmed[prefix.Length..];
        int dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || string.IsNullOrWhiteSpace(rest[(dash + 1)..]))
        {
            return null;
        }

        return AcceptedSpelling(rest[..dash], accepted);
    }

    /// <summary>The accepted set's spelling of <paramref name="text"/>, matched ignoring case; null when none matches.</summary>
    private static string? AcceptedSpelling(string text, IReadOnlySet<string> accepted)
    {
        if (accepted.Contains(text))
        {
            return text;
        }

        foreach (string candidate in accepted)
        {
            if (string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}

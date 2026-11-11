namespace KyberWeave.Core.Arbiter.ModelTargets;

/// <summary>
/// The alias rule a harness model override is written by. The resolver asks it whether a token would be
/// delivered, and the adapter that writes the model asks it which alias to write, so both answer the same way.
/// </summary>
public static class ModelTokenAliases
{
    private const string Claude = "claude";

    /// <summary>
    /// The alias a model token writes as: an accepted alias writes as it is, and a full model identifier
    /// (<c>claude-&lt;family&gt;-...</c>) whose family is accepted writes that family. Null when neither holds.
    /// </summary>
    /// <param name="token">The model token from the targets table.</param>
    /// <param name="accepted">The aliases the harness accepts.</param>
    public static string? AliasFor(string token, IReadOnlySet<string> accepted)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(accepted);

        if (accepted.Contains(token))
        {
            return token;
        }

        string[] parts = token.Split('-');
        return parts.Length > 1
            && string.Equals(parts[0], Claude, StringComparison.Ordinal)
            && accepted.Contains(parts[1])
                ? parts[1]
                : null;
    }
}

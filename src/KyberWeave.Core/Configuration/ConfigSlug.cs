namespace KyberWeave.Core.Configuration;

/// <summary>
/// The identifier shape shared by the values a host declares and something else then builds
/// a path or a lookup name out of: technologies, and configuration registry properties.
/// </summary>
/// <remarks>
/// Constrained where the operator wrote it rather than where it is consumed. A technology
/// becomes a directory name; a registry property becomes the name a portable skill asks for
/// by hand. Both are read and typed by people, so both are lowercase, hyphen-separated, and
/// free of the separators that would let one of them mean a path.
/// </remarks>
internal static class ConfigSlug
{
    /// <summary>Lowercase alphanumerics separated by single interior hyphens.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c is >= 'a' and <= 'z' || c is >= '0' and <= '9')
                continue;

            // A leading, trailing or doubled hyphen produces a name no author would write by
            // hand and no lookup would guess.
            if (c != '-' || i == 0 || i == value.Length - 1 || value[i - 1] == '-')
                return false;
        }

        return true;
    }
}

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// Rejects operator-supplied values that a generated document cannot carry, before anything is
/// written.
/// </summary>
/// <remarks>
/// YAML punctuation is quoted at the emission site. The checks here are the two things quoting
/// cannot fix: a control character changes the generated file's line structure, and a pipe adds
/// a column to the catalog's table row.
/// </remarks>
public static class ScaffoldValues
{
    /// <summary>Rejects values that cannot remain on one generated line.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="parameterName">The name the caller knows the value by, used in the failure.</param>
    /// <returns>The value, so a caller can compose the check with construction.</returns>
    /// <exception cref="ArgumentException">The value contains a control character.</exception>
    public static string RequireEmittableValue(string value, string parameterName)
    {
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"'{parameterName}' may not contain control characters. " +
                "Generated values must remain on one line.",
                parameterName);
        }

        return value;
    }

    /// <summary>Rejects values that would add a column to the pipe-delimited catalog row.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="parameterName">The name the caller knows the value by, used in the failure.</param>
    /// <exception cref="ArgumentException">The value contains a control character or a pipe.</exception>
    public static void RequireCatalogValue(string value, string parameterName)
    {
        RequireEmittableValue(value, parameterName);
        if (value.Contains('|', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{parameterName}' may not contain '|'. " +
                "The value is written into a pipe-delimited catalog row.",
                parameterName);
        }
    }
}

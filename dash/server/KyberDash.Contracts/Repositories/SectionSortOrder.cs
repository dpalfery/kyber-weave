namespace KyberDash.Contracts.Repositories;

/// <summary>
/// The one definition of a session section's sort order. Every layer that accepts, forwards or
/// translates an <c>order</c> value reads it here, so widening the accepted set happens in one place.
/// </summary>
public static class SectionSortOrder
{
    /// <summary>The value an absent or unspecified order takes.</summary>
    public const string Default = "desc";

    /// <summary>The two accepted values, newest first then oldest first.</summary>
    public const string Descending = "desc";

    /// <summary>Oldest first.</summary>
    public const string Ascending = "asc";

    /// <summary>
    /// Parses an order value, trimming it and comparing case-insensitively against
    /// <see cref="Descending"/> and <see cref="Ascending"/>.
    /// </summary>
    /// <param name="order">The raw value.</param>
    /// <param name="normalized">The accepted value when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the value is accepted.</returns>
    public static bool TryNormalize(string? order, out string normalized)
    {
        normalized = Default;
        if (string.IsNullOrWhiteSpace(order))
        {
            return false;
        }

        string trimmed = order.Trim();
        if (string.Equals(trimmed, Descending, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Descending;
            return true;
        }

        if (string.Equals(trimmed, Ascending, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Ascending;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses an order value, or throws <see cref="ArgumentException"/> for one that is not accepted.
    /// </summary>
    /// <param name="order">The raw value.</param>
    /// <returns>The accepted value.</returns>
    public static string Normalize(string order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(order);
        if (!TryNormalize(order, out string normalized))
        {
            throw new ArgumentException($"Sort order '{order}' must be '{Descending}' or '{Ascending}'.", nameof(order));
        }

        return normalized;
    }

    /// <summary>
    /// Reads an accepted order value as the SQL direction it selects — <c>"DESC"</c> or <c>"ASC"</c>.
    /// </summary>
    /// <param name="order">The raw value.</param>
    /// <returns>The SQL direction label.</returns>
    public static string ToSqlDirection(string order) =>
        string.Equals(Normalize(order), Ascending, StringComparison.Ordinal) ? "ASC" : "DESC";
}

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>Parses, spells and evaluates a <see cref="BriefCondition"/>.</summary>
public static class BriefConditionExtensions
{
    /// <summary>The condition as the map spells it.</summary>
    /// <param name="condition">The condition.</param>
    /// <returns><c>always</c> or <c>fact=a|b; fact=c</c>.</returns>
    public static string Spell(this BriefCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition.Clauses.Count == 0
            ? "always"
            : string.Join("; ", condition.Clauses.Select(clause => $"{clause.Fact}={string.Join('|', clause.Values)}"));
    }

    /// <summary>Parses <c>always</c> or <c>fact=a|b; fact=c</c>.</summary>
    /// <param name="text">The condition text.</param>
    /// <param name="where">Where the text came from, for the error message.</param>
    /// <returns>The condition.</returns>
    /// <exception cref="BriefMapException">The text names a fact or value the vocabulary does not have.</exception>
    public static BriefCondition ParseCondition(string text, string where)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, "always", StringComparison.Ordinal))
        {
            return BriefCondition.Always;
        }

        List<BriefClause> clauses = [];
        foreach (string clause in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = clause.IndexOf('=', StringComparison.Ordinal);
            string fact = equals < 0 ? clause : clause[..equals].Trim();
            if (equals < 0 || !BriefFacts.Vocabulary.TryGetValue(fact, out IReadOnlyList<string>? known))
            {
                throw new BriefMapException(
                    $"{where}: '{clause}' is not 'fact=value'. Facts: {string.Join(", ", BriefFacts.Names)}.");
            }

            string[] values = clause[(equals + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? unknown = values.FirstOrDefault(value => !known.Contains(value, StringComparer.Ordinal));
            if (values.Length == 0 || unknown is not null)
            {
                throw new BriefMapException(
                    $"{where}: '{unknown ?? clause}' is not a value of '{fact}'. Values: {string.Join(", ", known)}.");
            }

            clauses.Add(new BriefClause(fact, values));
        }

        return new BriefCondition(clauses);
    }

    /// <summary>True when every clause holds for <paramref name="facts"/>.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="facts">The run's facts; an unknown fact holds.</param>
    /// <returns>Whether the condition holds.</returns>
    public static bool Holds(this BriefCondition condition, BriefFacts facts)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(facts);
        foreach (BriefClause clause in condition.Clauses)
        {
            string? actual = facts.Get(clause.Fact);
            if (actual is not null && !clause.Values.Contains(actual, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

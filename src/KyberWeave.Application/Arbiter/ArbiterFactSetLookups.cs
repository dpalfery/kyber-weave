using System.Diagnostics.CodeAnalysis;

namespace KyberWeave.Core.Arbiter;

/// <summary>Lookups and edits over an <see cref="ArbiterFactSet"/>.</summary>
public static class ArbiterFactSetLookups
{
    /// <summary>Whether the set contains <paramref name="name"/>.</summary>
    public static bool Contains(this ArbiterFactSet set, string name)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(name);
        return set.Facts.ContainsKey(name);
    }

    /// <summary>
    /// Whether the set carries a value for <paramref name="name"/>: present and not null.
    /// </summary>
    /// <remarks>
    /// This is what "the fact is present" means to a rule. <see cref="Contains"/> answers
    /// whether a name was ever stored, and the builder stores every declared name for a
    /// trigger whether or not the dispatch supplied it -- so <c>Contains</c> alone made
    /// every declared fact look present and left <c>exists: false</c> unable to match
    /// anything. A name with no value is the absence a rule is written against.
    /// </remarks>
    public static bool IsPresent(this ArbiterFactSet set, string name)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(name);
        return set.Facts.TryGetValue(name, out ArbiterFact? fact) && fact.Value is not null;
    }

    /// <summary>Tries to get the fact named <paramref name="name"/>.</summary>
    public static bool TryGet(this ArbiterFactSet set, string name, [NotNullWhen(true)] out ArbiterFact? fact)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(name);
        return set.Facts.TryGetValue(name, out fact);
    }

    /// <summary>Returns a new set with <paramref name="name"/> set to the given value.</summary>
    public static ArbiterFactSet With(this ArbiterFactSet set, string name, object? value, ArbiterFactLabel label)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(name);
        Dictionary<string, ArbiterFact> next = new(set.Facts, StringComparer.Ordinal)
        {
            [name] = new ArbiterFact(value, label),
        };
        return new ArbiterFactSet(next);
    }
}

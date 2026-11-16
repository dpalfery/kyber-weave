using System.Diagnostics.CodeAnalysis;

namespace KyberWeave.Core.Arbiter;

/// <summary>Where a fact came from: computed by the harness or stated by the caller.</summary>
public enum ArbiterFactLabel
{
    /// <summary>Computed by the harness (paths, digests, plan projections).</summary>
    Derived,

    /// <summary>Stated by the caller (prompts, task text, lens names).</summary>
    Asserted,
}

/// <summary>A single named fact value with its provenance label.</summary>
/// <param name="Value">The fact value: scalar, string, or list of values.</param>
/// <param name="Label">Whether the value was derived or asserted.</param>
public sealed record ArbiterFact(object? Value, ArbiterFactLabel Label);

/// <summary>
/// The harness-neutral fact bag step 0 evaluates against. Names use dotted
/// paths (<c>plan.task.files</c>); a name with no value is absent, satisfies
/// <c>exists: false</c> and fails every other operator.
/// </summary>
public sealed class ArbiterFactSet
{
    private readonly Dictionary<string, ArbiterFact> _facts;

    /// <summary>Creates an empty fact set.</summary>
    public ArbiterFactSet()
    {
        _facts = new Dictionary<string, ArbiterFact>(StringComparer.Ordinal);
    }

    /// <summary>Creates a fact set from existing entries.</summary>
    public ArbiterFactSet(IDictionary<string, ArbiterFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        _facts = new Dictionary<string, ArbiterFact>(facts, StringComparer.Ordinal);
    }

    /// <summary>AllFacts in the set, keyed by dotted fact name.</summary>
    public IReadOnlyDictionary<string, ArbiterFact> Facts => _facts;

    /// <summary>Whether the set contains <paramref name="name"/>.</summary>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _facts.ContainsKey(name);
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
    public bool IsPresent(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _facts.TryGetValue(name, out ArbiterFact? fact) && fact.Value is not null;
    }

    /// <summary>Tries to get the fact named <paramref name="name"/>.</summary>
    public bool TryGet(string name, [NotNullWhen(true)] out ArbiterFact? fact)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _facts.TryGetValue(name, out fact);
    }

    /// <summary>Returns a new set with <paramref name="name"/> set to the given value.</summary>
    public ArbiterFactSet With(string name, object? value, ArbiterFactLabel label)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArbiterFactSet next = new(_facts);
        next._facts[name] = new ArbiterFact(value, label);
        return next;
    }
}

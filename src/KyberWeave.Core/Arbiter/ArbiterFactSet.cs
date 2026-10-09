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
/// paths (<c>plan.task.files</c>); an absent name satisfies
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

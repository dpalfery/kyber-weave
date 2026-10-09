using KyberWeave.Core.Review;

namespace KyberWeave.Core.Arbiter.Rules;

/// <summary>The closed step-0 operator set. No expression language exists beyond these.</summary>
public enum RuleOperator
{
    /// <summary>Every child holds.</summary>
    All,

    /// <summary>At least one child holds.</summary>
    Any,

    /// <summary>The single child does not hold.</summary>
    Not,

    /// <summary>Presence check against the expected boolean.</summary>
    Exists,

    /// <summary>Deep equality against the operand.</summary>
    Equals,

    /// <summary>Scalar membership in the operand list.</summary>
    In,

    /// <summary>Any path matches any operand glob via <c>PathGlob</c>.</summary>
    Matches,

    /// <summary>Every fact path is covered by the operand list via <c>PathGlob</c>.</summary>
    SubsetOf,

    /// <summary>At least one fact path is covered by the operand list via <c>PathGlob</c>.</summary>
    Intersects,

    /// <summary>Element count equals the operand.</summary>
    Count,
}

/// <summary>
/// An operator operand: either a literal or another fact's dotted name.
/// A string operand that names a fact must use <see cref="Fact"/> so a
/// literal string is never mistaken for a reference.
/// </summary>
public sealed record RuleOperand
{
    private RuleOperand(object? literalValue, string? factName, bool isFactRef)
    {
        LiteralValue = literalValue;
        FactName = factName;
        IsFactRef = isFactRef;
    }

    /// <summary>The literal value, when <see cref="IsFactRef"/> is false.</summary>
    public object? LiteralValue { get; }

    /// <summary>The referenced fact name, when <see cref="IsFactRef"/> is true.</summary>
    public string? FactName { get; }

    /// <summary>Whether the operand is a reference to another fact.</summary>
    public bool IsFactRef { get; }

    /// <summary>Creates a literal operand.</summary>
    public static RuleOperand Literal(object? value) => new(value, null, false);

    /// <summary>Creates a fact-reference operand.</summary>
    public static RuleOperand Fact(string factName)
    {
        ArgumentNullException.ThrowIfNull(factName);
        return new RuleOperand(null, factName, true);
    }
}

/// <summary>
/// One step-0 <c>when</c> clause. Leaf operators name exactly one fact and one
/// operator; <see cref="RuleOperator.All"/>, <see cref="RuleOperator.Any"/>
/// and <see cref="RuleOperator.Not"/> combine child predicates.
/// </summary>
/// <remarks>
/// Path-sensitive operators (<c>matches</c>, <c>subset-of</c>, <c>intersects</c>)
/// cover list items through <see cref="PathGlob.IsMatch"/> so a task file entry
/// such as <c>src/auth/**</c> covers <c>src/auth/a.cs</c>. An absent fact
/// satisfies <c>exists: false</c> and fails every other operator.
/// </remarks>
public sealed record RulePredicate
{
    private RulePredicate(
        string? fact,
        RuleOperator @operator,
        RuleOperand? operand,
        IReadOnlyList<RulePredicate> children)
    {
        Fact = fact;
        Operator = @operator;
        Operand = operand;
        Children = children;
    }

    /// <summary>The dotted fact name, or null for <c>all</c>/<c>any</c> combinators.</summary>
    public string? Fact { get; }

    /// <summary>The single operator this predicate applies.</summary>
    public RuleOperator Operator { get; }

    /// <summary>The operand, for leaf operators.</summary>
    public RuleOperand? Operand { get; }

    /// <summary>Child predicates, for <c>all</c>, <c>any</c> and <c>not</c>.</summary>
    public IReadOnlyList<RulePredicate> Children { get; }

    /// <summary>True when every child holds (vacuously true when empty).</summary>
    public static RulePredicate All(params RulePredicate[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        return new RulePredicate(null, RuleOperator.All, null, Array.AsReadOnly((RulePredicate[])children.Clone()));
    }

    /// <summary>True when at least one child holds.</summary>
    public static RulePredicate Any(params RulePredicate[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        return new RulePredicate(null, RuleOperator.Any, null, Array.AsReadOnly((RulePredicate[])children.Clone()));
    }

    /// <summary>True when the child does not hold.</summary>
    public static RulePredicate Not(RulePredicate child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return new RulePredicate(null, RuleOperator.Not, null, [child]);
    }

    /// <summary>Presence check: absent facts satisfy <c>false</c> only.</summary>
    public static RulePredicate Exists(string fact, bool expected)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return new RulePredicate(fact, RuleOperator.Exists, RuleOperand.Literal(expected), []);
    }

    /// <summary>Deep equality against a literal or another fact.</summary>
    public static RulePredicate Equals(string fact, RuleOperand operand)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(operand);
        return new RulePredicate(fact, RuleOperator.Equals, operand, []);
    }

    /// <summary>Scalar membership against a literal list or another fact.</summary>
    public static RulePredicate In(string fact, RuleOperand operand)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(operand);
        return new RulePredicate(fact, RuleOperator.In, operand, []);
    }

    /// <summary>Path match against a glob, glob list, or another fact.</summary>
    public static RulePredicate Matches(string fact, RuleOperand operand)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(operand);
        return new RulePredicate(fact, RuleOperator.Matches, operand, []);
    }

    /// <summary>Subset cover against a list, glob list, or another fact.</summary>
    public static RulePredicate SubsetOf(string fact, RuleOperand operand)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(operand);
        return new RulePredicate(fact, RuleOperator.SubsetOf, operand, []);
    }

    /// <summary>Overlap cover against a list, glob list, or another fact.</summary>
    public static RulePredicate Intersects(string fact, RuleOperand operand)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(operand);
        return new RulePredicate(fact, RuleOperator.Intersects, operand, []);
    }

    /// <summary>Element count against a literal or a fact resolving to an integer.</summary>
    public static RulePredicate Count(string fact, int expected)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return new RulePredicate(fact, RuleOperator.Count, RuleOperand.Literal(expected), []);
    }

    /// <summary>Element count against a literal or a fact reference.</summary>
    public static RulePredicate Count(string fact, RuleOperand operand)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(operand);
        return new RulePredicate(fact, RuleOperator.Count, operand, []);
    }

    /// <summary>Evaluates the predicate against <paramref name="facts"/>.</summary>
    public bool Evaluate(ArbiterFactSet facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return Operator switch
        {
            RuleOperator.All => Children.All(child => child.Evaluate(facts)),
            RuleOperator.Any => Children.Any(child => child.Evaluate(facts)),
            RuleOperator.Not => Children.Count == 1 && !Children[0].Evaluate(facts),
            RuleOperator.Exists => EvaluateExists(facts),
            RuleOperator.Equals => EvaluateEquals(facts),
            RuleOperator.In => EvaluateIn(facts),
            RuleOperator.Matches => EvaluateMatches(facts),
            RuleOperator.SubsetOf => EvaluateSubsetOf(facts),
            RuleOperator.Intersects => EvaluateIntersects(facts),
            RuleOperator.Count => EvaluateCount(facts),
            _ => false,
        };
    }

    private bool EvaluateExists(ArbiterFactSet facts)
    {
        bool present = Fact is not null && facts.Contains(Fact);
        bool expected = Operand?.LiteralValue is bool b && b;
        return present == expected;
    }

    private bool EvaluateEquals(ArbiterFactSet facts)
    {
        if (Fact is null || !facts.TryGet(Fact, out ArbiterFact? fact))
        {
            return false;
        }

        if (!TryResolveOperand(facts, out object? other))
        {
            return false;
        }

        return ValuesEqual(fact.Value, other);
    }

    private bool EvaluateIn(ArbiterFactSet facts)
    {
        if (Fact is null || !facts.TryGet(Fact, out ArbiterFact? fact))
        {
            return false;
        }

        if (!TryResolveOperand(facts, out object? other))
        {
            return false;
        }

        foreach (object? candidate in ToList(other))
        {
            if (ValuesEqual(fact.Value, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private bool EvaluateMatches(ArbiterFactSet facts)
    {
        if (Fact is null || !facts.TryGet(Fact, out ArbiterFact? fact))
        {
            return false;
        }

        if (!TryResolveOperand(facts, out object? other))
        {
            return false;
        }

        IReadOnlyList<object?> paths = ToList(fact.Value);
        IReadOnlyList<object?> patterns = ToList(other);
        foreach (object? path in paths)
        {
            string? pathText = ToText(path);
            if (pathText is null)
            {
                continue;
            }

            foreach (object? pattern in patterns)
            {
                string? patternText = ToText(pattern);
                if (patternText is not null && PathGlob.IsMatch(patternText, pathText))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool EvaluateSubsetOf(ArbiterFactSet facts)
    {
        if (Fact is null || !facts.TryGet(Fact, out ArbiterFact? fact))
        {
            return false;
        }

        if (!TryResolveOperand(facts, out object? other))
        {
            return false;
        }

        IReadOnlyList<object?> paths = ToList(fact.Value);
        IReadOnlyList<object?> cover = ToList(other);
        foreach (object? path in paths)
        {
            if (!IsCovered(path, cover))
            {
                return false;
            }
        }

        return true;
    }

    private bool EvaluateIntersects(ArbiterFactSet facts)
    {
        if (Fact is null || !facts.TryGet(Fact, out ArbiterFact? fact))
        {
            return false;
        }

        if (!TryResolveOperand(facts, out object? other))
        {
            return false;
        }

        IReadOnlyList<object?> paths = ToList(fact.Value);
        IReadOnlyList<object?> cover = ToList(other);
        foreach (object? path in paths)
        {
            if (IsCovered(path, cover))
            {
                return true;
            }
        }

        return false;
    }

    private bool EvaluateCount(ArbiterFactSet facts)
    {
        if (Fact is null || !facts.TryGet(Fact, out ArbiterFact? fact))
        {
            return false;
        }

        if (!TryResolveOperand(facts, out object? other))
        {
            return false;
        }

        int actual = CountOf(fact.Value);
        if (other is int expected)
        {
            return actual == expected;
        }

        if (other is long expectedLong)
        {
            return actual == expectedLong;
        }

        // A fact reference may resolve to an integer or to a list whose
        // length is the expected count.
        if (other is System.Collections.IEnumerable enumerable and not string)
        {
            int count = 0;
            foreach (object? _ in enumerable)
            {
                count++;
            }

            return actual == count;
        }

        return false;
    }

    private bool TryResolveOperand(ArbiterFactSet facts, out object? value)
    {
        if (Operand is null)
        {
            value = null;
            return false;
        }

        if (!Operand.IsFactRef)
        {
            value = Operand.LiteralValue;
            return true;
        }

        if (Operand.FactName is not null && facts.TryGet(Operand.FactName, out ArbiterFact? fact))
        {
            value = fact.Value;
            return true;
        }

        value = null;
        return false;
    }

    private static bool IsCovered(object? path, IReadOnlyList<object?> cover)
    {
        string? pathText = ToText(path);
        if (pathText is null)
        {
            return false;
        }

        foreach (object? entry in cover)
        {
            string? coverText = ToText(entry);
            if (coverText is null)
            {
                continue;
            }

            if (string.Equals(pathText, coverText, StringComparison.Ordinal)
                || PathGlob.IsMatch(coverText, pathText))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is string leftText && right is string rightText)
        {
            return string.Equals(leftText, rightText, StringComparison.Ordinal);
        }

        if (left is System.Collections.IEnumerable leftItems and not string
            && right is System.Collections.IEnumerable rightItems and not string)
        {
            List<object?> leftList = [.. leftItems.Cast<object?>()];
            List<object?> rightList = [.. rightItems.Cast<object?>()];
            return leftList.Count == rightList.Count
                && leftList.Zip(rightList).All(pair => ValuesEqual(pair.First, pair.Second));
        }

        if (IsNumeric(left) && IsNumeric(right))
        {
            return Convert.ToDecimal(left, System.Globalization.CultureInfo.InvariantCulture)
                == Convert.ToDecimal(right, System.Globalization.CultureInfo.InvariantCulture);
        }

        return left.Equals(right);
    }

    private static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static IReadOnlyList<object?> ToList(object? value)
    {
        if (value is null)
        {
            return [];
        }

        if (value is string)
        {
            return [value];
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            return [.. enumerable.Cast<object?>()];
        }

        return [value];
    }

    private static string? ToText(object? value) => value as string;

    private static int CountOf(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        if (value is string)
        {
            return 1;
        }

        if (value is System.Collections.ICollection collection)
        {
            return collection.Count;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            int count = 0;
            foreach (object? _ in enumerable)
            {
                count++;
            }

            return count;
        }

        return 1;
    }
}

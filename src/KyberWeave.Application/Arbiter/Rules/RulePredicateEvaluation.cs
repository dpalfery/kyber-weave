using KyberWeave.Core.Review;

namespace KyberWeave.Core.Arbiter.Rules;

/// <summary>Evaluation of a step-0 rule predicate over a fact set.</summary>
public static class RulePredicateEvaluation
{
    /// <summary>Evaluates the predicate against <paramref name="facts"/>.</summary>
    public static bool Evaluate(this RulePredicate predicate, ArbiterFactSet facts)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(facts);
        return predicate.Operator switch
        {
            RuleOperator.All => predicate.Children.All(child => child.Evaluate(facts)),
            RuleOperator.Any => predicate.Children.Any(child => child.Evaluate(facts)),
            RuleOperator.Not => predicate.Children.Count == 1 && !predicate.Children[0].Evaluate(facts),
            RuleOperator.Exists => EvaluateExists(predicate, facts),
            RuleOperator.Equals => EvaluateEquals(predicate, facts),
            RuleOperator.In => EvaluateIn(predicate, facts),
            RuleOperator.Matches => EvaluateMatches(predicate, facts),
            RuleOperator.SubsetOf => EvaluateSubsetOf(predicate, facts),
            RuleOperator.Intersects => EvaluateIntersects(predicate, facts),
            RuleOperator.Count => EvaluateCount(predicate, facts),
            _ => false,
        };
    }

    private static bool EvaluateExists(RulePredicate predicate, ArbiterFactSet facts)
    {
        bool present = predicate.Fact is not null && facts.IsPresent(predicate.Fact);
        bool expected = predicate.Operand?.LiteralValue is bool b && b;
        return present == expected;
    }

    /// <summary>
    /// Resolves the fact this predicate names, treating a null-valued fact as absent so
    /// that every leaf operator fails it, exactly as the contract on
    /// <see cref="ArbiterFactSet"/> states.
    /// </summary>
    private static ArbiterFact? ResolveFact(ArbiterFactSet facts, string? name) =>
        name is not null && facts.TryGet(name, out ArbiterFact? fact) && fact.Value is not null
            ? fact
            : null;

    private static bool EvaluateEquals(RulePredicate predicate, ArbiterFactSet facts)
    {
        ArbiterFact? fact = ResolveFact(facts, predicate.Fact);
        if (fact is null)
        {
            return false;
        }

        if (!TryResolveOperand(predicate, facts, out object? other))
        {
            return false;
        }

        return ValuesEqual(fact.Value, other);
    }

    private static bool EvaluateIn(RulePredicate predicate, ArbiterFactSet facts)
    {
        ArbiterFact? fact = ResolveFact(facts, predicate.Fact);
        if (fact is null)
        {
            return false;
        }

        if (!TryResolveOperand(predicate, facts, out object? other))
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

    private static bool EvaluateMatches(RulePredicate predicate, ArbiterFactSet facts)
    {
        ArbiterFact? fact = ResolveFact(facts, predicate.Fact);
        if (fact is null)
        {
            return false;
        }

        if (!TryResolveOperand(predicate, facts, out object? other))
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

    private static bool EvaluateSubsetOf(RulePredicate predicate, ArbiterFactSet facts)
    {
        ArbiterFact? fact = ResolveFact(facts, predicate.Fact);
        if (fact is null)
        {
            return false;
        }

        if (!TryResolveOperand(predicate, facts, out object? other))
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

    private static bool EvaluateIntersects(RulePredicate predicate, ArbiterFactSet facts)
    {
        ArbiterFact? fact = ResolveFact(facts, predicate.Fact);
        if (fact is null)
        {
            return false;
        }

        if (!TryResolveOperand(predicate, facts, out object? other))
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

    private static bool EvaluateCount(RulePredicate predicate, ArbiterFactSet facts)
    {
        ArbiterFact? fact = ResolveFact(facts, predicate.Fact);
        if (fact is null)
        {
            return false;
        }

        if (!TryResolveOperand(predicate, facts, out object? other))
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

    private static bool TryResolveOperand(RulePredicate predicate, ArbiterFactSet facts, out object? value)
    {
        if (predicate.Operand is null)
        {
            value = null;
            return false;
        }

        if (!predicate.Operand.IsFactRef)
        {
            value = predicate.Operand.LiteralValue;
            return true;
        }

        if (predicate.Operand.FactName is not null && ResolveFact(facts, predicate.Operand.FactName) is { } referenced)
        {
            value = referenced.Value;
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
            if (TryToDecimal(left, out decimal leftDecimal)
                && TryToDecimal(right, out decimal rightDecimal))
            {
                return leftDecimal == rightDecimal;
            }

            // Two finite floating-point values too large for decimal still compare
            // as doubles. Non-finite values are unequal, never an error.
            if (IsFloating(left) && IsFloating(right))
            {
                double leftDouble = Convert.ToDouble(left, System.Globalization.CultureInfo.InvariantCulture);
                double rightDouble = Convert.ToDouble(right, System.Globalization.CultureInfo.InvariantCulture);
                return double.IsFinite(leftDouble) && double.IsFinite(rightDouble) && leftDouble == rightDouble;
            }

            return false;
        }

        return left.Equals(right);
    }

    private static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static bool IsFloating(object value) => value is float or double;

    /// <summary>
    /// Converts a numeric fact to <see cref="decimal"/> without letting a non-finite or
    /// out-of-range value throw out of step 0, which is documented as never throwing.
    /// A value that cannot be represented makes the comparison false, never an error.
    /// </summary>
    private static bool TryToDecimal(object value, out decimal result)
    {
        try
        {
            result = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            result = default;
            return false;
        }
    }

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

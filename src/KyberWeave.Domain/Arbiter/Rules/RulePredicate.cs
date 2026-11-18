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
/// such as <c>src/auth/**</c> covers <c>src/auth/a.cs</c>. A fact with no value
/// is absent: it satisfies <c>exists: false</c> and fails every other operator.
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
}

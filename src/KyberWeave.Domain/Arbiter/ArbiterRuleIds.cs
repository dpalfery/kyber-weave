namespace KyberWeave.Core.Arbiter;

/// <summary>The shipped Arbiter rule ids a source file reads by name.</summary>
public static class ArbiterRuleIds
{
    /// <summary>The rule that decides whether a changed path matches a gate's <c>applies-when.paths</c>.</summary>
    public const string GateSelect = "KW-ARB-GATE-001";
}

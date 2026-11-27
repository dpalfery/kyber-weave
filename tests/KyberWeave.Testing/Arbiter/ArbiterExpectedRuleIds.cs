using KyberWeave.Core.Arbiter;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The rule ids the shipped Arbiter configuration carries, one each, so several classes can compare a loaded
/// rule set against the same list without sharing a test class.</summary>
internal static class ArbiterExpectedRuleIds
{
    /// <summary>Every shipped rule id.</summary>
    internal static readonly string[] All =
    [
        "KW-ARB-PLAN-001",
        "KW-ARB-SCOPE-001",
        "KW-ARB-SCOPE-002",
        "KW-ARB-READY-001",
        "KW-ARB-OWNER-001",
        "KW-ARB-OWNER-002",
        "KW-ARB-ROSTER-001",
        "KW-ARB-RUN-001",
        "KW-ARB-DIFF-001",
        "KW-ARB-PLANNER-001",
        "KW-ARB-PLANNER-DIFF-001",
        "KW-ARB-READONLY-001",
        "KW-ARB-LENS-001",
        "KW-ARB-QUOTE-001",
        "KW-ARB-PREEX-001",
        "KW-ARB-CLAIM-001",
        "KW-ARB-GATE-CORROBORATED-001",
        ArbiterRuleIds.GateSelect,
        "KW-ARB-PLANREAD-001",
        "KW-ARB-GATE-REPORT-001",
        "KW-ARB-LENS-002",
        "KW-ARB-DIFFICULTY-001",
        "KW-ARB-LADDER-001",
        "KW-ARB-NITPICK-001",
        "KW-ARB-NITPICK-002",
        "KW-ARB-DUP-001",
        "KW-ARB-TRIAGE-001",
        "KW-ARB-INTAKE-001",
        "KW-ARB-DOCIMPACT-001",
        "KW-ARB-FAILURE-001",
        "KW-ARB-TRIM-001",
        "KW-ARB-COMMENT-001",
        "KW-ARB-CHECKOUT-001",
        "KW-ARB-MODE-002",
        "KW-ARB-SEVERITY-001",
        "KW-ARB-OVERRIDE-001",
    ];
}

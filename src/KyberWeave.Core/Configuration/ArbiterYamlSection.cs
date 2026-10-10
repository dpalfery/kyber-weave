namespace KyberWeave.Core.Configuration;

/// <summary>The <c>arbiter:</c> section of <c>kyber-weave.yml</c>.</summary>
internal sealed class ArbiterYamlSection
{
    public bool? Enabled { get; set; }

    public ArbiterProviderYaml? Provider { get; set; }

    public List<ArbiterRuleYaml>? Rules { get; set; }
}

/// <summary>The <c>arbiter.provider</c> mapping. Non-secret: the key never appears here.</summary>
internal sealed class ArbiterProviderYaml
{
    public string? Kind { get; set; }

    public string? Endpoint { get; set; }

    public string? Model { get; set; }

    public int? TimeoutMs { get; set; }
}

/// <summary>
/// One entry of <c>arbiter.rules</c>: either a shipped-rule override (only
/// <c>enabled</c>, <c>confidence-at-least</c> / <c>probability-below</c> and
/// <c>effects</c> may be set) or a host rule in the full rule shape.
/// </summary>
internal sealed class ArbiterRuleYaml
{
    public string? Id { get; set; }

    public bool? Enabled { get; set; }

    public double? ConfidenceAtLeast { get; set; }

    public double? ProbabilityBelow { get; set; }

    public Dictionary<string, string>? Effects { get; set; }

    /// <summary>
    /// The trigger name, or trigger names for a rule that runs on several triggers
    /// (e.g. <c>ROSTER-001</c> on <c>delegate</c> and <c>investigate</c>). The parser
    /// expands a list into one rule per trigger sharing the rule id.
    /// </summary>
    public object? Trigger { get; set; }

    public string? Question { get; set; }

    public List<string>? Answers { get; set; }

    public List<ArbiterDecideClauseYaml>? Decide { get; set; }

    public ArbiterAskYaml? Ask { get; set; }
}

/// <summary>One step-0 <c>decide</c> entry: the first matching <c>when</c> answers.</summary>
internal sealed class ArbiterDecideClauseYaml
{
    public ArbiterWhenYaml? When { get; set; }

    public string? Answer { get; set; }
}

/// <summary>
/// One step-0 <c>when</c> predicate. Leaf operators name one <c>fact</c> and one
/// operator; <c>all</c>, <c>any</c> and <c>not</c> combine child predicates.
/// Operands stay <c>object</c> because an operand is either a literal scalar or
/// list, or the name of another fact — the parser resolves which.
/// </summary>
internal sealed class ArbiterWhenYaml
{
    public string? Fact { get; set; }

    public List<ArbiterWhenYaml>? All { get; set; }

    public List<ArbiterWhenYaml>? Any { get; set; }

    public ArbiterWhenYaml? Not { get; set; }

    public bool? Exists { get; set; }

    public new object? Equals { get; set; }

    public object? In { get; set; }

    public object? Matches { get; set; }

    public object? SubsetOf { get; set; }

    public object? Intersects { get; set; }

    public object? Count { get; set; }
}

/// <summary>One step-1 <c>ask</c> question in the trigger's batched provider call.</summary>
internal sealed class ArbiterAskYaml
{
    public string? Type { get; set; }

    public Dictionary<string, string>? State { get; set; }

    public Dictionary<string, string>? Criteria { get; set; }

    public List<string>? Levels { get; set; }

    public string? Question { get; set; }

    public string? InstructionsFrom { get; set; }

    public double? ConfidenceAtLeast { get; set; }

    public double? ProbabilityBelow { get; set; }

    public string? When { get; set; }

    public List<string>? TunedFor { get; set; }
}

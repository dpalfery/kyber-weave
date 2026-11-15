using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter;

/// <summary>The <c>arbiter:</c> host configuration.</summary>
public sealed record ArbiterConfig
{
    /// <summary>Whether the Arbiter enforces anything. Squad renders hooks only when true.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether <c>arbiter.enabled</c> was explicitly set in the configuration.</summary>
    public bool? ExplicitlyEnabled { get; init; }

    /// <summary>Global scope configuration.</summary>
    public ArbiterGlobalConfig Global { get; init; } = new([]);

    /// <summary>Non-secret provider configuration.</summary>
    public ArbiterProviderConfig Provider { get; init; } = ArbiterProviderConfig.DefaultNone;

    /// <summary>Effective rules: shipped rules with host overrides applied, then host rules.</summary>
    public IReadOnlyList<ArbiterRule> Rules { get; init; } = [];

    /// <summary>The glob patterns that mark a path as a test file.</summary>
    public IReadOnlyList<string> TestFilePatterns { get; init; } = [];

    /// <summary>The <c>LENS-001</c> step-0 per-lens path table.</summary>
    public IReadOnlyList<ArbiterLensPathEntry> LensPaths { get; init; } = [];

    /// <summary>The <c>arbiter.model-targeting</c> block.</summary>
    public ArbiterModelTargetingConfig ModelTargeting { get; init; } = ArbiterModelTargetingConfig.Defaults;

    /// <summary>The <c>arbiter.stores</c> block.</summary>
    public ArbiterStoresConfig Stores { get; init; } = ArbiterStoresConfig.Defaults;

    /// <summary>The <c>arbiter.comment-triage</c> block.</summary>
    public ArbiterCommentTriageConfig CommentTriage { get; init; } = ArbiterCommentTriageConfig.Defaults;

    /// <summary>The <c>arbiter.dispatch-budget</c> block.</summary>
    public ArbiterDispatchBudgetConfig DispatchBudget { get; init; } = ArbiterDispatchBudgetConfig.Defaults;

    /// <summary>The <c>arbiter.seat-budget</c> block: the budget of one <c>review-lens</c> or <c>review-triage</c> dispatch.</summary>
    public ArbiterDispatchBudgetConfig SeatBudget { get; init; } = ArbiterDispatchBudgetConfig.SeatDefaults;

    /// <summary>
    /// Product defaults for an unconfigured host: disabled, no provider, every
    /// shipped rule enabled.
    /// </summary>
    /// <remarks>
    /// Loaded from the embedded <c>default-rules.yml</c> rather than hardcoded so the
    /// shipped catalogue has exactly one source of truth: the file the validator and
    /// the documentation both read.
    /// </remarks>
    public static ArbiterConfig ProductDefaults { get; } = ArbiterConfigLoader.LoadEmbeddedDefaults();
}

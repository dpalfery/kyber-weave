using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter;

/// <summary>Which provider answers step-1 questions. The key never appears in configuration.</summary>
public enum ArbiterProviderKind
{
    /// <summary>No provider: step-1 rules answer undecidable (Req 19.1).</summary>
    None,

    /// <summary>The TypeSafe systemone API, self-hosted or at the pinned endpoint.</summary>
    Systemone,
}

/// <summary>Non-secret provider configuration: endpoint, model and latency budget.</summary>
/// <param name="Kind">The provider kind.</param>
/// <param name="Endpoint">The systemone base URL.</param>
/// <param name="Model">The pinned answering model.</param>
/// <param name="TimeoutMs">The per-call budget inside the hook latency budget.</param>
public sealed record ArbiterProviderConfig(
    ArbiterProviderKind Kind,
    string Endpoint,
    string Model,
    int TimeoutMs)
{
    /// <summary>Product default: no provider, pinned endpoint and model, 3s budget.</summary>
    public static ArbiterProviderConfig DefaultNone { get; } = new(
        ArbiterProviderKind.None, "https://api.typesafe.ai/v1", "jev-1.14.0", 3000);
}

/// <summary>
/// One <c>OWNER-001</c> file-kind row: the first row whose patterns cover a path
/// owns it. Data, not code, so a host reading the rule sees the whole map.
/// </summary>
/// <param name="Patterns">Glob patterns for this owner, in match order.</param>
/// <param name="Owner">
///     The Squad agent owning matching files, or <see cref="TaskOwner"/> for test files, which belong to whichever
///     specialist owns the task.
/// </param>
public sealed record ArbiterFileKindMapEntry(IReadOnlyList<string> Patterns, string Owner)
{
    /// <summary>The owner a test-file row names: the task's own specialist, not a dedicated test agent.</summary>
    public const string TaskOwner = "task-owner";
}

/// <summary>
/// One <c>LENS-001</c> step-0 row: when every changed path matches one of
/// <see cref="NotApplicableWhenAllMatch"/>, the lens does not apply. Only path
/// conditions the lens's own Applicability section states may appear here;
/// every other lens defers to step 1.
/// </summary>
/// <param name="Lens">The lens name.</param>
/// <param name="NotApplicableWhenAllMatch">Changed paths all matching these skip the lens.</param>
public sealed record ArbiterLensPathEntry(string Lens, IReadOnlyList<string> NotApplicableWhenAllMatch);

/// <summary>Global scope settings under <c>arbiter.global</c>.</summary>
public sealed record ArbiterGlobalConfig(IReadOnlyList<string> Targets);

/// <summary>
/// The <c>arbiter.model-targeting</c> block: how the Arbiter routes work to a model tier.
/// </summary>
/// <param name="Mode">The targeting mode: <c>off</c>, <c>shadow</c> or <c>enforce</c>.</param>
/// <param name="DifficultyProfiles">The profile each difficulty (<c>easy</c>, <c>standard</c>, <c>hard</c>) resolves to.</param>
/// <param name="Ladder">The ordered profiles a retarget may climb, cheapest first.</param>
public sealed record ArbiterModelTargetingConfig(
    string Mode,
    IReadOnlyDictionary<string, string> DifficultyProfiles,
    IReadOnlyList<string> Ladder)
{
    /// <summary>The shipped defaults: shadow mode, easy to fast, standard to general, hard to deep-planning.</summary>
    public static ArbiterModelTargetingConfig Defaults { get; } = new(
        "shadow",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["easy"] = "fast",
            ["standard"] = "general",
            ["hard"] = "deep-planning",
        },
        ["fast", "general", "deep-planning"]);
}

/// <summary>The <c>arbiter.stores</c> block: size and retention limits for the Arbiter's stores.</summary>
/// <param name="MaxItemBytes">The largest single stored item, in bytes.</param>
/// <param name="RetainDays">How many days a stored item is kept.</param>
public sealed record ArbiterStoresConfig(int MaxItemBytes, int RetainDays)
{
    /// <summary>The shipped defaults: 16 KiB items kept for 14 days.</summary>
    public static ArbiterStoresConfig Defaults { get; } = new(16384, 14);
}

/// <summary>The <c>arbiter.comment-triage</c> block.</summary>
/// <param name="BotLogins">Logins of review bots whose comments the triage treats as bot output.</param>
public sealed record ArbiterCommentTriageConfig(IReadOnlyList<string> BotLogins)
{
    /// <summary>The shipped default: no bot logins.</summary>
    public static ArbiterCommentTriageConfig Defaults { get; } = new([]);
}

/// <summary>
/// The <c>arbiter.dispatch-budget</c> block: how long, and for how many tool calls, one developer or docs
/// dispatch may run before the worker guard stops it (<c>KW-ARB-BUDGET-001</c>).
/// </summary>
/// <param name="Minutes">Wall-clock minutes from the dispatch's first tool call.</param>
/// <param name="ToolCalls">Tool calls the dispatch may make.</param>
/// <param name="CallMinutes">
/// Minutes between two consecutive tool calls of the dispatch past which the earlier call is taken to have hung:
/// the guard sees only the start of each call, so the gap is the earlier call's run time plus the model's own time.
/// </param>
public sealed record ArbiterDispatchBudgetConfig(int Minutes, int ToolCalls, int CallMinutes = 5)
{
    /// <summary>The shipped defaults: 15 minutes or 150 tool calls, whichever comes first, and 5 minutes for one call.</summary>
    public static ArbiterDispatchBudgetConfig Defaults { get; } = new(15, 150, 5);
}

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

    /// <summary>The <c>OWNER-001</c> file-kind map, first match wins.</summary>
    public IReadOnlyList<ArbiterFileKindMapEntry> OwnerFileKindMap { get; init; } = [];

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

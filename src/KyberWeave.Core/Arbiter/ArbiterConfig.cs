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
        ArbiterProviderKind.None, "https://api.typesafe.ai/v1", "jev-1.13.0", 3000);
}

/// <summary>
/// One <c>OWNER-001</c> file-kind row: the first row whose patterns cover a path
/// owns it. Data, not code, so a host reading the rule sees the whole map.
/// </summary>
/// <param name="Patterns">Glob patterns for this owner, in match order.</param>
/// <param name="Owner">The Squad agent owning matching files.</param>
public sealed record ArbiterFileKindMapEntry(IReadOnlyList<string> Patterns, string Owner);

/// <summary>
/// One <c>LENS-001</c> step-0 row: when every changed path matches one of
/// <see cref="NotApplicableWhenAllMatch"/>, the lens does not apply. Only path
/// conditions the lens's own Applicability section states may appear here;
/// every other lens defers to step 1.
/// </summary>
/// <param name="Lens">The lens name.</param>
/// <param name="NotApplicableWhenAllMatch">Changed paths all matching these skip the lens.</param>
public sealed record ArbiterLensPathEntry(string Lens, IReadOnlyList<string> NotApplicableWhenAllMatch);

/// <summary>The <c>arbiter:</c> host configuration.</summary>
public sealed record ArbiterConfig
{
    /// <summary>Whether the Arbiter enforces anything. Squad renders hooks only when true.</summary>
    public bool Enabled { get; init; }

    /// <summary>Non-secret provider configuration.</summary>
    public ArbiterProviderConfig Provider { get; init; } = ArbiterProviderConfig.DefaultNone;

    /// <summary>Effective rules: shipped rules with host overrides applied, then host rules.</summary>
    public IReadOnlyList<ArbiterRule> Rules { get; init; } = [];

    /// <summary>The <c>OWNER-001</c> file-kind map, first match wins.</summary>
    public IReadOnlyList<ArbiterFileKindMapEntry> OwnerFileKindMap { get; init; } = [];

    /// <summary>The <c>LENS-001</c> step-0 per-lens path table.</summary>
    public IReadOnlyList<ArbiterLensPathEntry> LensPaths { get; init; } = [];

    /// <summary>
    /// Product defaults for an unconfigured host: disabled, no provider, all 18
    /// shipped rules enabled.
    /// </summary>
    /// <remarks>
    /// Loaded from the embedded <c>default-rules.yml</c> rather than hardcoded so the
    /// shipped catalogue has exactly one source of truth: the file the validator and
    /// the documentation both read.
    /// </remarks>
    public static ArbiterConfig ProductDefaults { get; } = ArbiterConfigLoader.LoadEmbeddedDefaults();
}

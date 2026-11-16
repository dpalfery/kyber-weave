using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Configuration;

/// <summary>How Kyber-Squad handles harness capabilities that cannot be represented exactly.</summary>
public enum SquadTranslationMode
{
    BestEffort
}

/// <summary>Kyber-Squad host configuration.</summary>
public sealed class SquadConfig
{
    private static readonly IReadOnlyList<SquadTarget> NoTargets = Array.Empty<SquadTarget>();

    /// <summary>The bundle installed when the host does not select one.</summary>
    public string Bundle { get; init; } = "full";

    /// <summary>The optional exact Squad version pin.</summary>
    public string? Version { get; init; }

    /// <summary>Configured harness targets. An empty list leaves target selection to detection.</summary>
    public IReadOnlyList<SquadTarget> Targets { get; init; } = NoTargets;

    /// <summary>Harnesses removed from the resolved target set.</summary>
    public IReadOnlyList<SquadTarget> Exclusions { get; init; } = NoTargets;

    /// <summary>The configured capability-translation policy.</summary>
    public SquadTranslationMode Translation { get; init; } = SquadTranslationMode.BestEffort;

    /// <summary>Product defaults for an unconfigured host.</summary>
    public static SquadConfig ProductDefaults { get; } = new();
}

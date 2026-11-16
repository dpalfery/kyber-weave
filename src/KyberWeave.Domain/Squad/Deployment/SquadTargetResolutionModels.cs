namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The lifecycle operation requesting target resolution.</summary>
public enum SquadTargetOperation
{
    Install,
    Update,
    Uninstall
}

/// <summary>The outcome category produced by target resolution.</summary>
public enum SquadTargetResolutionKind
{
    Resolved,
    InteractiveSelectionRequired,
    Failure
}

/// <summary>The highest-precedence source that selected the target set.</summary>
public enum SquadTargetResolutionSource
{
    None,
    Explicit,
    Configuration,
    Receipt,
    Markers
}

/// <summary>Inputs to the pure Squad target-selection decision.</summary>
public sealed class SquadTargetResolutionRequest
{
    public required string RootPath { get; init; }

    public SquadTargetOperation Operation { get; init; }

    public IReadOnlyList<string> ExplicitTargets { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SquadTarget> ConfiguredTargets { get; init; } = Array.Empty<SquadTarget>();

    public IReadOnlyList<SquadTarget> ReceiptTargets { get; init; } = Array.Empty<SquadTarget>();

    public IReadOnlyList<string> ExplicitExclusions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SquadTarget> ConfiguredExclusions { get; init; } = Array.Empty<SquadTarget>();

    public bool IsInteractive { get; init; }
}

/// <summary>A target-selection decision for the CLI host to render or act upon.</summary>
public sealed class SquadTargetResolutionDecision
{
    public required SquadTargetResolutionKind Kind { get; init; }

    public required IReadOnlyList<SquadTarget> Targets { get; init; }

    public required SquadTargetResolutionSource Source { get; init; }

    public int? ExitCode { get; init; }

    public string? RecoveryCommand { get; init; }
}

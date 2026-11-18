using System.Collections.Frozen;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The mechanism by which an instance replaces or layers harness configuration.</summary>
public enum SquadInstanceKind
{
    HomeReplace,
    ConfigLayer,
    None
}

/// <summary>The readiness state of an instance target.</summary>
public enum SquadInstanceStatus
{
    Ready,
    SpikePending,
    Unsupported
}

/// <summary>Catalog entry describing harness instance parameters and support status.</summary>
public sealed record SquadInstanceRow(
    SquadTarget Target,
    string Executable,
    SquadInstanceKind Kind,
    string? Variable,
    IReadOnlyList<string> ExtraLinks,
    IReadOnlyList<string> NeverLinked,
    SquadInstanceStatus Status,
    string? Reason);

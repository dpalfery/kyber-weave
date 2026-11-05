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

/// <summary>
/// Canonical catalog mapping all approved Squad targets to their instance configuration,
/// executable names, environment variables, and support status.
/// </summary>
public static class SquadInstanceCatalog
{
    private static readonly IReadOnlyList<SquadInstanceRow> CatalogRows =
    [
        new(
            SquadTarget.Claude,
            "claude",
            SquadInstanceKind.HomeReplace,
            "CLAUDE_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.Ready,
            null),
        new(
            SquadTarget.Codex,
            "codex",
            SquadInstanceKind.HomeReplace,
            "CODEX_HOME",
            [],
            [],
            SquadInstanceStatus.Ready,
            null),
        new(
            SquadTarget.Copilot,
            "copilot",
            SquadInstanceKind.HomeReplace,
            "COPILOT_HOME",
            [],
            [],
            SquadInstanceStatus.Ready,
            null),
        new(
            SquadTarget.Pi,
            "pi",
            SquadInstanceKind.HomeReplace,
            "PI_CODING_AGENT_DIR",
            [],
            [],
            SquadInstanceStatus.Ready,
            null),
        new(
            SquadTarget.OpenCode,
            "opencode",
            SquadInstanceKind.ConfigLayer,
            "OPENCODE_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.SpikePending,
            null),
        new(
            SquadTarget.Kilo,
            "kilo",
            SquadInstanceKind.ConfigLayer,
            "KILO_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.SpikePending,
            null),
        new(
            SquadTarget.Cursor,
            "cursor",
            SquadInstanceKind.ConfigLayer,
            "CURSOR_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.SpikePending,
            null),
        new(
            SquadTarget.ZCode,
            "zcode",
            SquadInstanceKind.ConfigLayer,
            "ZCODE_STORAGE_DIR",
            [],
            [],
            SquadInstanceStatus.SpikePending,
            null),
        new(
            SquadTarget.Antigravity,
            "antigravity",
            SquadInstanceKind.None,
            null,
            [],
            [],
            SquadInstanceStatus.Unsupported,
            "Antigravity does not support configuration or home directory redirection via environment variable."),
        new(
            SquadTarget.Factory,
            "factory",
            SquadInstanceKind.None,
            null,
            [],
            [],
            SquadInstanceStatus.Unsupported,
            "Factory does not support configuration or home directory redirection via environment variable."),
        new(
            SquadTarget.CommandCode,
            "commandcode",
            SquadInstanceKind.None,
            null,
            [],
            [],
            SquadInstanceStatus.Unsupported,
            "CommandCode does not support configuration or home directory redirection via environment variable."),
        new(
            SquadTarget.Devin,
            "devin",
            SquadInstanceKind.None,
            null,
            [],
            [],
            SquadInstanceStatus.Unsupported,
            "Devin stores configuration in system app data and does not support instance directory redirection."),
        new(
            SquadTarget.Warp,
            "warp",
            SquadInstanceKind.None,
            null,
            [],
            [],
            SquadInstanceStatus.Unsupported,
            "Warp does not support configuration or home directory redirection via environment variable.")
    ];

    private static readonly FrozenDictionary<SquadTarget, SquadInstanceRow> RowsByTarget =
        CatalogRows.ToFrozenDictionary(row => row.Target);

    /// <summary>The complete roster of instance rows across all approved Squad targets.</summary>
    public static IReadOnlyList<SquadInstanceRow> AllRows => CatalogRows;

    /// <summary>Returns the catalog row for the specified target.</summary>
    /// <param name="target">The target to look up.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when target is not defined in the catalog.</exception>
    public static SquadInstanceRow GetRow(SquadTarget target)
    {
        if (RowsByTarget.TryGetValue(target, out SquadInstanceRow? row))
        {
            return row;
        }

        throw new ArgumentOutOfRangeException(
            nameof(target),
            target,
            $"Target '{target}' is not registered in the instance catalog.");
    }
}

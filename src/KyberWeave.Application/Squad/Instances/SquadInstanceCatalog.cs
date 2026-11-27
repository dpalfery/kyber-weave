using System.Collections.Frozen;
using KyberWeave.Arbiter.Hooks;

namespace KyberWeave.Core.Squad.Deployment;

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
            HarnessRegistry.SquadTokenFor(SquadTarget.Claude),
            SquadInstanceKind.HomeReplace,
            "CLAUDE_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.Ready,
            null),
        new(
            SquadTarget.Codex,
            HarnessRegistry.SquadTokenFor(SquadTarget.Codex),
            SquadInstanceKind.HomeReplace,
            "CODEX_HOME",
            [],
            [],
            SquadInstanceStatus.Ready,
            null),
        new(
            SquadTarget.Copilot,
            HarnessRegistry.SquadTokenFor(SquadTarget.Copilot),
            SquadInstanceKind.HomeReplace,
            "COPILOT_HOME",
            [],
            [],
            SquadInstanceStatus.Ready,
            null),
        new(
            SquadTarget.Pi,
            HarnessRegistry.SquadTokenFor(SquadTarget.Pi),
            SquadInstanceKind.HomeReplace,
            "PI_CODING_AGENT_DIR",
            [],
            [],
            SquadInstanceStatus.Ready,
            null),
        new(
            SquadTarget.OpenCode,
            HarnessRegistry.SquadTokenFor(SquadTarget.OpenCode),
            SquadInstanceKind.ConfigLayer,
            "OPENCODE_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.SpikePending,
            null),
        new(
            SquadTarget.Kilo,
            HarnessRegistry.SquadTokenFor(SquadTarget.Kilo),
            SquadInstanceKind.ConfigLayer,
            "KILO_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.SpikePending,
            null),
        new(
            SquadTarget.Cursor,
            HarnessRegistry.SquadTokenFor(SquadTarget.Cursor),
            SquadInstanceKind.ConfigLayer,
            "CURSOR_CONFIG_DIR",
            [],
            [],
            SquadInstanceStatus.SpikePending,
            null),
        new(
            SquadTarget.ZCode,
            HarnessRegistry.SquadTokenFor(SquadTarget.ZCode),
            SquadInstanceKind.ConfigLayer,
            "ZCODE_STORAGE_DIR",
            [],
            [],
            SquadInstanceStatus.SpikePending,
            null),
        new(
            SquadTarget.Antigravity,
            HarnessRegistry.SquadTokenFor(SquadTarget.Antigravity),
            SquadInstanceKind.None,
            null,
            [],
            [],
            SquadInstanceStatus.Unsupported,
            "Antigravity does not support configuration or home directory redirection via environment variable."),
        new(
            SquadTarget.Factory,
            HarnessRegistry.SquadTokenFor(SquadTarget.Factory),
            SquadInstanceKind.None,
            null,
            [],
            [],
            SquadInstanceStatus.Unsupported,
            "Factory does not support configuration or home directory redirection via environment variable."),
        new(
            SquadTarget.CommandCode,
            HarnessRegistry.SquadTokenFor(SquadTarget.CommandCode),
            SquadInstanceKind.None,
            null,
            [],
            [],
            SquadInstanceStatus.Unsupported,
            "CommandCode has no config-directory variable and no XDG base directory: only whole-HOME redirection can move it, which also moves authentication and system-tool state. Its global root is ~/.commandcode, not the home directory a link sync would copy."),
        new(
            SquadTarget.Devin,
            HarnessRegistry.SquadTokenFor(SquadTarget.Devin),
            SquadInstanceKind.None,
            null,
            [],
            [],
            SquadInstanceStatus.Unsupported,
            "Devin stores configuration in system app data and does not support instance directory redirection."),
        new(
            SquadTarget.Warp,
            HarnessRegistry.SquadTokenFor(SquadTarget.Warp),
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

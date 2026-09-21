namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Resolves the real per-user global root for each target's agents and skills.</summary>
public interface ISquadGlobalRootResolver
{
    /// <summary>
    /// Returns the absolute path to the global root for <c>target</c>'s agents and skills,
    /// reading the target's override environment variable first, falling back to the
    /// verified default beneath the home directory if the override is null or empty.
    /// Targets without a renderer throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    string ResolveGlobalRoot(SquadTarget target);
}

/// <summary>
/// Resolves each target's verified global root from an override environment variable
/// or its documented default path. Verified against live harness configurations on 2026-09-14:
/// Claude Code (P40, `.claude/`), Codex (P41, `.codex/`), Cursor (P42, `.cursor/`),
/// Copilot (P43, `.copilot/`), Antigravity (P44, `.gemini/config/`), Pi (P9, P3, `.pi/agent/`),
/// OpenCode (`~/.config/opencode/`, verified against OpenCode's agents/skills/config docs),
/// and Kilo (`~/.config/kilo/`, verified against Kilo's custom-subagents and settings docs).
/// Factory personal droids and skills live under `~/.factory` with no environment override
/// (docs.factory.ai/harness/subagents and docs.factory.ai/harness/skills, 2026-09-16).
/// Warp skills live under `~/.warp` (`~/.warp/skills/`, verified against docs.warp.dev/features/skills).
/// ZCode agents, skills, and commands live under `$ZCODE_STORAGE_DIR` (default `~/.zcode`),
/// verified against zai-org/ZCode 3.14.0 on 2026-09-21.
/// The home directory and every override environment value must be fully qualified:
/// a relative root would be completed against the process working directory by
/// <see cref="SquadPathPolicy.ResolveFile"/>.
/// </summary>
/// <remarks>
/// Each target's root was verified for 2026-09-14:
/// - Claude: `.claude/agents/` and `.claude/skills/` under `$CLAUDE_CONFIG_DIR` (default `~/.claude`)
/// - Codex: `.codex/agents/` and `.codex/skills/` under `$CODEX_HOME` (default `~/.codex`);
///   `CODEX_HOME` confirmed live in the owner's `config.toml` on this machine
/// - Cursor: `.cursor/agents/` and `.cursor/skills/` under `$CURSOR_CONFIG_DIR` (default `~/.cursor`)
/// - Copilot: `.copilot/agents/` and `.copilot/skills/` under `$COPILOT_HOME` (default `~/.copilot`)
/// - Antigravity: `skills/` under `~/.gemini/config/` (no override, no agent primitive)
/// - Pi: `agents/` and `skills/` under `$PI_CODING_AGENT_DIR` (default `~/.pi/agent`)
/// - OpenCode: `agents/` and `skills/` under a three-tier root — `$OPENCODE_CONFIG_DIR` first,
///   then `$XDG_CONFIG_HOME/opencode`, then `~/.config/opencode` — unlike every other target's
///   two-tier (one override, one default) chain, because OpenCode's own config resolution
///   respects `XDG_CONFIG_HOME` as a documented middle tier.
/// - Kilo: `agents/` and `skills/` under `$XDG_CONFIG_HOME/kilo` if set, otherwise
///   `~/.config/kilo`. Kilo's own docs place global agent markdown at
///   `~/.config/kilo/agents/` and global config at `~/.config/kilo/kilo.jsonc`.
/// - Factory: `droids/` and `skills/` under `~/.factory` (no override; no all-users path).
/// - Warp: `skills/` under `~/.warp` (`~/.warp/skills/`, verified against docs.warp.dev).
/// - ZCode: `agents/`, `skills/`, and `commands/` under `$ZCODE_STORAGE_DIR`, default
///   `~/.zcode`. The environment variable reaches the agent root through the runtime
///   config's `storage.dir` (`env-config.adapter.ts` maps `ZCODE_STORAGE_DIR` onto it,
///   and `create-app.ts` resolves the subagent root from it). The same key can also be
///   set in `~/.zcode/cli/config.json`, which no environment read can observe — see
///   [the todo](../../../../docs/todo/zcode-storage-dir-config-override.md).
/// </remarks>
public sealed class SquadGlobalRoots : ISquadGlobalRootResolver
{
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly string _homeDirectory;

    public SquadGlobalRoots(Func<string, string?> getEnvironmentVariable, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        _getEnvironmentVariable = getEnvironmentVariable;
        _homeDirectory = RequireFullyQualified(homeDirectory, nameof(homeDirectory));
    }

    public string ResolveGlobalRoot(SquadTarget target)
    {
        return target switch
        {
            SquadTarget.Claude => ResolveWithOverride("CLAUDE_CONFIG_DIR", ".claude"),
            SquadTarget.Codex => ResolveWithOverride("CODEX_HOME", ".codex"),
            SquadTarget.Cursor => ResolveWithOverride("CURSOR_CONFIG_DIR", ".cursor"),
            SquadTarget.Copilot => ResolveWithOverride("COPILOT_HOME", ".copilot"),
            SquadTarget.Antigravity => ResolveWithOverride(null, Path.Combine(".gemini", "config")),
            SquadTarget.Pi => ResolveWithOverride("PI_CODING_AGENT_DIR", Path.Combine(".pi", "agent")),
            SquadTarget.OpenCode => ResolveOpenCodeRoot(),
            SquadTarget.Kilo => ResolveXdgConfigAppRoot("kilo"),
            SquadTarget.Factory => ResolveWithOverride(null, ".factory"),
            SquadTarget.Warp => ResolveWithOverride(null, ".warp"),
            SquadTarget.ZCode => ResolveWithOverride("ZCODE_STORAGE_DIR", ".zcode"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                $"No verified global root exists for target '{target}'; " +
                "this target cannot be deployed with --global.")
        };
    }

    private string ResolveWithOverride(string? overrideVariableName, string defaultRelativePath)
    {
        if (overrideVariableName is not null)
        {
            string? overrideValue = _getEnvironmentVariable(overrideVariableName);
            if (!string.IsNullOrEmpty(overrideValue))
            {
                return RequireFullyQualified(overrideValue, overrideVariableName);
            }
        }

        return Path.Combine(_homeDirectory, defaultRelativePath);
    }

    /// <summary>
    /// OpenCode's own config resolution (https://opencode.ai/docs/config) checks
    /// <c>OPENCODE_CONFIG_DIR</c> first, then respects <c>XDG_CONFIG_HOME</c> before falling
    /// back to <c>~/.config/opencode</c> — the one target whose fallback chain has three tiers
    /// rather than the two <see cref="ResolveWithOverride"/> covers.
    /// </summary>
    private string ResolveOpenCodeRoot()
    {
        string? configDirOverride = _getEnvironmentVariable("OPENCODE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(configDirOverride))
        {
            return RequireFullyQualified(configDirOverride, "OPENCODE_CONFIG_DIR");
        }

        return ResolveXdgConfigAppRoot("opencode");
    }

    private string ResolveXdgConfigAppRoot(string applicationDirectoryName)
    {
        string? xdgConfigHome = _getEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdgConfigHome))
        {
            return Path.Combine(
                RequireFullyQualified(xdgConfigHome, "XDG_CONFIG_HOME"),
                applicationDirectoryName);
        }

        return Path.Combine(_homeDirectory, ".config", applicationDirectoryName);
    }

    /// <summary>
    /// <see cref="SquadPathPolicy.ResolveFile"/> calls <see cref="Path.GetFullPath(string)"/> on
    /// the root. A relative value would therefore resolve against the process working directory
    /// and a <c>--global</c> install could write outside the intended home tree.
    /// </summary>
    private static string RequireFullyQualified(string path, string paramName)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "A Squad global root must be fully qualified so it cannot resolve against the process working directory.",
                paramName);
        }

        return path;
    }
}

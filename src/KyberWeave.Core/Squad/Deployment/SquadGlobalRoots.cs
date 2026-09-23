using System.Text.Json;

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
/// - Antigravity: `agents/` and `skills/` under `~/.gemini/config/` (no override; native agent primitive since 1.2.7)
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
/// - ZCode: `agents/`, `skills/`, and `commands/` under the resolved storage directory —
///   `$ZCODE_STORAGE_DIR` first, then `storage.dir` from `~/.zcode/cli/config.json`, then
///   `~/.zcode`. This is the only target whose root can come from a file, because it is the
///   only one whose harness resolves the value through a layered runtime config rather than
///   an environment variable alone: `createConfig` layers system defaults, the user config
///   file, project config files, then `ZCODE_*` environment variables, so the environment
///   outranks the file. Project config files sit between the two and are deliberately not
///   read here: honouring them would make a `--global` root depend on the working directory,
///   which is the one thing `--global` exists not to do.
/// </remarks>
public sealed class SquadGlobalRoots : ISquadGlobalRootResolver
{
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, string?> _readFileText;
    private readonly string _homeDirectory;

    /// <param name="getEnvironmentVariable">Reads an override environment variable's value.</param>
    /// <param name="homeDirectory">The fully qualified per-user home directory.</param>
    /// <param name="readFileText">
    /// Reads a configuration file's text, or returns null when it is absent or unreadable.
    /// Core defines the port and the composition root supplies the implementation; the
    /// parameter is optional so every existing two-argument construction keeps working, and a
    /// null reader simply means the one file-backed root falls through to its default.
    /// </param>
    public SquadGlobalRoots(
        Func<string, string?> getEnvironmentVariable,
        string homeDirectory,
        Func<string, string?>? readFileText = null)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        _getEnvironmentVariable = getEnvironmentVariable;
        _readFileText = readFileText ?? (_ => null);
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
            SquadTarget.ZCode => ResolveZCodeRoot(),
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
    /// Resolves ZCode's storage directory the way ZCode itself layers it: the
    /// <c>ZCODE_STORAGE_DIR</c> environment variable outranks <c>storage.dir</c> in
    /// <c>~/.zcode/cli/config.json</c>, which outranks the <c>~/.zcode</c> default
    /// (<c>createConfig</c> in <c>adapters/src/config/config-factory.ts</c>, verified against
    /// zai-org/ZCode 3.14.0 on 2026-09-21).
    /// </summary>
    /// <remarks>
    /// The configured value may itself be <c>~/</c>-relative, because ZCode expands that form
    /// in <c>resolveConfigPath</c>. A value that is neither home-relative nor fully qualified
    /// is rejected rather than completed against the process working directory, for the reason
    /// <see cref="RequireFullyQualified"/> states. A malformed or unreadable file is not an
    /// error: ZCode's own loader swallows it and falls back to the default, so resolving to a
    /// root ZCode will not use would be worse than agreeing with it.
    /// </remarks>
    private string ResolveZCodeRoot()
    {
        string? environmentOverride = _getEnvironmentVariable("ZCODE_STORAGE_DIR");
        if (!string.IsNullOrEmpty(environmentOverride))
        {
            return RequireFullyQualified(environmentOverride, "ZCODE_STORAGE_DIR");
        }

        string defaultRoot = Path.Combine(_homeDirectory, ".zcode");
        string? configured = ReadZCodeConfiguredStorageDirectory(
            Path.Combine(defaultRoot, "cli", "config.json"));

        return configured is null
            ? defaultRoot
            : RequireFullyQualified(ExpandHomeRelative(configured), "storage.dir");
    }

    private string? ReadZCodeConfiguredStorageDirectory(string configPath)
    {
        string? text = _readFileText(configPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("storage", out JsonElement storage) ||
                storage.ValueKind != JsonValueKind.Object ||
                !storage.TryGetProperty("dir", out JsonElement dir) ||
                dir.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? value = dir.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Expands the leading <c>~/</c> ZCode's own <c>resolveConfigPath</c> accepts.</summary>
    private string ExpandHomeRelative(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(_homeDirectory, path[2..])
            : path;

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

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
/// Copilot (P43, `.copilot/`), Antigravity (P44, `.gemini/config/`), and Pi (P9, P3, `.pi/agent/`).
/// Only `CODEX_HOME` was observed live; the others are documented and verified in the plan.
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
        _homeDirectory = homeDirectory;
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
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                $"No verified global root exists for target '{target}'; " +
                "targets without a renderer cannot be deployed globally.")
        };
    }

    private string ResolveWithOverride(string? overrideVariableName, string defaultRelativePath)
    {
        if (overrideVariableName is not null)
        {
            string? overrideValue = _getEnvironmentVariable(overrideVariableName);
            if (!string.IsNullOrEmpty(overrideValue))
            {
                return overrideValue;
            }
        }

        return Path.Combine(_homeDirectory, defaultRelativePath);
    }
}

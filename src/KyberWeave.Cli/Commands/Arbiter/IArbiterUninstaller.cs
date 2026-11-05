namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Removes Arbiter-owned artifacts for one validated harness target, anchored at a deployment root.
/// </summary>
/// <remarks>
/// Only Squad-owned files and hook blocks are removed: standalone shim/plugin files the renderers own
/// outright, and hook entries the shared hook files carry under the arbiter command signature. Agent
/// frontmatter, MCP grants, and any other operator-owned surface are left untouched.
/// </remarks>
public interface IArbiterUninstaller
{
    /// <summary>Removes Arbiter-owned artifacts for <paramref name="target"/> beneath the configured root.</summary>
    void Uninstall(string target);
}

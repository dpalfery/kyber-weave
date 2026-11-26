using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Arbiter;

/// <summary>Names the file under a harness's global root that carries the Arbiter hook.</summary>
public static class ArbiterGlobalHookPaths
{
    /// <summary>Resolves the path of the file holding the global Arbiter hook for a harness.</summary>
    /// <param name="target">The harness.</param>
    /// <param name="home">The home directory the global root falls back to.</param>
    /// <param name="globalRoots">Resolves each harness's global root; built over <paramref name="environmentReader"/> when null.</param>
    /// <param name="environmentReader">Reads environment variables for the default resolver; reads nothing when null.</param>
    /// <returns>The path, or null when the harness has no global hook file or its root cannot be resolved.</returns>
    public static string? Resolve(
        SquadTarget target,
        string home,
        ISquadGlobalRootResolver? globalRoots = null,
        Func<string, string?>? environmentReader = null)
    {
        ISquadGlobalRootResolver roots = globalRoots ?? new SquadGlobalRoots(environmentReader ?? (_ => null), home);
        string root;
        try
        {
            root = roots.ResolveGlobalRoot(target);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return target switch
        {
            SquadTarget.Claude => Path.Combine(root, "settings.json"),
            SquadTarget.Cursor => Path.Combine(root, "hooks.json"),
            SquadTarget.Codex => Path.Combine(root, "hooks.json"),
            SquadTarget.Factory => Path.Combine(root, "hooks.json"),
            SquadTarget.Devin => Path.Combine(root, "hooks.v1.json"),
            SquadTarget.Antigravity => Path.Combine(root, "hooks.json"),
            SquadTarget.Kilo => Path.Combine(root, "plugin", "kyber-arbiter.ts"),
            SquadTarget.OpenCode => Path.Combine(root, "plugins", "kyber-arbiter.ts"),
            SquadTarget.Pi => Path.Combine(root, "extensions", "kyber-arbiter.ts"),
            SquadTarget.Copilot => Path.Combine(root, "hooks", "kyber-arbiter.json"),
            SquadTarget.CommandCode => Path.Combine(root, "mods", "kyber-arbiter.ts"),
            _ => null,
        };
    }
}

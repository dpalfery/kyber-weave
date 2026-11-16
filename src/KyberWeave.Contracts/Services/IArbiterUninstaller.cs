using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Contracts.Services;

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
    /// <summary>Removes Arbiter-owned artifacts for <paramref name="target"/> beneath <paramref name="root"/>.</summary>
    /// <param name="root">The deployment root: the project, or the home for global scope.</param>
    /// <param name="scope">Whether the deployment is project or global.</param>
    /// <param name="target">The harness token.</param>
    void Uninstall(string root, SquadDeploymentScope scope, string target);

    /// <summary>
    /// Whether any arbiter-owned artifact for <paramref name="target"/> exists beneath <paramref name="root"/>: a
    /// receipt-owned file on disk, or a shared hook file still carrying a receipt-owned block entry. An unparsable hook
    /// file counts as present so it fails honestly in <see cref="Uninstall"/> instead of reading as nothing to uninstall.
    /// An unreadable receipt propagates for the same reason rather than reading as absent.
    /// </summary>
    /// <param name="root">The deployment root.</param>
    /// <param name="target">The harness.</param>
    /// <param name="scope">Whether the deployment is project or global.</param>
    /// <returns>True when an owned artifact is present.</returns>
    bool HasDeployment(string root, SquadTarget target, SquadDeploymentScope scope);

    /// <summary>
    /// Whether the receipts record another target in this root, the global scope or any registered project. The key is
    /// stored per endpoint origin, not per target, so deleting it while another target remains deployed removes that
    /// target's auth too.
    /// </summary>
    /// <remarks>
    /// The answer is best effort: an unreadable receipt says nothing about the other targets, and <c>--remove-key</c>
    /// is the operator's explicit request either way.
    /// </remarks>
    /// <param name="root">The deployment root the uninstall ran against.</param>
    /// <param name="target">The harness being uninstalled.</param>
    /// <returns>True when another target is still deployed.</returns>
    bool AnotherTargetIsDeployed(string root, SquadTarget target);
}

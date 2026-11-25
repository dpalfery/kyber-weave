using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>Parameters for a Squad render execution.</summary>
/// <param name="SourceDirectory">The canonical Squad source tree to render.</param>
/// <param name="Targets">The harness targets to render for.</param>
/// <param name="Scope">Whether the output is project-relative or global.</param>
/// <param name="UserScopeDirectory">The Kyber-Weave app-data directory, not a harness root.</param>
/// <param name="TranslationMode">The translation mode for lossy targets.</param>
/// <param name="GlobalRoots">
/// Resolves each target's real per-user global root; required only by renderers whose
/// global-scope output embeds that root.
/// </param>
/// <param name="ModelPresets">
/// Optional model presets to apply per target during rendering.
/// </param>
/// <param name="PreloadedSource">
/// Optional in-memory canonical source, avoiding disk reads when provided.
/// </param>
/// <param name="EnvironmentReader">
/// Optional environment variable reader delegate, avoiding process-level environment mutation.
/// </param>
/// <param name="Arbiter">
/// Optional Arbiter enforcement settings; hooks render only when enabled and project-scoped.
/// </param>
/// <param name="DiskInputs">
/// What a render reads from the Squad source tree and the user's Copilot config, read before the render by the host
/// so a renderer stays a pure function of its request. A request with no preloaded source and no inputs cannot render.
/// </param>
/// <param name="WorkerRoster">
/// The host's <c>worker-roster</c>, or null for the default. Under <c>tiered</c> a rendered agent's
/// <c>delegates-to</c> leaves out every agent that declares the <c>technology</c> roster, so the technology
/// developers are not offered to a run that cannot dispatch them. Any other value renders unchanged.
/// </param>
/// <param name="DocsServerCommand">
/// The absolute path of <c>kyber-weave-mcp</c> as PATH names it, or null when the host found none. A global render
/// whose target launches the documentation server by a command line (Antigravity) uses it as that command; a
/// project render and a null value use the bare name, because a machine path in a committed file breaks every
/// other clone.
/// </param>
/// <remarks>
/// <see cref="UserScopeDirectory"/> is the Kyber-Weave app-data directory, so it cannot tell
/// a renderer where a harness actually reads its global agents from (for Copilot that is
/// <c>$COPILOT_HOME</c> or <c>~/.copilot</c>). <see cref="GlobalRoots"/> carries that
/// resolver into the render so a target whose agent bodies cannot rely on file-relative link
/// anchoring (Copilot, at global scope) can emit absolute resource links. Every site that
/// renders for deployment or comparison (lifecycle install and update, and doctor's global
/// collision scan) must supply the same resolver, or install, receipt, and doctor would see
/// different bytes. It is null-tolerant for project scope and for targets that ignore it; a
/// Copilot global render without it fails closed rather than shipping relative links.
/// </remarks>
public sealed record SquadRenderRequest(
    string SourceDirectory,
    IReadOnlyList<SquadTarget> Targets,
    SquadDeploymentScope Scope,
    string? UserScopeDirectory = null,
    string TranslationMode = "best-effort",
    ISquadGlobalRootResolver? GlobalRoots = null,
    IReadOnlyDictionary<SquadTarget, SquadModelPreset>? ModelPresets = null,
    SquadSource? PreloadedSource = null,
    Func<string, string?>? EnvironmentReader = null,
    SquadArbiterWiring? Arbiter = null,
    SquadRenderDiskInputs? DiskInputs = null,
    string? WorkerRoster = null,
    string? DocsServerCommand = null)
{
    /// <summary>
    /// Convenience constructor applying a single <see cref="SquadModelPreset"/> across all requested targets.
    /// </summary>
    public SquadRenderRequest(
        string SourceDirectory,
        IReadOnlyList<SquadTarget> Targets,
        SquadDeploymentScope Scope,
        SquadModelPreset? ModelPresets)
        : this(
            SourceDirectory,
            Targets,
            Scope,
            null,
            "best-effort",
            null,
            ModelPresets is null ? null : Targets.ToDictionary(static target => target, _ => ModelPresets),
            null,
            null,
            null,
            null)
    {
    }
}

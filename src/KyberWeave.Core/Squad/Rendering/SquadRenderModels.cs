using JetBrains.Annotations;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Defines the contract for lowering canonical Squad source into a coding harness's
/// native agent and skill files.
/// </summary>
/// <remarks>
/// This is the seam that used to be filled by shelling out to the upstream Agent Package
/// Manager (<c>apm compile</c>). No released APM version exposes the machine-readable
/// output this pipeline needs — 0.28.0 has no <c>--format json</c> mode at all, and three
/// of the ten approved targets (<c>kilo</c>, <c>warp</c>, <c>factory</c>) are absent from
/// its target matrix — so rendering is native Kyber-Weave code from here on.
/// </remarks>
public interface ISquadRenderer
{
    /// <summary>The targets this renderer can produce native output for.</summary>
    IReadOnlyCollection<SquadTarget> SupportedTargets { get; }

    /// <summary>Renders canonical Squad source into harness-native deployment files.</summary>
    Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default);
}

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
    ISquadGlobalRootResolver? GlobalRoots = null);

/// <summary>The structured result of a Squad render operation.</summary>
public sealed record SquadRenderResult(
    bool Success,
    IReadOnlyList<SquadDeploymentFile> Files,
    IReadOnlyList<SquadDegradationRecord> Degradations,
    IReadOnlyList<SquadRenderWarning> Warnings,
    IReadOnlyList<string> Errors);

/// <summary>A structured record of an agent-to-role lowering or capability degradation.</summary>
public sealed record SquadDegradationRecord(
    string Target,
    string CanonicalIdentity,
    string OutputIdentity,
    string Code,
    string InstructionDigest,
    string? Details = null);

/// <summary>A diagnostic warning emitted during Squad rendering.</summary>
public sealed record SquadRenderWarning(
    string Code,
    string Message,
    string? Target = null);

/// <summary>Raised when render output violates safety, integrity, or coverage invariants.</summary>
public sealed class SquadRenderValidationException : InvalidOperationException
{
    [UsedImplicitly]
    public SquadRenderValidationException()
    {
    }

    public SquadRenderValidationException(string message)
        : base(message)
    {
    }

    public SquadRenderValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

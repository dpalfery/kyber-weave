using System.Text.Json.Nodes;
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
public sealed record SquadRenderRequest(
    string SourceDirectory,
    IReadOnlyList<SquadTarget> Targets,
    SquadDeploymentScope Scope,
    string? UserScopeDirectory = null,
    string TranslationMode = "best-effort",
    SquadArbiterWiring? Arbiter = null);

/// <summary>The host's Arbiter enforcement settings carried into a render.</summary>
/// <remarks>
/// Carried as render parameters rather than read from host configuration at render time so
/// a render stays a pure function of its request, and so a request that omits the field
/// renders byte for byte as it did before the field existed. Hook production keys off
/// <see cref="ArbiterHookWiring"/>: hooks are produced only when <see cref="Enabled"/> is
/// true and the scope is <see cref="SquadDeploymentScope.Project"/> (Req 22.4), and
/// <see cref="HookTimeoutSeconds"/> becomes every produced hook's command timeout.
/// </remarks>
public sealed record SquadArbiterWiring(bool Enabled, int HookTimeoutSeconds);

/// <summary>The structured result of a Squad render operation.</summary>
public sealed record SquadRenderResult(
    bool Success,
    IReadOnlyList<SquadDeploymentFile> Files,
    IReadOnlyList<SquadDegradationRecord> Degradations,
    IReadOnlyList<SquadRenderWarning> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<SquadRenderedBlock>? Blocks = null);

/// <summary>
/// One managed entry a renderer wants spliced into a shared hook file: the hook
/// container it belongs to and the entry payload.
/// </summary>
/// <remarks>
/// <see cref="Container"/> names the hook container case-insensitively —
/// <c>preToolUse</c>/<c>PreToolUse</c> for the pre container and
/// <c>postToolUse</c>/<c>PostToolUse</c> for the post container — because Cursor's
/// containers are camelCase while every other shared-file format's are PascalCase.
/// Anything else is a renderer bug and the deployment plan refuses it.
/// </remarks>
public sealed record SquadRenderedBlockEntry(string Container, JsonNode Entry);

/// <summary>
/// A block fragment a renderer emits for a shared hook file the user also owns: the
/// target, the portable path, the file's shape, and the managed entries to splice.
/// </summary>
/// <remarks>
/// Unlike <see cref="SquadDeploymentFile"/>, a block never claims the whole file —
/// <see cref="SquadDeploymentPlan"/> splices <see cref="Entries"/> into whatever the
/// user already has at <see cref="RelativePath"/> at plan time, so an existing user
/// file is content to merge with rather than an unmanaged collision.
/// </remarks>
public sealed record SquadRenderedBlock(
    string Target,
    string RelativePath,
    SquadHookBlockFormat Format,
    IReadOnlyList<SquadRenderedBlockEntry> Entries);

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

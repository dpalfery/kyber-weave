using System.Text.Json.Nodes;
using JetBrains.Annotations;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>What a render reads from disk, read ahead of the render and carried on the request.</summary>
/// <param name="SourceFailure">Why the canonical source could not be loaded, rethrown when a render needs the source.</param>
/// <param name="WorkerBaselineBlock">The worker baseline block, or null when the tree carries none.</param>
/// <param name="WorkerBaselineFailure">Why the worker baseline could not be read, rethrown when a render needs it.</param>
/// <param name="OperatorChannelOverlayText">The operator channel overlay file's text, or null when it could not be read.</param>
/// <param name="OperatorChannelOverlayFailure">Why the overlay could not be read, rethrown when a render needs it.</param>
/// <param name="CopilotMcpServerDeclared">Whether the user's Copilot MCP config declares the operator channel's server.</param>
public sealed record SquadRenderDiskInputs(
    Exception? SourceFailure = null,
    string? WorkerBaselineBlock = null,
    Exception? WorkerBaselineFailure = null,
    string? OperatorChannelOverlayText = null,
    Exception? OperatorChannelOverlayFailure = null,
    bool CopilotMcpServerDeclared = false);

/// <summary>The host's Arbiter enforcement settings carried into a render.</summary>
/// <remarks>
/// Carried as render parameters rather than read from host configuration at render time so
/// a render stays a pure function of its request, and so a request that omits the field
/// renders byte for byte as it did before the field existed. Hook production keys off
/// <c>ArbiterHookWiring</c>: hooks are produced only when <see cref="Enabled"/> is
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
/// <c>SubagentStop</c> is the third container, and only Factory's hook file holds it.
/// Anything else is a renderer bug and the deployment plan refuses it.
/// </remarks>
public sealed record SquadRenderedBlockEntry(string Container, JsonNode Entry);

/// <summary>
/// A block fragment a renderer emits for a shared hook file the user also owns: the
/// target, the portable path, the file's shape, and the managed entries to splice.
/// </summary>
/// <remarks>
/// Unlike <see cref="SquadDeploymentFile"/>, a block never claims the whole file —
/// <c>SquadDeploymentPlan</c> splices <see cref="Entries"/> into whatever the
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

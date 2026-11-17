using System.Text.Json.Nodes;
using JetBrains.Annotations;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

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
    string? WorkerRoster = null)
{
    /// <summary>Gets the canonical source this render starts from, or throws why it is not available.</summary>
    /// <returns>The preloaded source.</returns>
    /// <exception cref="SquadRenderValidationException">The request was never prepared with the source.</exception>
    public SquadSource RequireSource() =>
        PreloadedSource ?? throw (DiskInputs?.SourceFailure ?? Unprepared("the canonical source"));

    /// <summary>Gets the worker baseline block, or throws why it could not be read.</summary>
    /// <returns>The block, or null when the tree carries no baseline.</returns>
    /// <exception cref="SquadRenderValidationException">The request was never prepared, or the baseline is malformed.</exception>
    public string? RequireWorkerBaselineBlock()
    {
        SquadRenderDiskInputs inputs = RequireDiskInputs();
        return inputs.WorkerBaselineFailure is { } failure ? throw failure : inputs.WorkerBaselineBlock;
    }

    /// <summary>Gets the reader of environment variables this render consults, or throws that the request was never prepared.</summary>
    /// <returns>The reader.</returns>
    /// <exception cref="SquadRenderValidationException">The request carries no reader.</exception>
    public Func<string, string?> RequireEnvironmentReader() =>
        EnvironmentReader ?? throw Unprepared("an environment reader");

    /// <summary>Gets what the host read from disk ahead of the render, or throws that the request was never prepared.</summary>
    /// <returns>The disk inputs.</returns>
    /// <exception cref="SquadRenderValidationException">The request was never prepared.</exception>
    public SquadRenderDiskInputs RequireDiskInputs() => DiskInputs ?? throw Unprepared("what the render reads from disk");

    private static SquadRenderValidationException Unprepared(string what) =>
        new($"The render request does not carry {what}: prepare it with the render input reader before rendering.");

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

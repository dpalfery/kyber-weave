using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>Defines the contract for executing APM compiler and pack operations.</summary>
public interface IApmRunner
{
    /// <summary>Renders a Squad source tree for the requested targets in an isolated staging root.</summary>
    Task<ApmRenderResult> RenderAsync(ApmRenderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Packs Squad distribution archives according to the specified format.</summary>
    Task<ApmPackResult> PackAsync(ApmPackRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Parameters for an APM render execution.</summary>
public sealed record ApmRenderRequest(
    string SourceDirectory,
    IReadOnlyList<SquadTarget> Targets,
    SquadDeploymentScope Scope,
    string? UserScopeDirectory = null,
    string TranslationMode = "best-effort");

/// <summary>The structured result of an APM render operation.</summary>
public sealed record ApmRenderResult(
    bool Success,
    IReadOnlyList<SquadDeploymentFile> Files,
    IReadOnlyList<ApmDegradationRecord> Degradations,
    IReadOnlyList<ApmWarning> Warnings,
    IReadOnlyList<string> Errors);

/// <summary>A structured record of an agent-to-role lowering or capability degradation.</summary>
public sealed record ApmDegradationRecord(
    string Target,
    string CanonicalIdentity,
    string OutputIdentity,
    string Code,
    string InstructionDigest,
    string? Details = null);

/// <summary>A diagnostic warning emitted during APM rendering or packing.</summary>
public sealed record ApmWarning(
    string Code,
    string Message,
    string? Target = null);

/// <summary>Output format options for <c>apm pack</c>.</summary>
public enum ApmPackFormat
{
    /// <summary>Standard APM distribution package containing all agents, skills, and MCP configuration.</summary>
    Apm,

    /// <summary>Agent Plugins v1.0.0 package containing portable skills and MCP servers only.</summary>
    Plugins,

    /// <summary>Both APM and Agent Plugins packages.</summary>
    All
}

/// <summary>Parameters for an APM pack execution.</summary>
public sealed record ApmPackRequest(
    string SourceDirectory,
    ApmPackFormat Format,
    string OutputDirectory,
    string Version);

/// <summary>The structured result of an APM pack operation.</summary>
public sealed record ApmPackResult(
    bool Success,
    IReadOnlyList<string> CreatedArchives,
    IReadOnlyList<string> Errors,
    IReadOnlyList<ApmWarning> Warnings,
    string? PluginManifestJson = null);

/// <summary>The result of validating and compiling a Squad source tree.</summary>
public sealed record SquadApmCompilationResult(
    SquadSource Source,
    IReadOnlyList<SquadDeploymentFile> RenderedFiles,
    IReadOnlyList<SquadDegradation> Degradations,
    IReadOnlyList<ApmDegradationRecord> StructuredDegradations);

/// <summary>Raised when APM structured output violates safety, integrity, or schema rules.</summary>
public sealed class SquadApmValidationException : InvalidOperationException
{
    public SquadApmValidationException()
    {
    }

    public SquadApmValidationException(string message)
        : base(message)
    {
    }

    public SquadApmValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

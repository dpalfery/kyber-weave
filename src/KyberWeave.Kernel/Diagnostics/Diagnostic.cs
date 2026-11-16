namespace KyberWeave.Core.Diagnostics;

/// <summary>
/// A single finding from a validation, lint, or security rule. The <see cref="Code"/>
/// is a stable identifier (e.g. KW-SKILL-SPEC-001) suitable for suppression and SARIF.
/// </summary>
/// <param name="Code">Stable rule identifier, e.g. KW-SKILL-SPEC-001 or KW-DOC-DRIFT-001.</param>
/// <param name="Severity">How severe the finding is.</param>
/// <param name="Message">One-sentence statement of the finding.</param>
/// <param name="Subject">
/// What the finding is about: a skill name, an agent role, or a document id. Named
/// generically because the diagnostic model is shared across artifact classes.
/// </param>
/// <param name="FilePath">Path of the file the finding is in, when known.</param>
/// <param name="Hint">Optional remediation hint.</param>
/// <param name="StartLine">One-based first line of the finding, when known.</param>
/// <param name="EndLine">One-based last line of the finding, when known.</param>
/// <param name="RelatedLocations">Other locations that contribute to the finding.</param>
public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    string Subject,
    string? FilePath = null,
    string? Hint = null,
    int? StartLine = null,
    int? EndLine = null,
    IReadOnlyList<DiagnosticLocation>? RelatedLocations = null)
{
    /// <summary>Alias for <see cref="FilePath"/> used by harness sync contracts.</summary>
    public string? Location => FilePath;

    /// <summary>Other locations that contribute to the finding.</summary>
    public IReadOnlyList<DiagnosticLocation> RelatedLocations { get; } = RelatedLocations ?? [];

    public override string ToString() => $"[{Code}] {Severity}: {Message}";
}

/// <summary>A line-addressable location related to a diagnostic.</summary>
/// <param name="FilePath">Path of the related file.</param>
/// <param name="StartLine">One-based first line of the related evidence, when known.</param>
/// <param name="EndLine">One-based last line of the related evidence, when known.</param>
/// <param name="Message">Optional explanation of how the location is related.</param>
public sealed record DiagnosticLocation(
    string FilePath,
    int? StartLine = null,
    int? EndLine = null,
    string? Message = null);

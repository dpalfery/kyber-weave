using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>What <c>PlanTaskFileGenerator.Run</c> does with the file it derives.</summary>
public enum PlanTaskFileMode
{
    /// <summary>Derive the file and report it; touch nothing.</summary>
    Preview,

    /// <summary>Derive the file and write it beside the plan, unless it is already exactly that.</summary>
    Write,

    /// <summary>Derive the file and report every way the one on disk differs; touch nothing.</summary>
    Check,

    /// <summary>Rewrite a legacy JSON task file as YAML in the same place, and remove the JSON file.</summary>
    Convert
}

/// <summary>The result of one <c>PlanTaskFileGenerator.Run</c>.</summary>
/// <param name="Report">The findings: gaps in the plan, differences found by a check, or problems writing.</param>
/// <param name="TaskFilePath">The repository-relative path of the task file beside the plan.</param>
/// <param name="Json">The derived file's text; null when the plan has a gap that stops it being derived.</param>
/// <param name="ExecutionDigest">The digest recorded in the derived file; null when none was derived.</param>
/// <param name="TaskCount">How many tasks the derived file carries.</param>
/// <param name="Wrote">True when this run changed the file on disk.</param>
/// <param name="File">The derived file; null when none was derived.</param>
/// <param name="DifficultyChecks">
/// One line per task on a write: what the decision model made of the architect's rating, or that it was not checked.
/// </param>
public sealed record PlanTaskFileOutcome(
    DiagnosticReport Report,
    string TaskFilePath,
    string? Json,
    string? ExecutionDigest,
    int TaskCount,
    bool Wrote,
    PlanTaskFile? File = null,
    IReadOnlyList<string>? DifficultyChecks = null);

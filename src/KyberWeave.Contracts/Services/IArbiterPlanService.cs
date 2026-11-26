using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Contracts.Services;

/// <summary>One plan file, parsed, with the execution digest a task file records.</summary>
/// <param name="ResolvedPath">The absolute path the plan was read from.</param>
/// <param name="Document">What the parser understood.</param>
/// <param name="Digest">The execution digest, or null when the parse dropped content or found no task.</param>
public sealed record ArbiterPlanRead(string ResolvedPath, PlanDocument Document, string? Digest);

/// <summary>Reads one plan file and parses it for the <c>arbiter plan</c> listing.</summary>
public interface IArbiterPlanService
{
    /// <summary>The plan file is missing or unreadable.</summary>
    public const string PlanNotReadable = "KW-ARB-PARSE-005";

    /// <summary>A plan with no Tasks section, or no task that parsed: downstream readiness answers <c>no-tasks</c>.</summary>
    public const string NoTasks = "KW-ARB-PARSE-004";

    /// <summary>
    /// Reads the plan at <paramref name="relative"/> under <paramref name="root"/> and parses it, adding the parse
    /// findings and the no-task finding to <paramref name="report"/>. The digest is withheld when the parse dropped
    /// content (<c>KW-ARB-PARSE-001</c>), since a digest over it would describe a plan that does not say what it says.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="relative">The plan's repository-relative path, as the argument was resolved.</param>
    /// <param name="argument">What the caller typed, for the finding's subject.</param>
    /// <param name="report">Receives the findings.</param>
    /// <returns>The plan, or null when it could not be read; the findings say why.</returns>
    ArbiterPlanRead? TryRead(string root, string relative, string argument, DiagnosticReport report);
}

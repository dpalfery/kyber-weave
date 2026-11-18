using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Contracts.Services;

/// <summary>How one <c>arbiter doctor</c> line is marked.</summary>
public enum ArbiterDoctorStatus
{
    /// <summary>The check passed.</summary>
    Ok,

    /// <summary>The check found something the operator should fix.</summary>
    Warn,

    /// <summary>Information only: nothing is wrong, so there is nothing to suppress.</summary>
    Info,
}

/// <summary>One line of the doctor's check output, formatted by the host.</summary>
/// <param name="Status">The mark the line carries.</param>
/// <param name="Subject">The thing checked, such as <c>Key</c>, or null for a line with no subject.</param>
/// <param name="RuleId">The finding's rule id, or null when the line carries none.</param>
/// <param name="Message">What the check found, with no markup.</param>
public sealed record ArbiterDoctorLine(
    ArbiterDoctorStatus Status,
    string? Subject,
    string? RuleId,
    string Message);

/// <summary>One target in the global scope, with the reason its hook is enforced or degraded.</summary>
/// <param name="Label">The harness name the operator knows it by.</param>
/// <param name="Reason">The reason the global wiring gives, such as <c>enforced-global</c>.</param>
public sealed record ArbiterDoctorScopeTarget(string Label, string Reason);

/// <summary>What one <c>arbiter doctor</c> run found.</summary>
/// <param name="ArbiterEnabled">Whether the project enables the Arbiter.</param>
/// <param name="GlobalTargets">The global scope's targets, in registry order.</param>
/// <param name="Gating">The per-harness gating rows the host prints for the project scope.</param>
/// <param name="Lines">The check lines, in the order they are printed.</param>
public sealed record ArbiterDoctorReport(
    bool ArbiterEnabled,
    IReadOnlyList<ArbiterDoctorScopeTarget> GlobalTargets,
    HarnessGatingReport Gating,
    IReadOnlyList<ArbiterDoctorLine> Lines);

/// <summary>Diagnoses the host's Arbiter installation without writing anything.</summary>
public interface IArbiterDoctorService
{
    /// <summary><c>artifacts/arbiter/</c> is not ignored by git.</summary>
    public const string LogNotIgnored = "KW-ARB-LOG-001";

    /// <summary><c>kyber-weave-arbiter</c> is missing from <c>PATH</c>, or its version differs from the CLI's.</summary>
    public const string BinaryMissing = "KW-ARB-BIN-001";

    /// <summary>No plan, spec or todo index is declared, so the Read guard protects nothing.</summary>
    public const string GuardWithoutIndex = "KW-ARB-GUARD-001";

    /// <summary>
    /// Model targeting is on for a hooked target whose model targets file is missing, unreadable, or not
    /// the table the last Squad update wrote. Without a current file the model override is dropped without
    /// anyone seeing it.
    /// </summary>
    public const string ModelTargetsMissing = "KW-ARB-MODEL-001";

    /// <summary>
    /// The project enables Arbiter, and an installed harness is ungated for it: no project hook gates its
    /// dispatches and no live user-level hook does. The finding names the harness and carries its fix.
    /// </summary>
    public const string ProjectUngated = "KW-ARB-PROJECT-001";

    /// <summary>
    /// The global Squad receipt holds an Arbiter hook for a harness that <c>arbiter.global.targets</c> does
    /// not list, so <c>kyber-weave update</c> never refreshes that hook.
    /// </summary>
    public const string GlobalUnlisted = "KW-ARB-GLOBAL-001";

    /// <summary>
    /// Runs every check and reports findings. A malformed override still leaves the product defaults to diagnose, so
    /// one bad file never hides the remaining checks. Writes nothing.
    /// </summary>
    /// <param name="repositoryPath">The repository path the command was pointed at.</param>
    /// <param name="configFile">The <c>--config</c> path, or null.</param>
    /// <param name="report">Receives the findings.</param>
    /// <returns>What the run found, for the host to print.</returns>
    ArbiterDoctorReport Inspect(string repositoryPath, string? configFile, DiagnosticReport report);
}

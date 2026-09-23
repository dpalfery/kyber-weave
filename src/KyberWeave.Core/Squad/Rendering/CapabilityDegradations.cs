using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Shared degradation record builder for the capability-not-isolable pattern: when a
/// harness grants a shell-class tool (Bash, Powershell, Execute, run_command, etc.),
/// write access becomes reachable through redirection even when write-class tools are
/// withheld. This gap appears in multiple renderers (Claude, Pi, ZCode, Factory,
/// OpenCode, Antigravity) and is recorded via this shared helper to ensure consistent
/// wording and structure across targets.
/// </summary>
/// <remarks>
/// D10: This degradation is recorded when <c>process.execute: allow</c> and
/// <c>filesystem.write: ask</c> or <c>deny</c>. The narrowing of write-class tools
/// is still applied (no <c>Edit</c>, <c>Write</c>, etc., in the tools list), but
/// this degradation explains why that narrowing is incomplete: the shell can reach
/// write through I/O redirection (e.g., `cmd > file`, `echo text >> file`).
/// </remarks>
internal static class CapabilityDegradations
{
    /// <summary>
    /// Determines whether a capability-not-isolable degradation should be recorded when
    /// <c>process.execute: allow</c> coexists with <c>filesystem.write: ask</c> or <c>deny</c>,
    /// meaning write access remains reachable through shell redirection despite narrowing
    /// the write-class tool list.
    /// </summary>
    /// <param name="targetToken">The target token (e.g., "antigravity", "claude").</param>
    /// <param name="canonicalIdentity">The canonical agent identity.</param>
    /// <param name="outputIdentity">The rendered output identity (usually same as canonical).</param>
    /// <param name="instructionDigest">The agent's body digest.</param>
    /// <param name="executeDecision">The <c>process.execute</c> permission decision from the capability profile.</param>
    /// <param name="writeDecision">The <c>filesystem.write</c> permission decision from the capability profile.</param>
    /// <param name="grantedShellTools">
    /// The names of shell-class tools the target emits when process.execute is allowed
    /// (e.g., ["run_command"] for Antigravity, ["Bash", "PowerShell"] for Claude).
    /// </param>
    /// <param name="withheldWriteTools">
    /// The names of write-class tools the target withholds when write is not allowed
    /// (e.g., ["write_to_file", "replace_file_content"] for Antigravity,
    /// ["Edit", "Write"] for Claude).
    /// </param>
    /// <returns>
    /// A degradation record with code "capability-not-isolable" naming both tool sets,
    /// or <c>null</c> if the gap does not apply (write is allowed, or execute is not allowed).
    /// </returns>
    public static SquadDegradationRecord? BuildCapabilityNotIsolable(
        string targetToken,
        string canonicalIdentity,
        string outputIdentity,
        string instructionDigest,
        SquadPermissionDecision executeDecision,
        SquadPermissionDecision writeDecision,
        IReadOnlyList<string> grantedShellTools,
        IReadOnlyList<string> withheldWriteTools)
    {
        // The gap applies only when execute is allow and write is NOT allow
        if (executeDecision != SquadPermissionDecision.Allow || writeDecision == SquadPermissionDecision.Allow)
        {
            return null;
        }

        // No gap if there are no shell tools to grant
        if (grantedShellTools.Count == 0)
        {
            return null;
        }

        // The gap exists: shell is allowed but write is denied or ask.
        string shellTools = string.Join(", ", grantedShellTools.Order(StringComparer.Ordinal));
        string writeTools = string.Join(", ", withheldWriteTools.Order(StringComparer.Ordinal));

        return new SquadDegradationRecord(
            Target: targetToken,
            CanonicalIdentity: canonicalIdentity,
            OutputIdentity: outputIdentity,
            Code: "capability-not-isolable",
            InstructionDigest: instructionDigest,
            Details: $"Capability profile grants process.execute ({shellTools}) but sets filesystem.write to " +
                $"'{DescribeDecision(writeDecision)}'. " +
                $"Write access remains reachable through shell redirection despite withholding write tools ({writeTools}).");
    }

    private static string DescribeDecision(SquadPermissionDecision decision) => decision switch
    {
        SquadPermissionDecision.Allow => "allow",
        SquadPermissionDecision.Ask => "ask",
        SquadPermissionDecision.Deny => "deny",
        _ => "deny"
    };
}

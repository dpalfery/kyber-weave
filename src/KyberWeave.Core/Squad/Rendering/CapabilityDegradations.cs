using System.Globalization;
using System.Text;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Shared helper for recording degradation when process.execute is allowed but filesystem.write
/// is denied or ask, creating a capability-isolation breach.
/// </summary>
internal static class CapabilityDegradations
{
    /// <summary>
    /// Builds a <c>capability-not-isolable</c> degradation record when process.execute is allow
    /// and filesystem.write is ask or deny, naming the granted shell tools and withheld write tools.
    /// </summary>
    /// <remarks>
    /// A granted shell tool can write files through redirection (e.g., <c>bash > file.txt</c> or
    /// <c>output=$(command)</c>), so withholding specific named write tools does not isolate the
    /// write capability when the shell is granted. This degradation is structural and present across
    /// all six renderers (Claude, Pi, ZCode, Factory, OpenCode, Antigravity) because the shell
    /// behavior is constant — the tool names differ by renderer, but the logic is shared here.
    /// </remarks>
    /// <param name="targetToken">The rendering target token (e.g., "claude", "pi", "factory").</param>
    /// <param name="canonicalIdentity">The canonical agent name.</param>
    /// <param name="outputIdentity">The agent name in the rendered output.</param>
    /// <param name="instructionDigest">The instruction body digest.</param>
    /// <param name="executeDecision">The process.execute permission decision.</param>
    /// <param name="writeDecision">The filesystem.write permission decision.</param>
    /// <param name="grantedShellTools">The set of shell/execute tools granted to the agent.</param>
    /// <param name="withheldWriteTools">The set of write tools withheld from the agent.</param>
    /// <returns>
    /// A degradation record when executeDecision is Allow AND writeDecision is Ask or Deny
    /// AND at least one shell tool is granted; null otherwise.
    /// </returns>
    internal static SquadDegradationRecord? BuildCapabilityNotIsolable(
        string targetToken,
        string canonicalIdentity,
        string outputIdentity,
        string instructionDigest,
        SquadPermissionDecision executeDecision,
        SquadPermissionDecision writeDecision,
        string[] grantedShellTools,
        string[] withheldWriteTools)
    {
        ArgumentNullException.ThrowIfNull(targetToken);
        ArgumentNullException.ThrowIfNull(canonicalIdentity);
        ArgumentNullException.ThrowIfNull(outputIdentity);
        ArgumentNullException.ThrowIfNull(instructionDigest);
        ArgumentNullException.ThrowIfNull(grantedShellTools);
        ArgumentNullException.ThrowIfNull(withheldWriteTools);

        // XOR pattern: execute must be allow, write must be ask or deny, and at least one shell tool granted
        if (executeDecision != SquadPermissionDecision.Allow)
        {
            return null;
        }

        if (writeDecision == SquadPermissionDecision.Allow)
        {
            return null;
        }

        if (grantedShellTools.Length == 0)
        {
            return null;
        }

        StringBuilder details = new StringBuilder();

        // Name the target
        details.Append(CultureInfo.InvariantCulture, $"Target '{targetToken}': ");

        // Name all granted shell tools (in stable order)
        string[] sortedShellTools = grantedShellTools.OrderBy(tool => tool, StringComparer.Ordinal).ToArray();
        details.Append(CultureInfo.InvariantCulture, $"granted shell tool{(sortedShellTools.Length > 1 ? "s" : "")} ");
        details.Append(string.Join(", ", sortedShellTools));

        // Name all withheld write tools (in stable order)
        if (withheldWriteTools.Length > 0)
        {
            string[] sortedWriteTools = withheldWriteTools.OrderBy(tool => tool, StringComparer.Ordinal).ToArray();
            details.Append(CultureInfo.InvariantCulture, $"; withheld write tool{(sortedWriteTools.Length > 1 ? "s" : "")} ");
            details.Append(string.Join(", ", sortedWriteTools));
        }

        details.Append('.');

        return new SquadDegradationRecord(
            Target: targetToken,
            CanonicalIdentity: canonicalIdentity,
            OutputIdentity: outputIdentity,
            Code: "capability-not-isolable",
            InstructionDigest: instructionDigest,
            Details: details.ToString());
    }
}

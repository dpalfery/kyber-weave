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
/// <para>
/// D10: This degradation is recorded when <c>process.execute: allow</c> and
/// <c>filesystem.write: ask</c> or <c>deny</c>. The narrowing of write-class tools
/// is still applied (no <c>Edit</c>, <c>Write</c>, etc., in the tools list), but
/// this degradation explains why that narrowing is incomplete: the shell can reach
/// write through I/O redirection (e.g., `cmd > file`, `echo text >> file`).
/// </para>
/// <para>
/// This class is also the shared home of the MCP entitlement rule every renderer applies
/// (<see cref="GrantsUniversalMcpServers"/> and <see cref="GrantsOrchestratorExcludedMcpServers"/>).
/// The rule answers a per-agent capability question the same way the record builders beside it
/// do, and it was previously copied into each renderer byte-for-byte; one copy per target is
/// what lets a target silently drift from the others. See ADR 0049.
/// </para>
/// </remarks>
internal static class CapabilityDegradations
{
    /// <summary>
    /// The capability profile whose holder routes work rather than researching it. Declared by id
    /// because the exemption belongs to the role, not to a permission the profile happens to
    /// carry — and declared here so the renderers that apply the carve-out cannot disagree about
    /// which profile id it names.
    /// </summary>
    public const string PureOrchestratorProfile = "orchestrator";

    /// <summary>
    /// The capability an MCP entitlement rests on: a role allowed to read the filesystem is allowed
    /// to consult a read-only MCP server.
    /// </summary>
    public const string FileReadCapability = "filesystem.read";

    /// <summary>
    /// Whether a capability profile is entitled to an MCP server that every agent needs, the pure
    /// orchestrator included: the profile resolves and allows <see cref="FileReadCapability"/>.
    /// </summary>
    /// <remarks>
    /// <c>kyber-weave</c> is the server this predicate exists for. It carries
    /// <c>agent_coding_standards</c>, the tool that hands an agent the standards it is held to,
    /// and the role that assigns work is the role deciding which standards apply to whom — so
    /// withholding it from the orchestrator withheld it from the one agent whose job depends on
    /// it (ADR 0049). Every tool on that server is read-only, so the grant adds no write, no
    /// execution, and no network reach.
    /// <para>
    /// The profile is named rather than read off <see cref="SquadAgent.CapabilityProfile"/> because
    /// a target may lower an agent from an envelope profile of its own (Devin, Copilot); the
    /// entitlement follows the profile the harness actually sees.
    /// </para>
    /// </remarks>
    /// <param name="profileName">The capability profile the target resolves the agent from.</param>
    /// <param name="capabilityProfiles">The loaded capability profiles.</param>
    public static bool GrantsUniversalMcpServers(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        // An unresolvable profile allows nothing. Reading a missing profile as permission would
        // turn a source error into silent widening, so the rule stays fail-closed. The name can be
        // absent rather than merely unknown — a hand-built or partially deserialised agent carries
        // no profile id — and a dictionary lookup rejects a null key by throwing, which would turn
        // the documented fail-closed answer into an exception on the caller's render path.
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return false;
        }

        return capabilityProfiles.TryGetValue(profileName, out SquadCapabilityProfile? profile) &&
               profile.Permissions.TryGetValue(FileReadCapability, out SquadPermissionDecision decision) &&
               decision == SquadPermissionDecision.Allow;
    }

    /// <summary>
    /// Whether a capability profile is entitled to an MCP server that keeps the pure-orchestrator
    /// carve-out: the same read entitlement as <see cref="GrantsUniversalMcpServers"/>, minus
    /// <see cref="PureOrchestratorProfile"/>.
    /// </summary>
    /// <remarks>
    /// <c>codegraph</c> and <c>context7</c> are the servers this predicate exists for. A role that
    /// only routes work has no use for code-graph or library lookups, and granting them would hand
    /// it a research surface its own profile is not written for (ADR 0049 decision 2).
    /// <para>
    /// The two rules differ only by the orchestrator clause, so they are two named predicates
    /// rather than one predicate with a boolean parameter: a call site then states which class of
    /// server it is gating, and a reader does not have to resolve an argument to know that.
    /// </para>
    /// </remarks>
    /// <param name="profileName">The capability profile the target resolves the agent from.</param>
    /// <param name="capabilityProfiles">The loaded capability profiles.</param>
    public static bool GrantsOrchestratorExcludedMcpServers(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        return !string.Equals(profileName, PureOrchestratorProfile, StringComparison.Ordinal) &&
               GrantsUniversalMcpServers(profileName, capabilityProfiles);
    }

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

    /// <summary>
    /// Reports every vocabulary capability's resolved decision (per the
    /// <c>permission-not-expressible</c> contract), defaulting an undeclared capability to
    /// <see cref="SquadPermissionDecision.Deny"/> the same way an absent permission entry
    /// behaves elsewhere in this pipeline.
    /// </summary>
    internal static string DescribeCapabilityDecisions(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary)
    {
        capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile);

        return string.Join(
            "; ",
            capabilityVocabulary.Select(capability =>
            {
                SquadPermissionDecision decision = profile is not null &&
                    profile.Permissions.TryGetValue(capability, out SquadPermissionDecision resolved)
                        ? resolved
                        : SquadPermissionDecision.Deny;
                return $"{capability}: {DescribeDecision(decision)}";
            }));
    }

    // The switch expression covers all three enum values (Allow, Ask, Deny). An unknown decision
    // is reachable only through an undefined cast, which the loader never produces in practice.
    // If the enum ever grows, an unknown value must fail at render time rather than be silently
    // misdescribed in a receipt, so the default arm throws to ensure correctness.
    private static string DescribeDecision(SquadPermissionDecision decision) => decision switch
    {
        SquadPermissionDecision.Allow => "allow",
        SquadPermissionDecision.Ask => "ask",
        SquadPermissionDecision.Deny => "deny",
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown permission decision.")
    };
}

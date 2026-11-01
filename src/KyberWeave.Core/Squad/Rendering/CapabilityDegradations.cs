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
/// (<see cref="GrantsUniversalMcpServers"/> and <see cref="GrantsOrchestratorExcludedMcpServers"/>)
/// and of the derivations built on it — which tool names each entitlement class carries
/// (<see cref="UniversalMcpToolNames"/>, <see cref="OrchestratorExcludedMcpToolNames"/>), which of
/// them one profile receives (<see cref="ResolveMcpToolNames"/>), and how a withholding is
/// recorded (<see cref="WithheldMcpServerNames"/>, <see cref="DescribeWithheldMcp"/>). The rule
/// answers a per-agent capability question the same way the record builders beside it do, and it
/// was previously copied into each renderer byte-for-byte; one copy per target is what lets a
/// target silently drift from the others.
/// </para>
/// <para>
/// The derivations are here for the same reason as the predicates, and the partition they share is
/// the part that matters: the server every agent needs is decided once, here, rather than at each
/// call site. A renderer that keeps its own copy of "which servers are which class" will answer a
/// future edit to that partition differently from its neighbour, and its degradation receipt will
/// then name a server the render still withholds or grants — invisibly, because a contract suite
/// reads the grant from source and can compare two renderers only where both render a principal.
/// See ADR 0050 decision 4.
/// </para>
/// </remarks>
internal static class CapabilityDegradations
{
    /// <summary>
    /// The MCP server every agent needs: granted on the file-read entitlement alone, the pure
    /// orchestrator included, and never withheld on a profile's identity. It carries
    /// <c>agent_coding_standards</c>, the tool that hands an agent the standards it is held to
    /// (ADR 0050).
    /// </summary>
    /// <remarks>
    /// Declared here rather than in each renderer because the server is one half of the partition
    /// the members below answer for. A target that needs the name for a question of its own keeps
    /// its own spelling — <c>AntigravityRenderer</c>'s declaration probe,
    /// <c>OpenCodeRenderer</c>'s <c>kyber-weave_*</c> permission key — because those ask where the
    /// server's configuration lives or how a harness scopes it, not which entitlement class it
    /// belongs to.
    /// </remarks>
    public const string UniversalMcpServer = "kyber-weave";

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
    /// it (ADR 0050). Every tool on that server is read-only, so the grant adds no write, no
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
    /// it a research surface its own profile is not written for (ADR 0050 decision 2).
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
    /// The qualified <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> names of the servers that keep the
    /// pure-orchestrator carve-out, in server-then-tool order.
    /// </summary>
    /// <remarks>
    /// Two named members rather than one member with a filter argument, for the reason the two
    /// predicates above are two named methods: a call site then states which class of server it is
    /// asking for, and neither call site decides on its own what belongs to the class. That
    /// decision is the partition, and it is made here once (ADR 0050 decision 4).
    /// </remarks>
    /// <param name="requiredMcpTools">The toolchain's declared <c>required-mcp-tools</c> roster.</param>
    public static IReadOnlyList<string> OrchestratorExcludedMcpToolNames(
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredMcpTools)
    {
        return QualifiedMcpToolNames(requiredMcpTools, server => !IsUniversalMcpServer(server));
    }

    /// <summary>
    /// The qualified <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> names of
    /// <see cref="UniversalMcpServer"/>, in server-then-tool order.
    /// </summary>
    /// <param name="requiredMcpTools">The toolchain's declared <c>required-mcp-tools</c> roster.</param>
    public static IReadOnlyList<string> UniversalMcpToolNames(
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredMcpTools)
    {
        return QualifiedMcpToolNames(requiredMcpTools, IsUniversalMcpServer);
    }

    /// <summary>
    /// The MCP tool names one principal's entitlement carries: the orchestrator-excluded servers
    /// first, then the universal server, each in the order the roster declares them.
    /// </summary>
    /// <remarks>
    /// The two name lists are passed in already split and ordered, so this answers only which of
    /// them the profile is entitled to and never re-derives the partition. Appending the universal
    /// class last is what keeps the emitted tool list byte-stable: the groups are fixed by
    /// entitlement class even though the names themselves come from source.
    /// </remarks>
    /// <param name="profileName">The capability profile the target resolves the principal from.</param>
    /// <param name="capabilityProfiles">The loaded capability profiles.</param>
    /// <param name="orchestratorExcludedMcpToolNames">
    /// The qualified names of the servers that keep the carve-out,
    /// from <see cref="OrchestratorExcludedMcpToolNames"/>.
    /// </param>
    /// <param name="universalMcpToolNames">
    /// The qualified names of <see cref="UniversalMcpServer"/>,
    /// from <see cref="UniversalMcpToolNames"/>.
    /// </param>
    public static IReadOnlyList<string> ResolveMcpToolNames(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> orchestratorExcludedMcpToolNames,
        IReadOnlyList<string> universalMcpToolNames)
    {
        return
        [
            .. GrantsOrchestratorExcludedMcpServers(profileName, capabilityProfiles)
                ? orchestratorExcludedMcpToolNames
                : [],
            .. GrantsUniversalMcpServers(profileName, capabilityProfiles)
                ? universalMcpToolNames
                : []
        ];
    }

    /// <summary>
    /// The declared servers a principal is withheld from: every server whose own entitlement its
    /// profile does not satisfy.
    /// </summary>
    /// <remarks>
    /// Computed per server rather than as one "granted any MCP" boolean, because the two classes
    /// answer different questions. A record built from a single answer would name the universal
    /// server as withheld from the orchestrator after granting it — the one place a review or a
    /// <c>squad doctor</c> run would read a claim the render contradicts.
    /// </remarks>
    /// <param name="profileName">The capability profile the target resolves the principal from.</param>
    /// <param name="capabilityProfiles">The loaded capability profiles.</param>
    /// <param name="declaredMcpServerNames">
    /// The roster's server names, from <see cref="DeclaredMcpServerNames"/>.
    /// </param>
    public static IReadOnlyList<string> WithheldMcpServerNames(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> declaredMcpServerNames)
    {
        bool grantsUniversal = GrantsUniversalMcpServers(profileName, capabilityProfiles);
        bool grantsOrchestratorExcluded = GrantsOrchestratorExcludedMcpServers(profileName, capabilityProfiles);

        return
        [
            .. declaredMcpServerNames.Where(server =>
                IsUniversalMcpServer(server) ? !grantsUniversal : !grantsOrchestratorExcluded)
        ];
    }

    /// <summary>
    /// Why a principal did not receive the declared MCP tools, naming exactly the servers it lost
    /// and the rule that lost them.
    /// </summary>
    /// <remarks>
    /// The reason is selected rather than hedged: the two classes are withheld for different
    /// causes, and a sentence offering both would be read as both. The withheld list never names a
    /// granted server either, which is why the explanation speaks of "a server every agent needs"
    /// rather than repeating the server name: the record and the rendered tool list have to agree.
    /// </remarks>
    /// <param name="profileName">The capability profile the target resolves the principal from.</param>
    /// <param name="withheldServerNames">The servers this principal is withheld from.</param>
    /// <param name="capabilityProfiles">The loaded capability profiles.</param>
    public static string DescribeWithheldMcp(
        string profileName,
        IReadOnlyList<string> withheldServerNames,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        string reason = GrantsUniversalMcpServers(profileName, capabilityProfiles)
            ? $"capability profile '{profileName}' is the pure orchestrator profile, which routes " +
              "work rather than researching it, and the carve-out it keeps reaches only the " +
              "code-graph and library servers; a server every agent needs is granted on the " +
              "file-read entitlement alone and is never withheld on the profile's identity"
            : $"capability profile '{profileName}' does not allow 'filesystem.read', so no " +
              "declared MCP server is granted";

        return $"Declared MCP server(s) {string.Join(", ", withheldServerNames)} are withheld: " +
               reason + ".";
    }

    /// <summary>
    /// The declared MCP server names, in ordinal order, for a record that names what was withheld.
    /// </summary>
    /// <param name="requiredMcpTools">The toolchain's declared <c>required-mcp-tools</c> roster.</param>
    public static IReadOnlyList<string> DeclaredMcpServerNames(
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredMcpTools)
    {
        return [.. requiredMcpTools.Keys.OrderBy(name => name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether a declared server name is the one every agent needs. The partition the two
    /// name-list members above are built on.
    /// </summary>
    private static bool IsUniversalMcpServer(string serverName)
    {
        return string.Equals(serverName, UniversalMcpServer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fully qualified names of the declared servers <paramref name="includeServer"/> admits,
    /// in a stable server-then-tool order.
    /// </summary>
    private static IReadOnlyList<string> QualifiedMcpToolNames(
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredMcpTools,
        Func<string, bool> includeServer)
    {
        ArgumentNullException.ThrowIfNull(includeServer);

        return
        [
            .. requiredMcpTools
                .Where(entry => includeServer(entry.Key))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SelectMany(entry => entry.Value
                    .OrderBy(tool => tool, StringComparer.Ordinal)
                    .Select(tool => $"mcp__{entry.Key}__{tool}"))
        ];
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

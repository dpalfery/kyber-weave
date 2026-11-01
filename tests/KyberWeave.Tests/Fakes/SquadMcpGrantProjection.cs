using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The MCP entitlement rule the squad renderers implement, computed from the loaded
/// <see cref="SquadSource"/> instead of read back from any renderer.
/// </summary>
/// <remarks>
/// Two rules, not one. <c>kyber-weave</c> is granted to every agent whose effective capability
/// profile allows <c>filesystem.read</c>, the pure orchestrator included: that server carries
/// <c>agent_coding_standards</c>, and a role that delegates work still has to hand its delegates
/// the standards they are held to. <c>codegraph</c> and <c>context7</c> keep the pure-orchestrator
/// carve-out, because a role that only routes work has no use for code-graph or library lookups.
///
/// A renderer never states this rule to a test. A suite that read the answer back from the
/// renderer could not disagree with it, so a renderer that dropped the orchestrator — or dropped
/// every agent — would satisfy its own expectation. This projection is the independent statement
/// that both the per-renderer suites and the cross-target parity guard compare against, so the
/// separate copies of the rule in production code have one place that can catch them drifting
/// apart.
///
/// The per-target vocabulary lives here too, and for the same reason. Each harness spells the
/// grant its own way, and several of those spellings are one character away from a value that
/// parses cleanly and matches nothing: Kilo's permission key keeps the hyphen in
/// <c>kyber-weave_*</c> because it sanitizes only characters outside <c>[a-zA-Z0-9_-]</c>, while
/// Pi's adapter namespace replaces that same hyphen with an underscore in
/// <c>mcp__kyber_weave__</c>. Stating both spellings here, as constants a test names, is what
/// lets a suite assert the wrong one is absent rather than merely that the right one appeared.
/// </remarks>
internal static class SquadMcpGrantProjection
{
    /// <summary>The server the owner widened to every read-entitled agent.</summary>
    internal const string KyberWeaveServer = "kyber-weave";

    /// <summary>
    /// The permission key Kilo gates the whole server on. Kilo sanitizes a server name only for
    /// characters outside <c>[a-zA-Z0-9_-]</c>, so the hyphen survives and this key matches
    /// <c>kyber-weave_docs_status</c>.
    /// </summary>
    internal const string KiloMcpPermissionKey = "kyber-weave_*";

    /// <summary>
    /// The misspelling Kilo would accept without complaint and match nothing with: <c>_</c> in
    /// place of the server name's <c>-</c>. Asserted absent, because a test that only checked for
    /// <see cref="KiloMcpPermissionKey"/> would still pass if this one were emitted beside it.
    /// </summary>
    internal const string KiloMcpPermissionKeyMisspelled = "kyber_weave_*";

    /// <summary>
    /// The extension Pi loads to reach an MCP server, and the namespace its deferred surface
    /// registers that server under.
    /// </summary>
    internal const string PiMcpAdapterExtension = "pi-mcp-adapter";

    /// <summary>
    /// The adapter's proxy entry, which precedes the per-tool entries: without it the per-tool
    /// names resolve to nothing, so an overlay that named tools but not the proxy would look
    /// configured and call nothing.
    /// </summary>
    internal const string PiMcpProxyToolEntry = "ext:pi-mcp-adapter/mcp";

    /// <summary>
    /// The profile whose holder routes work rather than researching it. Declared here rather than
    /// read from a renderer so the expectation cannot drift with the implementation.
    /// </summary>
    internal const string PureOrchestratorProfile = "orchestrator";

    /// <summary>The capability the kyber-weave entitlement rests on.</summary>
    internal const string FileReadCapability = "filesystem.read";


    /// <summary>
    /// The profile a target actually lowers an agent from. Devin and Copilot declare
    /// target-specific envelope profiles, and the envelope — not the shared profile — is what
    /// that harness sees, so the entitlement has to be read from the profile the target resolves.
    /// </summary>
    internal static string EffectiveProfileName(SquadAgent agent, SquadTarget target)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return target switch
        {
            SquadTarget.Devin => agent.DevinCapabilityProfile ?? agent.CapabilityProfile,
            SquadTarget.Copilot => agent.CopilotCapabilityProfile ?? agent.CapabilityProfile,
            _ => agent.CapabilityProfile
        };
    }

    /// <summary>
    /// Whether the agent's effective profile resolves and allows <paramref name="capability"/>.
    /// An unresolvable profile allows nothing: a missing profile is a source error, and reading it
    /// as permission would turn that error into silent widening.
    /// </summary>
    internal static bool Allows(
        SquadSource source,
        SquadAgent agent,
        SquadTarget target,
        string capability)
    {
        ArgumentNullException.ThrowIfNull(source);
        string profileName = EffectiveProfileName(agent, target);
        return source.CapabilityProfiles.Profiles.TryGetValue(
                   profileName,
                   out SquadCapabilityProfile? profile) &&
               profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
               decision == SquadPermissionDecision.Allow;
    }

    /// <summary>
    /// Whether the agent is entitled to <c>kyber-weave</c>: the profile resolves and allows
    /// <c>filesystem.read</c>, with no reference to the profile's identity.
    /// </summary>
    internal static bool GrantsKyberWeave(SquadSource source, SquadAgent agent, SquadTarget target)
    {
        return Allows(source, agent, target, FileReadCapability);
    }

    /// <summary>
    /// Whether the agent is entitled to <c>codegraph</c> and <c>context7</c>: the same read
    /// entitlement minus the pure orchestrator, which keeps its carve-out on both.
    /// </summary>
    internal static bool GrantsOrchestratorExcludedServers(
        SquadSource source,
        SquadAgent agent,
        SquadTarget target)
    {
        return Allows(source, agent, target, FileReadCapability) &&
               !string.Equals(
                   EffectiveProfileName(agent, target),
                   PureOrchestratorProfile,
                   StringComparison.Ordinal);
    }

    /// <summary>The agents the rule grants <c>kyber-weave</c>, by source identity.</summary>
    internal static IReadOnlySet<string> KyberWeaveEntitledAgents(SquadSource source, SquadTarget target)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Agents
            .Where(agent => GrantsKyberWeave(source, agent, target))
            .Select(agent => agent.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The MCP server names the toolchain roster declares, in ordinal order. This is the roster
    /// the enumerated-name renderers grant from, and a different question from the servers the
    /// squad's own <c>mcp.json</c> declares — see <see cref="SquadMcpDeclaration"/>.
    /// </summary>
    internal static IReadOnlyList<string> DeclaredServers(SquadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return [.. source.Toolchain.RequiredMcpTools.Keys.OrderBy(name => name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The fully qualified <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> names the roster declares,
    /// server-then-tool in ordinal order, filtered by <paramref name="includeServer"/>.
    /// </summary>
    internal static IReadOnlyList<string> QualifiedToolNames(
        SquadSource source,
        Func<string, bool> includeServer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(includeServer);
        return
        [
            .. source.Toolchain.RequiredMcpTools
                .Where(entry => includeServer(entry.Key))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SelectMany(entry => entry.Value
                    .OrderBy(tool => tool, StringComparer.Ordinal)
                    .Select(tool => $"mcp__{entry.Key}__{tool}"))
        ];
    }

    /// <summary>The <c>kyber-weave</c> roster's fully qualified tool names.</summary>
    internal static IReadOnlyList<string> KyberWeaveToolNames(SquadSource source)
    {
        return QualifiedToolNames(source, server => string.Equals(server, KyberWeaveServer, StringComparison.Ordinal));
    }

    /// <summary>
    /// The fully qualified tool names of every server that keeps the pure-orchestrator carve-out.
    /// </summary>
    internal static IReadOnlyList<string> OrchestratorExcludedToolNames(SquadSource source)
    {
        return QualifiedToolNames(source, server => !string.Equals(server, KyberWeaveServer, StringComparison.Ordinal));
    }

    /// <summary>
    /// The MCP tool names one agent is entitled to on a target that enumerates the whole roster
    /// it grants (Claude, ZCode, Devin): the orchestrator-excluded servers first, then
    /// <c>kyber-weave</c>, in the server-then-tool order the roster declares.
    /// </summary>
    internal static IReadOnlyList<string> EntitledRosterToolNames(
        SquadSource source,
        SquadAgent agent,
        SquadTarget target)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return
        [
            .. GrantsOrchestratorExcludedServers(source, agent, target)
                ? OrchestratorExcludedToolNames(source)
                : [],
            .. GrantsKyberWeave(source, agent, target) ? KyberWeaveToolNames(source) : []
        ];
    }

    /// <summary>
    /// The <c>kyber-weave</c> tool names one agent is entitled to on a target that named no MCP
    /// tool at all before this change (Command Code), where the widening adds the one server the
    /// squad's own <c>mcp.json</c> declares and no other.
    /// </summary>
    internal static IReadOnlyList<string> EntitledKyberWeaveToolNames(
        SquadSource source,
        SquadAgent agent,
        SquadTarget target)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return GrantsKyberWeave(source, agent, target) ? KyberWeaveToolNames(source) : [];
    }

    /// <summary>
    /// The server names one agent is withheld from, for an enumerated-name renderer's record:
    /// every roster server minus the ones the agent is granted.
    /// </summary>
    internal static IReadOnlyList<string> WithheldServers(
        SquadSource source,
        SquadAgent agent,
        SquadTarget target)
    {
        ArgumentNullException.ThrowIfNull(agent);
        bool grantsKyberWeave = GrantsKyberWeave(source, agent, target);
        bool grantsOthers = GrantsOrchestratorExcludedServers(source, agent, target);
        return
        [
            .. DeclaredServers(source).Where(server =>
                string.Equals(server, KyberWeaveServer, StringComparison.Ordinal)
                    ? !grantsKyberWeave
                    : !grantsOthers)
        ];
    }

    /// <summary>
    /// The namespace Pi's MCP adapter registers a server under: hyphens become underscores before
    /// any other character is mapped, which is why <c>kyber-weave</c> is reached as
    /// <c>kyber_weave</c> there and nowhere else.
    /// </summary>
    /// <remarks>
    /// Stated as the inverse of <see cref="KiloMcpPermissionKey"/> on purpose. The same server
    /// name is hyphenated on Kilo and underscored on Pi, and a test that assumed one spelling
    /// everywhere would pass on one target and silently assert nothing on the other.
    /// </remarks>
    internal static string PiMcpServerNamespace(string serverName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverName);
        return serverName.Replace('-', '_');
    }

    /// <summary>
    /// The adapter's tool segment: every character outside <c>[A-Za-z0-9_]</c> becomes <c>_</c>,
    /// which is how a hyphenated tool name would be spelled.
    /// </summary>
    internal static string PiMcpToolSegment(string toolName)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        return string.Concat(toolName.Select(
            character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_'));
    }

    /// <summary>
    /// The prefix every <c>kyber-weave</c> overlay tool entry on Pi carries, so a suite can assert
    /// the underscored namespace appeared and the hyphenated one did not.
    /// </summary>
    internal static string PiMcpToolEntryPrefix { get; } =
        $"ext:{PiMcpAdapterExtension}/mcp__{PiMcpServerNamespace(KyberWeaveServer)}__";

    /// <summary>
    /// The overlay entries an entitled agent owes on Pi: the proxy entry, then one entry per
    /// roster tool in ordinal order.
    /// </summary>
    /// <remarks>
    /// Derived from the source-declared roster, so a tool added to <c>toolchain.yml</c> moves this
    /// expectation without a renderer or a test edit, and a renderer that dropped one entry fails.
    /// A roster with no <c>kyber-weave</c> entry yields nothing, which is what keeps an unmatched
    /// grant from loading an extension for a server nobody declared.
    /// </remarks>
    internal static IReadOnlyList<string> PiMcpOverlayToolEntries(SquadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.Toolchain.RequiredMcpTools.TryGetValue(
                KyberWeaveServer,
                out IReadOnlyList<string>? tools))
        {
            return [];
        }

        return
        [
            PiMcpProxyToolEntry,
            .. tools
                .Order(StringComparer.Ordinal)
                .Select(tool => PiMcpToolEntryPrefix + PiMcpToolSegment(tool))
        ];
    }

    /// <summary>
    /// The declared launch definition of one server in the squad's own <c>mcp.json</c>, or
    /// <see langword="null"/> when the declaration does not carry a usable one.
    /// </summary>
    /// <remarks>
    /// Antigravity renders this into each entitled agent's <c>mcpServers</c> entry, so the
    /// expectation has to come from the same declaration rather than from a literal: a command
    /// line pinned in a test would keep passing while the operator's configuration was edited to
    /// launch something else.
    /// </remarks>
    internal static SquadDeclaredMcpServer? DeclaredServerLaunch(SquadSource source, string serverName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(serverName);

        if (!source.McpConfiguration.TryGetProperty("mcpServers", out JsonElement servers) ||
            servers.ValueKind != JsonValueKind.Object ||
            !servers.TryGetProperty(serverName, out JsonElement server) ||
            server.ValueKind != JsonValueKind.Object ||
            !server.TryGetProperty("command", out JsonElement commandElement) ||
            commandElement.ValueKind != JsonValueKind.String ||
            !server.TryGetProperty("args", out JsonElement argsElement) ||
            argsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? command = commandElement.GetString();
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        List<string> args = [];
        foreach (JsonElement arg in argsElement.EnumerateArray())
        {
            string? value = arg.GetString();
            if (value is null)
            {
                return null;
            }

            args.Add(value);
        }

        return new SquadDeclaredMcpServer(serverName, command, args);
    }
}

/// <summary>A declared MCP server's launch definition, as <c>mcp.json</c> states it.</summary>
/// <param name="Name">The server key under <c>mcpServers</c>.</param>
/// <param name="Command">The executable the harness is told to start.</param>
/// <param name="Args">The arguments that follow it, in declared order.</param>
internal sealed record SquadDeclaredMcpServer(
    string Name,
    string Command,
    IReadOnlyList<string> Args);

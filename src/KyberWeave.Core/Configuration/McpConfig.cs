using System.Collections.ObjectModel;

namespace KyberWeave.Core.Configuration;

/// <summary>Which side ships the MCP server binary a declaration names.</summary>
/// <remarks>
/// The distinction exists because the remedy differs, not because the check does. Kyber-Weave
/// builds <c>kyber-weave-mcp</c> itself, so its absence is this tool's own problem and the fix is
/// an update. A third-party server is installed from a distribution Kyber-Weave does not own, so
/// its absence is reported together with the manual command the declaration carries and nothing
/// more is promised.
/// </remarks>
public enum McpProvenance
{
    /// <summary>A binary Kyber-Weave itself ships.</summary>
    FirstParty,

    /// <summary>A binary the operator installs from a distribution Kyber-Weave does not own.</summary>
    ThirdParty
}

/// <summary>How strongly a repository's agent instructions depend on a declared server.</summary>
public enum McpRequirementLevel
{
    /// <summary>An instruction depends on this server; its absence breaks that instruction.</summary>
    Required,

    /// <summary>An instruction degrades without this server but keeps working.</summary>
    Recommended
}

/// <summary>One MCP server a repository's agent instructions rely on.</summary>
/// <param name="Name">The server's name, as the declaration keyed it and as an MCP client sees it.</param>
/// <param name="Command">
/// The launch command as argv: the program first, then each argument in the order written. Never a
/// command line, for the same reason <c>review.gates</c> takes argv — re-splitting a string is how
/// an argument containing a space silently becomes two.
/// </param>
/// <param name="Provenance">Which side ships the binary.</param>
/// <param name="Level">Whether the instructions merely prefer this server or depend on it.</param>
/// <param name="Install">
/// An optional manual install command, printed verbatim when the binary is absent. Free text on
/// purpose: Kyber-Weave never installs external software as a side effect, so this is shown to the
/// operator rather than executed.
/// </param>
public sealed record RequiredMcpServer(
    string Name,
    IReadOnlyList<string> Command,
    McpProvenance Provenance,
    McpRequirementLevel Level = McpRequirementLevel.Required,
    string? Install = null);

/// <summary>The <c>mcp:</c> host configuration.</summary>
public sealed class McpConfig
{
    /// <summary>
    /// The servers the repository's agent instructions rely on, keyed by server name, in
    /// declaration order. Empty when the host declares none.
    /// </summary>
    public IReadOnlyDictionary<string, RequiredMcpServer> RequiredServers { get; init; } =
        new ReadOnlyDictionary<string, RequiredMcpServer>(
            new Dictionary<string, RequiredMcpServer>(StringComparer.Ordinal));

    /// <summary>Product defaults for a host that declares no <c>mcp:</c> section.</summary>
    /// <remarks>
    /// Deliberately empty of servers rather than pre-populated with today's two. Whether a
    /// repository depends on an MCP server is the repository's claim to make: a default that
    /// already named one would turn every existing host's upgrade into a new finding about a
    /// dependency it never declared, and <c>docs init</c> would stop behaving as it does today.
    /// </remarks>
    public static McpConfig ProductDefaults { get; } = new();
}

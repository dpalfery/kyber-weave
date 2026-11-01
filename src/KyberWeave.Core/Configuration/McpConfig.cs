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
/// <remarks>
/// The constructor is the type's own promise rather than a convenience. The loader refuses a
/// malformed entry before it constructs anything, so these guards are unreachable from the
/// <c>kyber-weave.yml</c> path today — but this is a public record, so the loader is not the only
/// caller it will ever have, and a caller that skipped the loader would otherwise inherit a server
/// with no name to key a harness entry by and no program to launch.
/// </remarks>
public sealed record RequiredMcpServer
{
    /// <summary>
    /// The binary Kyber-Weave itself ships for its own MCP server (R1.1: today
    /// <c>kyber-weave-mcp</c>). Named here because <see cref="McpProvenance.FirstParty"/> is a claim
    /// about this binary and nothing else.
    /// </summary>
    public const string ShippedBinary = "kyber-weave-mcp";

    /// <summary>
    /// Builds a declared server, refusing one that cannot describe a server at all.
    /// </summary>
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
    /// <exception cref="ArgumentException">
    /// <paramref name="Name"/> is blank, the command is empty or holds a blank element, or
    /// <paramref name="Provenance"/> claims a first-party binary the command does not name.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="Command"/> is null.</exception>
    /// <remarks>
    /// The parameters keep the PascalCase names a positional record would have given them, so a
    /// caller that already names one — <c>Install:</c>, <c>Level:</c> — compiles unchanged. This
    /// record was positional until the guards arrived, and turning them into camelCase parameters
    /// would have been a source-breaking change with no other purpose.
    /// </remarks>
    public RequiredMcpServer(
        string Name,
        IReadOnlyList<string> Command,
        McpProvenance Provenance,
        McpRequirementLevel Level = McpRequirementLevel.Required,
        string? Install = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentNullException.ThrowIfNull(Command);

        if (Command.Count == 0)
        {
            throw new ArgumentException(
                "A declared server needs a launch command: the program first, then each argument.",
                nameof(Command));
        }

        foreach (string element in Command)
        {
            if (string.IsNullOrWhiteSpace(element))
            {
                throw new ArgumentException(
                    $"A launch command holds an argv element that names nothing: '{element}'.",
                    nameof(Command));
            }
        }

        this.Name = Name;
        // Copied so the record's own value cannot change under it: a caller that keeps the list it
        // passed in would otherwise be able to rewrite a server the configuration already merged.
        this.Command = [.. Command];
        this.Provenance = Provenance;
        this.Level = Level;
        this.Install = Install;

        // First-party is a claim about which side ships the binary, so it is only true of a command
        // that runs that binary. Both remedies downstream are built on the claim — "this Kyber-Weave
        // installation is broken; run kyber-weave update" — and each would be advice about a program
        // the repository never installed if a claim like this one were carried.
        if (this.Provenance == McpProvenance.FirstParty && !NamesShippedBinary(this.Command))
        {
            throw new ArgumentException(
                $"FirstParty claims Kyber-Weave ships this server, but the command runs '{this.Command[0]}'. "
                + $"Kyber-Weave ships {ShippedBinary}; a binary it does not ship is McpProvenance.ThirdParty.",
                nameof(Provenance));
        }
    }

    /// <summary>
    /// Whether a launch command runs the binary Kyber-Weave ships, which is the only thing
    /// <see cref="McpProvenance.FirstParty"/> can truthfully be claimed about.
    /// </summary>
    /// <param name="command">The launch command as argv.</param>
    /// <returns><see langword="true"/> when argv[0] names <see cref="ShippedBinary"/>.</returns>
    /// <remarks>
    /// Only argv[0] is read, because it is the whole of what the rest of this feature treats as "the
    /// binary a declaration names": the check probes it, and a harness launches it. Matching any
    /// later element would let <c>[wrapper, --server, kyber-weave-mcp]</c> pass as first-party while
    /// the thing a missing-binary finding would be about is <c>wrapper</c>.
    /// The match is a containment rather than an equality so a locally built or renamed copy of the
    /// shipped server still counts, and it ignores case because the file name on Windows does.
    /// </remarks>
    public static bool NamesShippedBinary(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Count == 0)
            return false;

        string program = Path.GetFileName(command[0]);

        return program.Contains(ShippedBinary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The server's name, as the declaration keyed it and as an MCP client sees it.</summary>
    public string Name { get; }

    /// <summary>The launch command as argv: the program first, then each argument in declaration order.</summary>
    public IReadOnlyList<string> Command { get; }

    /// <summary>Which side ships the binary the command names.</summary>
    public McpProvenance Provenance { get; }

    /// <summary>Whether the instructions merely prefer this server or depend on it.</summary>
    public McpRequirementLevel Level { get; }

    /// <summary>An optional manual install command, printed verbatim when the binary is absent.</summary>
    public string? Install { get; }
}

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

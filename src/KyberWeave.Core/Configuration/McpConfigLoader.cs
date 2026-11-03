using System.Collections.ObjectModel;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads, merges, and validates the <c>mcp:</c> host configuration section.</summary>
public static class McpConfigLoader
{
    /// <summary>
    /// The section path every refusal names. A refusal that only quoted the server would leave the
    /// operator searching the file for which section the server was declared in.
    /// </summary>
    private const string SectionPath = "mcp.required-servers";

    /// <summary>Merges the parsed <c>mcp:</c> section over the defaults.</summary>
    /// <param name="defaults">The configuration a host gets when the section is absent.</param>
    /// <param name="section">The parsed section, or null when the host declares none.</param>
    /// <exception cref="YamlException">
    /// A declaration is malformed. The message names the section, the server and the value at
    /// fault, because the loader that catches it turns it into a single
    /// <c>KW-CONFIG-001</c> diagnostic and the operator has only that message to work from.
    /// </exception>
    internal static McpConfig Merge(McpConfig defaults, McpYamlSection? section)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (section?.RequiredServers is null)
            return defaults;

        Dictionary<string, RequiredMcpServer> servers = new(StringComparer.Ordinal);
        foreach ((string name, McpServerYaml? declaration) in section.RequiredServers)
            servers[name] = ParseServer(name, declaration);

        // The host's map replaces the inherited one instead of being merged into it. Union would
        // make a server the host removed keep being reported missing, and nothing the operator
        // can write would retract it. Null still falls back above, so an absent section is not a
        // way to clear the defaults by accident.
        return new McpConfig
        {
            RequiredServers = new ReadOnlyDictionary<string, RequiredMcpServer>(servers)
        };
    }

    private static RequiredMcpServer ParseServer(string name, McpServerYaml? declaration)
    {
        // The map key is the server name: it is the key a harness entry is written under, the
        // subject a diagnostic quotes, and — for Codex — a TOML table header. A blank one names
        // none of those, so it is refused here rather than carried into three consumers.
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new YamlException(
                $"{SectionPath} has a server with a blank name ('{name}'). Every declared server needs a name: "
                + "it is the key its harness entries, its findings and a TOML table are written under.");
        }

        string where = $"{SectionPath}.{name}";

        // Parsed in declaration order, because the provenance is a claim about the command and the
        // refusal can only name the offending program once the command has been read.
        IReadOnlyList<string> command = ParseCommand(declaration?.Command, where);

        return new RequiredMcpServer(
            name,
            command,
            ParseProvenance(declaration?.Provenance, command, where),
            ParseLevel(declaration?.Level, where),
            declaration?.Install);
    }

    /// <summary>
    /// Reads the launch command as argv. A declared list that names no program is refused rather
    /// than carried as an empty command, because an entry with no program is the same misspelling
    /// as an absent one and there is nothing a launcher could do with it.
    /// </summary>
    private static IReadOnlyList<string> ParseCommand(List<string>? command, string where)
    {
        if (command is null || command.Count == 0)
        {
            throw new YamlException(
                $"{where} has no command. Declare the launch command as argv, program first: " +
                "command: [kyber-weave-mcp, --repo-root, .].");
        }

        foreach (string? element in command)
        {
            if (string.IsNullOrWhiteSpace(element))
                throw new YamlException($"{where}.command has an empty argument. Every argv element must name something to pass.");
        }

        return [.. command];
    }

    /// <summary>
    /// Reads the provenance class, refusing <c>first-party</c> for a command that runs a binary
    /// Kyber-Weave does not ship.
    /// </summary>
    /// <remarks>
    /// The classes are not a preference, they are a statement of fact about who ships the binary
    /// (R1.1), and 6.3 builds the first-party remedy on it: an absent first-party binary is reported
    /// as this installation being broken. A declaration that says <c>first-party</c> while running
    /// somebody else's program would make that remedy advice about a program Kyber-Weave never
    /// installed — so it is refused here, where the operator can still read which value was wrong,
    /// rather than loaded and acted on. Refusing keeps 6.3's promise true for the declarations that
    /// do load: every first-party server the rest of the product sees really is the shipped binary.
    /// </remarks>
    private static McpProvenance ParseProvenance(
        string? value,
        IReadOnlyList<string> command,
        string where)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new YamlException(
                $"{where} has no provenance. Declare it as first-party or third-party.");
        }

        return value switch
        {
            "first-party" when RequiredMcpServer.NamesShippedBinary(command) => McpProvenance.FirstParty,
            "first-party" => throw new YamlException(
                $"{where}.provenance is 'first-party', but the command runs '{command[0]}'. first-party "
                + $"means a binary Kyber-Weave ships, today {RequiredMcpServer.ShippedBinary}; declare this "
                + "server third-party if the binary is somebody else's."),
            "third-party" => McpProvenance.ThirdParty,
            _ => throw new YamlException(
                $"{where}.provenance '{value}' is not recognised; use first-party or third-party.")
        };
    }

    /// <summary>
    /// Reads the requirement level, defaulting to <see cref="McpRequirementLevel.Required"/> when
    /// the entry names none (R1.2).
    /// </summary>
    /// <remarks>
    /// Required is the deliberate default. A server an instruction leans on and an operator forgot
    /// to classify is still one whose absence breaks that instruction, so reading silence as
    /// "recommended" would quietly downgrade the claim the repository made.
    /// </remarks>
    private static McpRequirementLevel ParseLevel(string? value, string where)
    {
        if (string.IsNullOrWhiteSpace(value))
            return McpRequirementLevel.Required;

        return value switch
        {
            "required" => McpRequirementLevel.Required,
            "recommended" => McpRequirementLevel.Recommended,
            _ => throw new YamlException(
                $"{where}.level '{value}' is not recognised; use required or recommended.")
        };
    }
}

namespace KyberWeave.Core.Configuration;

/// <summary>The <c>mcp:</c> section of <c>kyber-weave.yml</c>.</summary>
internal sealed class McpYamlSection
{
    /// <summary>
    /// Server name to its declaration, as the operator wrote them. A null map and an empty map
    /// both mean "declared nothing", which is the same state as an absent section.
    /// </summary>
    public Dictionary<string, McpServerYaml?>? RequiredServers { get; init; }
}

/// <summary>One entry of <c>mcp.required-servers</c>.</summary>
/// <remarks>
/// Every field the loader needs a decision on is read as raw YAML rather than bound to an enum.
/// Binding <c>provenance</c> straight to <see cref="McpProvenance"/> would let a misspelling
/// deserialize to the default member, which claims the server is first-party and required — a
/// silent change to what the repository says it depends on. <c>level</c> has the same shape.
/// </remarks>
internal sealed class McpServerYaml
{
    /// <summary>The launch command as argv, in declaration order.</summary>
    public List<string>? Command { get; init; }

    /// <summary>Provenance as written: <c>first-party</c> or <c>third-party</c>.</summary>
    public string? Provenance { get; init; }

    /// <summary>Requirement level as written: <c>required</c> or <c>recommended</c>.</summary>
    public string? Level { get; init; }

    /// <summary>A manual install command, carried verbatim.</summary>
    public string? Install { get; init; }
}

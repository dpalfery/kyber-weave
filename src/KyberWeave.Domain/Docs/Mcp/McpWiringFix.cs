namespace KyberWeave.Core.Docs.Mcp;

/// <summary>
/// One harness file a wiring repair would write, and the servers it would put there.
/// </summary>
/// <param name="Harness">The harness slug, which is also the repair's target.</param>
/// <param name="Path">The repository-relative configuration file the write would land in.</param>
/// <param name="Servers">The server names the write would add, ordinal-sorted.</param>
/// <param name="Create">
/// Whether the file is absent today, so the repair would create it rather than insert into it.
/// </param>
/// <remarks>
/// This is what an operator is shown before consenting to a write, so it names the file and the
/// servers rather than the finding that prompted them: "cursor is missing a server" is a diagnosis,
/// and "this would write .cursor/mcp.json adding kyber-weave" is what consent is given to.
/// </remarks>
public sealed record McpWiringFix(
    string Harness,
    string Path,
    IReadOnlyList<string> Servers,
    bool Create);

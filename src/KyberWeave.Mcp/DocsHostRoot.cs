namespace KyberWeave.Mcp;

/// <summary>
/// The checkout one set of docs tools answers for.
/// </summary>
/// <remarks>
/// A type rather than a bare string because the MCP SDK constructs each tool type from the container on every call
/// (<c>ActivatorUtilities.CreateInstance</c>), and no container can resolve a <see cref="string"/>: the tool then
/// fails on every invocation while <c>tools/list</c>, which reads attributes only, still answers.
/// </remarks>
/// <param name="Path">The absolute repository root these tools answer for.</param>
public sealed record DocsHostRoot(string Path);

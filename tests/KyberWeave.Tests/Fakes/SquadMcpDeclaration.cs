using System.Text.Json;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Reads what a Squad source declares for MCP, so a test's expectation is computed from the
/// loaded <see cref="SquadSource"/> rather than echoed from the renderer under test.
/// </summary>
/// <remarks>
/// A test that asked the renderer which servers it meant to declare could never disagree with
/// it, so a renderer that invented a name — or dropped every one — would satisfy its own
/// expectation. The renderer keeps its own private enumeration for the same reason: this helper
/// and <c>FactoryRenderer</c> reach the same answer independently, and only that independence
/// lets a contract test or a roster guard notice when the two diverge. It therefore reads the
/// declaration only and must not call into a renderer.
/// </remarks>
internal static class SquadMcpDeclaration
{
    /// <summary>
    /// The server names the squad's own <c>mcp.json</c> declares, in ordinal order. A missing or
    /// non-object <c>mcpServers</c> yields no names, matching the fail-closed reading a renderer
    /// has to take: an unreadable declaration grants nothing rather than inheriting the parent.
    /// </summary>
    internal static IReadOnlyList<string> ServerNames(SquadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.McpConfiguration.TryGetProperty("mcpServers", out JsonElement servers) ||
            servers.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return
        [
            .. servers.EnumerateObject()
                .Select(server => server.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
        ];
    }
}

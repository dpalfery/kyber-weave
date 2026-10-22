using System.Reflection;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the roster parity invariant between the kyber-weave MCP server and the shipped
/// kyber-squad toolchain: every tool <c>DocsTools</c> exposes must appear under
/// <c>required-mcp-tools.kyber-weave</c>.
/// </summary>
/// <remarks>
/// The roster is what the renderers grant from and what <c>squad doctor</c> checks an install
/// against, so a tool the server exposes but the roster omits is invisible to every canonical
/// agent — and it fails silently, because a roster that is merely short reports nothing.
/// <c>docs_status</c> shipped after the roster was last verified and went missing for exactly
/// that reason. This guard closes the class of defect rather than the instance, and carries no
/// exception list: an allow-list only one layer knows about is the drift it exists to catch.
/// </remarks>
public sealed class SquadToolchainRosterGuardTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    [Fact]
    public void ShippedToolchainRosterGrantsEveryToolTheKyberWeaveMcpServerExposes()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        Assert.True(
            source.Toolchain.RequiredMcpTools.TryGetValue("kyber-weave", out IReadOnlyList<string>? declared),
            "products/kyber-squad/toolchain.yml declares no 'kyber-weave' roster under "
            + "required-mcp-tools, so no canonical agent is granted any kyber-weave tool.");
        Assert.NotNull(declared);

        IReadOnlyList<string> exposed = ExposedMcpToolNames();
        Assert.NotEmpty(exposed);

        string[] missing =
        [
            .. exposed
                .Except(declared, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
        ];

        Assert.True(
            missing.Length == 0,
            $"products/kyber-squad/toolchain.yml omits {missing.Length} tool(s) the kyber-weave MCP "
            + $"server exposes: {string.Join(", ", missing)}. Add each under "
            + "required-mcp-tools.kyber-weave; the renderers grant from this roster and `squad doctor` "
            + "checks an install against it, so an omitted tool is silently unavailable to every "
            + "canonical agent.");
    }

    /// <summary>
    /// The tool names the server actually exposes, read from the <c>[McpServerTool]</c> metadata
    /// rather than transcribed, so the guard tracks the server instead of a copy of it.
    /// </summary>
    private static IReadOnlyList<string> ExposedMcpToolNames()
    {
        List<string> names = [];

        foreach (MethodInfo method in typeof(DocsTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            McpServerToolAttribute? attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute is null)
            {
                continue;
            }

            string? name = attribute.Name;
            Assert.NotNull(name);
            names.Add(name);
        }

        return names;
    }
}

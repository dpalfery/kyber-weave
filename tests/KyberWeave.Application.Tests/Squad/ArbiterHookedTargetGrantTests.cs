using System.Reflection;
using System.Text;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The Arbiter's hooks stand in for a grant, so no target that carries the hooks also grants the Arbiter server's
/// tools in a file it renders.
/// </summary>
public sealed class ArbiterHookedTargetGrantTests
{
    private const string ArbiterServer = "kw-arbiter";

    [Fact]
    public async Task NoHookedTargetGrantsTheArbiterServer()
    {
        string[] qualifiedTools = DeclaredArbiterToolNames()
            .Select(tool => $"mcp__{ArbiterServer}__{tool}")
            .ToArray();

        foreach (SquadTarget target in Enum.GetValues<SquadTarget>())
        {
            if (!ArbiterHookWiring.HookedTargets.Contains(SquadTargetCatalog.GetToken(target)))
            {
                continue;
            }

            SquadRenderResult result = await CanonicalSquadTree.RenderSharedProjectAsync(target);
            Assert.True(result.Success, string.Join("; ", result.Errors));

            foreach (SquadDeploymentFile file in result.Files)
            {
                string content = Encoding.UTF8.GetString(file.Content.Span);

                foreach (string tool in qualifiedTools)
                {
                    Assert.DoesNotContain(tool, content, StringComparison.Ordinal);
                }

                Assert.DoesNotContain($"{ArbiterServer}/", content, StringComparison.Ordinal);
            }
        }
    }

    private static string[] DeclaredArbiterToolNames() =>
        [.. typeof(ArbiterTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name!)
            .Order(StringComparer.Ordinal)];
}

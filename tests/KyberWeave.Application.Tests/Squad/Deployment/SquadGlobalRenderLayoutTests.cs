using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
///     Guards the per-target global render layout: no renderer may emit a path carrying the target's
///     project-scope prefix, so a layout classifier cannot mistake a per-target global render for a legacy
///     single-root path.
/// </summary>
public sealed class SquadGlobalRenderLayoutTests
{
    private static readonly CanonicalSquadTree Tree = CanonicalSquadTree.Shared;

    [Fact]
    public async Task RenderReceiptLayout_GlobalRender_ContainsNoPrefixedPaths()
    {
        // Map of target to its legacy prefix from the investigation findings.
        Dictionary<SquadTarget, string> legacyPrefixes = new()
        {
            { SquadTarget.Claude, ".claude/" },
            { SquadTarget.Codex, ".codex/" },
            { SquadTarget.Cursor, ".cursor/" },
            { SquadTarget.Copilot, ".github/" },
            { SquadTarget.Antigravity, ".agents/" },
            { SquadTarget.Pi, ".pi/" },
            { SquadTarget.OpenCode, ".opencode/" },
            { SquadTarget.Kilo, ".kilo/" },
            { SquadTarget.Factory, ".factory/" },
            { SquadTarget.Warp, ".warp/" },
            { SquadTarget.ZCode, ".zcode/" },
            { SquadTarget.Devin, ".devin/" }
        };

        SquadGlobalRoots globalRoots = new(_ => null, Path.Combine(Tree.Files.Root, "home"));
        ISquadRenderer renderer = Tree.Registry(SquadHostDefaults.CreateRenderers());

        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            SquadRenderResult result = await renderer.RenderAsync(
                Tree.Prepare(new SquadRenderRequest(
                    Tree.ProductRoot,
                    [target],
                    SquadDeploymentScope.Global,
                    GlobalRoots: globalRoots)
                {
                    PreloadedSource = CanonicalSquadTree.SharedSource
                }));

            if (legacyPrefixes.TryGetValue(target, out string? prefix))
                foreach (SquadDeploymentFile file in result.Files)
                    Assert.False(
                        file.RelativePath.StartsWith(prefix, StringComparison.Ordinal),
                        $"Renderer for {target} must not emit paths prefixed with '{prefix}' in global scope.");
        }
    }
}

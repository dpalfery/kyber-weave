using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
///     A real global deployment onto the real disk: one install writes each harness's files only under
///     that harness's resolved home subtree, and the receipt describes exactly what landed there.
/// </summary>
/// <remarks>
///     This is the end-to-end smoke test for global deployment, kept here because it proves the real file
///     system, the real release extraction and the real transaction together, which no in-memory case can.
///     The same behaviour in memory — every receipt path under the resolved root, and a global install
///     writing only there — is covered by
///     <c>SquadGlobalScopeDeploymentTests.InstallAsync_GlobalScopeDryRun_PlansEveryFileUnderTheResolvedTargetRootWithBareRelativePaths</c>
///     and <c>SquadGlobalScopeDeploymentTests.InstallAsync_GlobalScope_NamingANewTargetAddsItAndKeepsTheExistingOnes</c>;
///     the receipt layout itself is <c>SquadRenderedFileCount.For</c>, shared with those cases.
///     <para>
///     One real install covers every harness: the four targets were originally asserted as four separate
///     installs that differed only in the harness name, and a deployment is the expensive part of this
///     suite. The per-harness expectations below are unchanged and still run for all four harnesses — the
///     install is shared, the assertions are not.
///     </para>
/// </remarks>
public sealed class RealGlobalInstallTests : IClassFixture<SquadCorpusTemplate>, IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SquadCorpusTemplate _corpus;

    public RealGlobalInstallTests(SquadCorpusTemplate corpus)
    {
        _corpus = corpus;
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    ///     A real global install writes each harness's files only under that harness's resolved home
    ///     subtree, and the receipt describes exactly what landed there.
    /// </summary>
    [Fact]
    public async Task RealGlobalInstall_WritesOnlyUnderEachResolvedHomeSubtree()
    {
        // Arrange
        SquadTarget[] targets = [SquadTarget.Claude, SquadTarget.Pi, SquadTarget.ZCode, SquadTarget.Devin];
        string tempHome = Path.Combine(_temp.Path, "real-home");
        Directory.CreateDirectory(tempHome);
        string projectRoot = Path.Combine(_temp.Path, "real-project");
        Directory.CreateDirectory(projectRoot);
        string userData = Path.Combine(_temp.Path, "real-user-data");

        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        ISquadRenderer renderer = SquadHostDefaults.ResolveRenderer();
        SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(releaseSource, renderer, stateStore, globalRoots: globalRoots);

        SquadInstallRequest request = new(
            projectRoot,
            SquadDeploymentScope.Global,
            targets,
            Version: "1.2.3");

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Receipt);
        Assert.NotNull(result.Plan);

        string[] targetTokens = [.. targets.Select(SquadTargetCatalog.GetToken)];
        Assert.All(result.Receipt.Files, file => Assert.Contains(file.Target, targetTokens));
        Assert.Equal(
            targets.Sum(target => SquadRenderedFileCount.For(_corpus.Source, target)),
            result.Receipt.Files.Count);

        foreach (SquadTarget target in targets)
        {
            string expectedRoot = globalRoots.ResolveGlobalRoot(target);
            string expectedToken = SquadTargetCatalog.GetToken(target);
            SquadOwnedFile[] targetFiles = [.. result.Receipt.Files.Where(file => file.Target == expectedToken)];

            Assert.Equal(SquadRenderedFileCount.For(_corpus.Source, target), targetFiles.Length);

            // Claude keeps the subagent and adds an entry-point skill beside it; Cursor
            // follows the Pi-renderer skill-only precedent (no agents/<name>.md principal).
            if (target is SquadTarget.Claude)
            {
                SquadSource source = _corpus.Source;
                SquadAgent primaryAgent = Assert.Single(source.Agents, a => a.Name == "conductor");

                // The conductor carries plan-path, spec-path, intake-path, and autonomy references;
                // the entry-point skill projects all four.
                Assert.Equal(4, primaryAgent.Resources.Count);
                Assert.Single(
                    targetFiles,
                    f => f.RelativePath == $"skills/{primaryAgent.Name}/SKILL.md");
                foreach (SquadResource resource in primaryAgent.Resources)
                    Assert.Single(
                        targetFiles,
                        f => f.RelativePath == $"skills/{primaryAgent.Name}/{resource.RelativePath}");

                Assert.Single(
                    targetFiles,
                    f => f.RelativePath == $"agents/{primaryAgent.Name}.md");
            }

            Assert.True(
                Directory.Exists(expectedRoot),
                $"Expected {target}'s resolved global root '{expectedRoot}' to exist after a real install.");
            string[] writtenFiles = Directory.EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories).ToArray();
            Assert.Equal(targetFiles.Length, writtenFiles.Length);

            foreach (SquadOwnedFile file in targetFiles)
            {
                string physicalPath = result.Plan.ResolvePhysicalPath(file, SquadFileSystemPathSemantics.Instance);
                Assert.True(
                    SquadFileSystemPathSemantics.Instance.IsWithin(expectedRoot, physicalPath),
                    $"Physical path '{physicalPath}' for '{file.RelativePath}' ({target}) is not under " +
                    $"the resolved global root '{expectedRoot}'.");
            }
        }

        // The project root Squad was pointed at receives no rendered files under Global scope.
        Assert.Empty(Directory.EnumerateFileSystemEntries(projectRoot, "*", SearchOption.AllDirectories));
    }
}

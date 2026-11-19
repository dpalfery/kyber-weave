using System.Security.Cryptography;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
///     Proves that under <c>Scope: Global</c> every rendered file lands under its target's resolved
///     per-user global root with a bare <c>agents/</c>, <c>skills/</c>, or Factory <c>droids/</c> relative
///     path, while project scope keeps today's harness prefix.
/// </summary>
/// <remarks>
///     The whole lifecycle — release extraction, render, plan, receipt and transaction — runs over an
///     in-memory Squad state and the shipped Squad source copied into it, so no case touches the disk, the
///     real home or the process environment. The real on-disk deployment is the integration smoke test
///     <c>RealGlobalInstallTests.RealGlobalInstallWritesOnlyUnderEachResolvedHomeSubtree</c>.
/// </remarks>
public sealed class SquadGlobalScopeDeploymentTests
{
    private readonly InMemorySquadState _state = new();

    private static SquadSource Corpus => CanonicalSquadTree.SharedSource;

    private string Home => _state.UserPaths.HomeDirectory;

    /// <summary>
    ///     Criterion 2: a global dry run per target plans bare relative paths under the resolved root.
    /// </summary>
    [Theory]
    [InlineData(SquadTarget.Claude)]
    [InlineData(SquadTarget.Codex)]
    [InlineData(SquadTarget.Cursor)]
    [InlineData(SquadTarget.Copilot)]
    [InlineData(SquadTarget.Antigravity)]
    [InlineData(SquadTarget.Pi)]
    [InlineData(SquadTarget.OpenCode)]
    [InlineData(SquadTarget.Kilo)]
    [InlineData(SquadTarget.Warp)]
    [InlineData(SquadTarget.ZCode)]
    [InlineData(SquadTarget.Devin)]
    public async Task InstallAsync_GlobalScopeDryRun_PlansEveryFileUnderTheResolvedTargetRootWithBareRelativePaths(
        SquadTarget target)
    {
        // Arrange
        string projectRoot = _state.CreateRoot($"global-dry-run-{target}");
        SquadGlobalRoots globalRoots = new(_ => null, Home);
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        SquadLifecycleService service = Lifecycle(releaseSource, globalRoots);

        SquadInstallRequest request = new(
            projectRoot,
            SquadDeploymentScope.Global,
            [target],
            Version: "1.2.3",
            DryRun: true);

        string expectedRoot = globalRoots.ResolveGlobalRoot(target);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Receipt);
        Assert.NotEmpty(result.Receipt.Files);
        Assert.Equal(SquadRenderedFileCount.For(Corpus, target), result.Receipt.Files.Count);

        // Claude keeps the subagent and adds an entry-point skill beside it; Cursor
        // follows the Pi-renderer skill-only precedent (no agents/<name>.md principal),
        // so its total is count-neutral and only the receipt paths differ.
        if (target is SquadTarget.Claude or SquadTarget.Cursor)
        {
            SquadSource source = Corpus;
            SquadAgent primaryAgent = Assert.Single(source.Agents, a => a.Name == "conductor");

            // The conductor carries plan-path, spec-path, intake-path, and autonomy references;
            // the entry-point skill projects all four.
            Assert.Equal(4, primaryAgent.Resources.Count);
            Assert.Single(
                result.Receipt.Files,
                f => f.RelativePath == $"skills/{primaryAgent.Name}/SKILL.md");
            foreach (SquadResource resource in primaryAgent.Resources)
                Assert.Single(
                    result.Receipt.Files,
                    f => f.RelativePath == $"skills/{primaryAgent.Name}/{resource.RelativePath}");

            if (target == SquadTarget.Claude)
                Assert.Single(
                    result.Receipt.Files,
                    f => f.RelativePath == $"agents/{primaryAgent.Name}.md");
            else
                Assert.DoesNotContain(
                    result.Receipt.Files,
                    f => f.RelativePath == $"agents/{primaryAgent.Name}.md");
        }

        if (target == SquadTarget.Codex)
        {
            SquadSource source = Corpus;
            SquadAgent primaryAgent = Assert.Single(source.Agents, agent => agent.Name == "conductor");

            string[] conductorEntryPointPaths =
            [
                $"skills/{primaryAgent.Name}/SKILL.md",
                .. primaryAgent.Resources.Select(resource =>
                    $"skills/{primaryAgent.Name}/{resource.RelativePath}")
            ];
            Assert.Equal(1 + primaryAgent.Resources.Count, conductorEntryPointPaths.Length);
            foreach (string path in conductorEntryPointPaths)
                Assert.Single(result.Receipt.Files, file => file.RelativePath == path);

            SquadOwnedFile nativeAgent = Assert.Single(
                result.Receipt.Files,
                file => file.RelativePath == $"agents/{primaryAgent.Name}.toml");
            Assert.Equal("codex", nativeAgent.Target);

            foreach (SquadResource resource in primaryAgent.Resources)
                Assert.Single(
                    result.Receipt.Files,
                    file => file.RelativePath == $"agents/{resource.RelativePath}");
        }

        bool sawAgentFile = false;
        bool sawSkillFile = false;
        bool sawCommandFile = false;
        foreach (SquadOwnedFile file in result.Receipt.Files)
        {
            Assert.False(
                file.RelativePath.StartsWith('.'),
                $"Global-scope path '{file.RelativePath}' for {target} still carries a project-scope dot-prefix.");
            Assert.False(
                Path.IsPathRooted(file.RelativePath),
                $"Global-scope path '{file.RelativePath}' for {target} must remain relative.");

            if (file.RelativePath.StartsWith("agents/", StringComparison.Ordinal))
                sawAgentFile = true;
            else if (file.RelativePath.StartsWith("skills/", StringComparison.Ordinal))
                sawSkillFile = true;
            else if (target == SquadTarget.ZCode &&
                     file.RelativePath.StartsWith("commands/", StringComparison.Ordinal))
                // ZCode is the only target with a third primitive: its primary agent lowers to
                // a slash command rather than to a skill.
                sawCommandFile = true;
            else
                Assert.Fail(
                    $"Global-scope path '{file.RelativePath}' for {target} is outside that " +
                    "target's declared global subtrees.");

            string physicalPath = result.Plan.ResolvePhysicalPath(file, _state.Semantics);
            Assert.True(
                _state.Semantics.IsWithin(expectedRoot, physicalPath),
                $"Physical path '{physicalPath}' for '{file.RelativePath}' ({target}) is not under " +
                $"the resolved global root '{expectedRoot}'.");
        }

        if (target == SquadTarget.Warp)
            Assert.False(sawAgentFile, $"{target} has no agent primitive; global scope must emit skills/ only.");
        else
            Assert.True(sawAgentFile, $"{target} is native and should emit at least one agents/ file.");

        // Factory uses droids/, not agents/; do not add it to this theory.

        Assert.Equal(target == SquadTarget.ZCode, sawCommandFile);

        Assert.True(sawSkillFile, $"{target} should emit at least one skills/ file.");

        // Nothing is created for a dry run, in the home or the project root.
        Assert.Empty(_state.Files.EnumerateEntries(Home));
        Assert.Empty(_state.Files.EnumerateEntries(projectRoot));
    }

    [Fact]
    public async Task InstallAsync_DryRunGlobalScope_ClaudeAndPiShareRelativePathsUnderDistinctRoots()
    {
        string projectRoot = _state.CreateRoot("multi-target-project");
        SquadGlobalRoots globalRoots = new(_ => null, Home);
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        SquadLifecycleService service = Lifecycle(releaseSource, globalRoots);

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            projectRoot,
            SquadDeploymentScope.Global,
            [SquadTarget.Claude, SquadTarget.Pi],
            Version: "1.2.3",
            DryRun: true));

        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Receipt);

        SquadOwnedFile claudeArchitect = Assert.Single(
            result.Receipt.Files,
            file => file.Target == "claude" && file.RelativePath == "agents/architect.md");
        SquadOwnedFile piArchitect = Assert.Single(
            result.Receipt.Files,
            file => file.Target == "pi" && file.RelativePath == "agents/architect.md");

        string claudePath = result.Plan.ResolvePhysicalPath(claudeArchitect, _state.Semantics);
        string piPath = result.Plan.ResolvePhysicalPath(piArchitect, _state.Semantics);
        Assert.NotEqual(claudePath, piPath);
        Assert.True(
            _state.Semantics.IsWithin(globalRoots.ResolveGlobalRoot(SquadTarget.Claude), claudePath));
        Assert.True(
            _state.Semantics.IsWithin(globalRoots.ResolveGlobalRoot(SquadTarget.Pi), piPath));
    }

    [Fact]
    public async Task InstallAsync_GlobalScope_NamingANewTargetAddsItAndKeepsTheExistingOnes()
    {
        (SquadLifecycleService service, SquadStateStore store, string project, SquadGlobalRoots roots, FakeSquadReleaseSource source) =
            GlobalService("add-target");
        using (source)
        {
            await service.InstallAsync(new SquadInstallRequest(project, SquadDeploymentScope.Global, [SquadTarget.Claude], Version: "1.2.3"));
            SquadInstallRequest addOpenCode = new(project, SquadDeploymentScope.Global, [SquadTarget.OpenCode], Version: "1.2.3");

            Assert.Equal([SquadTarget.Claude, SquadTarget.OpenCode], service.EffectiveInstallTargets(addOpenCode));
            SquadLifecycleResult added = await service.InstallAsync(addOpenCode);

            Assert.True(added.Success, string.Join("; ", added.Errors ?? Array.Empty<string>()));
            SquadReceipt receipt = store.ReadReceipt(project, SquadDeploymentScope.Global)!;
            string[] targets = [.. receipt.Files.Select(file => file.Target).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            Assert.Equal(["claude", "opencode"], targets);
            SquadOwnedFile openCodeFile = receipt.Files.First(file => file.Target == "opencode");
            Assert.True(_state.Files.FileExists(added.Plan!.ResolvePhysicalPath(openCodeFile, _state.Semantics)));
            Assert.True(_state.Files.FileExists(added.Plan!.ResolvePhysicalPath(receipt.Files.First(file => file.Target == "claude"), _state.Semantics)));
            Assert.True(_state.Semantics.IsWithin(roots.ResolveGlobalRoot(SquadTarget.OpenCode), added.Plan!.ResolvePhysicalPath(openCodeFile, _state.Semantics)));
        }
    }

    [Fact]
    public async Task InstallAsync_GlobalScope_DroppingATargetOfAnExistingInstallStillConflicts()
    {
        (SquadLifecycleService service, _, string project, _, FakeSquadReleaseSource source) = GlobalService("narrow-target");
        using (source)
        {
            await service.InstallAsync(new SquadInstallRequest(project, SquadDeploymentScope.Global, [SquadTarget.Claude, SquadTarget.OpenCode], Version: "1.2.3"));

            await Assert.ThrowsAsync<SquadDeploymentConflictException>(() => service.InstallAsync(
                new SquadInstallRequest(project, SquadDeploymentScope.Global, [SquadTarget.OpenCode], Version: "1.2.3")));
        }
    }

    /// <summary>Criterion 3: project scope is unchanged (regression guard).</summary>
    [Fact]
    public async Task InstallAsync_ProjectScope_NamingAnotherTargetStillConflicts()
    {
        (SquadLifecycleService service, _, string project, _, FakeSquadReleaseSource source) = GlobalService("project-add");
        using (source)
        {
            await service.InstallAsync(new SquadInstallRequest(project, SquadDeploymentScope.Project, [SquadTarget.Claude], Version: "1.2.3"));

            await Assert.ThrowsAsync<SquadDeploymentConflictException>(() => service.InstallAsync(
                new SquadInstallRequest(project, SquadDeploymentScope.Project, [SquadTarget.OpenCode], Version: "1.2.3")));
        }
    }

    [Fact]
    public void CreateInstall_GlobalScope_RefusesPathOwnedByAnotherProjectsReceipt()
    {
        string projectRoot = _state.CreateRoot("sibling-project");
        SquadGlobalRoots globalRoots = new(_ => null, Home);

        byte[] content = "owned-elsewhere"u8.ToArray();
        SquadReceipt sibling = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InMemorySquadState.Start,
            [],
            [
                new SquadOwnedFile(
                    "agents/architect.md",
                    Convert.ToHexStringLower(SHA256.HashData(content)),
                    "pi",
                    false)
            ]);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            _state.Planner.CreateInstall(
                projectRoot,
                SquadDeploymentScope.Global,
                GlobalLock(["pi"]),
                [new SquadDeploymentFile("agents/architect.md", content, "pi")],
                [],
                true,
                _state.Time,
                globalRoots,
                [sibling]));

        Assert.Contains("already owned by another", exception.Message, StringComparison.Ordinal);
        Assert.Contains("agents/architect.md", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUninstall_GlobalScope_LeavesFileOwnedByAnotherProjectsReceipt()
    {
        string projectRoot = _state.CreateRoot("uninstall-sibling-project");
        SquadGlobalRoots globalRoots = new(_ => null, Home);

        byte[] content = "shared-global-bytes"u8.ToArray();
        string digest = Convert.ToHexStringLower(SHA256.HashData(content));
        string physical = Path.Combine(globalRoots.ResolveGlobalRoot(SquadTarget.Pi), "agents", "architect.md");
        _state.Files.CreateDirectory(Path.GetDirectoryName(physical)!);
        _state.Files.WriteAllBytes(physical, content);

        SquadOwnedFile owned = new("agents/architect.md", digest, "pi", false);
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InMemorySquadState.Start,
            [],
            [owned]);
        SquadReceipt sibling = receipt;

        SquadDeploymentPlan plan = _state.Planner.CreateUninstall(
            projectRoot,
            SquadDeploymentScope.Global,
            receipt,
            globalRoots,
            [sibling]);

        Assert.Empty(plan.FileMutations);
        Assert.DoesNotContain(plan.Receipt.Files, file => file.RelativePath == "agents/architect.md");
        Assert.True(_state.Files.FileExists(physical));
    }

    /// <summary>Criterion 3: project scope is unchanged (regression guard).</summary>
    [Fact]
    public async Task InstallAsync_GlobalScopeDryRun_Factory_PlansDroidsAndSkillsUnderHomeFactory()
    {
        string projectRoot = _state.CreateRoot("project-factory-global");
        SquadGlobalRoots globalRoots = new(_ => null, Home);
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        SquadLifecycleService service = Lifecycle(releaseSource, globalRoots);

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            projectRoot,
            SquadDeploymentScope.Global,
            [SquadTarget.Factory],
            Version: "1.2.3",
            DryRun: true));

        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Receipt);
        Assert.NotEmpty(result.Receipt.Files);
        Assert.Equal(SquadRenderedFileCount.For(Corpus, SquadTarget.Factory), result.Receipt.Files.Count);

        string expectedRoot = globalRoots.ResolveGlobalRoot(SquadTarget.Factory);
        bool sawDroidFile = false;
        bool sawSkillFile = false;
        foreach (SquadOwnedFile file in result.Receipt.Files)
        {
            Assert.False(
                file.RelativePath.StartsWith('.'),
                $"Global-scope path '{file.RelativePath}' for Factory still carries a project-scope dot-prefix.");
            Assert.DoesNotContain(".factory", file.RelativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("agents/", file.RelativePath, StringComparison.Ordinal);

            if (file.RelativePath.StartsWith("droids/", StringComparison.Ordinal))
                sawDroidFile = true;
            else if (file.RelativePath.StartsWith("skills/", StringComparison.Ordinal))
                sawSkillFile = true;
            else
                Assert.Fail($"Global-scope path '{file.RelativePath}' for Factory is neither droids/ nor skills/.");

            string physicalPath = result.Plan.ResolvePhysicalPath(file, _state.Semantics);
            Assert.True(
                _state.Semantics.IsWithin(expectedRoot, physicalPath),
                $"Physical path '{physicalPath}' for '{file.RelativePath}' (Factory) is not under " +
                $"the resolved global root '{expectedRoot}'.");
        }

        Assert.True(sawDroidFile, "Factory should emit at least one droids/ file.");
        Assert.True(sawSkillFile, "Factory should emit at least one skills/ file.");
        Assert.Empty(_state.Files.EnumerateEntries(Home));
        Assert.Empty(_state.Files.EnumerateEntries(projectRoot));

        string agentsCompat = Path.Combine(Home, ".agents");
        string agentCompat = Path.Combine(Home, ".agent");
        Assert.False(_state.Files.DirectoryExists(agentsCompat), "Factory global install must not write ~/.agents.");
        Assert.False(_state.Files.DirectoryExists(agentCompat), "Factory global install must not write ~/.agent.");
    }

    [Fact]
    public async Task InstallAsync_ProjectScopeDryRun_Factory_UsesDroidsNotAgents()
    {
        string projectRoot = _state.CreateRoot("project-scope-factory");
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        SquadLifecycleService service = Lifecycle(releaseSource);

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            projectRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Factory],
            Version: "1.2.3",
            DryRun: true));

        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Receipt);
        Assert.NotEmpty(result.Receipt.Files);
        Assert.All(result.Receipt.Files, file =>
        {
            Assert.True(
                file.RelativePath.StartsWith(".factory/droids/", StringComparison.Ordinal) ||
                file.RelativePath.StartsWith(".factory/skills/", StringComparison.Ordinal),
                $"Project-scope Factory path '{file.RelativePath}' is not under .factory/droids/ or .factory/skills/.");
            Assert.DoesNotContain(".factory/agents/", file.RelativePath, StringComparison.Ordinal);
        });
    }

    /// <summary>Criterion 3: project scope is unchanged (regression guard).</summary>
    [Theory]
    [InlineData(SquadTarget.Claude, ".claude/")]
    [InlineData(SquadTarget.Codex, ".codex/")]
    [InlineData(SquadTarget.Cursor, ".cursor/")]
    [InlineData(SquadTarget.Copilot, ".github/")]
    [InlineData(SquadTarget.Antigravity, ".agents/")]
    [InlineData(SquadTarget.Pi, ".pi/")]
    [InlineData(SquadTarget.OpenCode, ".opencode/")]
    [InlineData(SquadTarget.Kilo, ".kilo/")]
    [InlineData(SquadTarget.ZCode, ".zcode/")]
    [InlineData(SquadTarget.Devin, ".devin/")]
    public async Task InstallAsync_ProjectScopeDryRun_RelativePathsKeepTodaysHarnessPrefix(
        SquadTarget target,
        string expectedPrefix)
    {
        // Arrange
        string projectRoot = _state.CreateRoot($"project-scope-{target}");
        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        SquadLifecycleService service = Lifecycle(releaseSource);

        SquadInstallRequest request = new(
            projectRoot,
            SquadDeploymentScope.Project,
            [target],
            Version: "1.2.3",
            DryRun: true);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Receipt);
        Assert.NotEmpty(result.Receipt.Files);

        // Antigravity (native dual-root) emits files under both .agents/agents/ and .agents/skills/.
        if (target == SquadTarget.Antigravity)
            Assert.All(
                result.Receipt.Files,
                file => Assert.True(
                    file.RelativePath.StartsWith(".agents/agents/", StringComparison.Ordinal) ||
                    file.RelativePath.StartsWith(".agents/skills/", StringComparison.Ordinal),
                    $"Antigravity file '{file.RelativePath}' is not under .agents/agents/ or .agents/skills/."));
        else
            Assert.All(
                result.Receipt.Files,
                file => Assert.StartsWith(expectedPrefix, file.RelativePath, StringComparison.Ordinal));
    }

    private (SquadLifecycleService Service, SquadStateStore Store, string Project, SquadGlobalRoots Roots, FakeSquadReleaseSource Source) GlobalService(string name)
    {
        string project = _state.CreateRoot(name);
        SquadGlobalRoots roots = new(_ => null, Home);
        FakeSquadReleaseSource source = ReleaseSource();
        return (Lifecycle(source, roots), _state.Store, project, roots, source);
    }

    /// <summary>The real lifecycle over the in-memory state, with the shipped renderers reading the in-memory tree.</summary>
    private SquadLifecycleService Lifecycle(ISquadReleaseSource releaseSource, SquadGlobalRoots? globalRoots = null) =>
        SquadTestCommands.InMemoryLifecycle(
            _state,
            releaseSource,
            new SquadRendererRegistry(SquadHostDefaults.CreateRenderers(), new InMemorySquadServices(_state.Files).RenderInputReader()),
            globalRoots);

    /// <summary>The shipped Squad source as an in-memory release the lifecycle extracts into the state's file system.</summary>
    private FakeSquadReleaseSource ReleaseSource()
    {
        string source = Path.Combine(_state.Files.Root, "release", "kyber-squad");
        _ = CanonicalSquadTree.CopyInto(_state.Files, source);
        return FakeSquadReleaseSource.FromDirectory(source, _state.Files);
    }

    private static SquadLock GlobalLock(IReadOnlyList<string> targets)
    {
        return new SquadLock(
            "kyber-squad.lock/v1",
            "1.2.3",
            "1.2.3",
            "1.2.3",
            "full",
            targets,
            [],
            "best-effort",
            "a".PadRight(64, '0'),
            "b".PadRight(64, '0'),
            new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));
    }
}

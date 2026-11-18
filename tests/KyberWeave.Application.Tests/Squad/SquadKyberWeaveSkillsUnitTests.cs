using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Testing.Fakes;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit twin of <c>SquadKyberWeaveSkillsTests</c>: the companion-skill logic over an in-memory repository. A
/// companion skill is collected beside the Squad skills, deployed by install, left alone by a same-version update and
/// removed by uninstall, and a name that clashes with a Squad skill or agent (in any case) is refused.
/// </summary>
public sealed class SquadKyberWeaveSkillsUnitTests
{
    private const string CompanionName = "kyber-weave-parity-fixture";

    private readonly InMemorySquadState _state = new();

    /// <summary>The companion skill is collected with its Squad-tree path and its source path.</summary>
    [Fact]
    public void CollectListsCompanionSkillFilesAsSquadTreeSkillPaths()
    {
        string repository = Path.GetFullPath("/repo");
        string source = Path.Combine(repository, "products", "kyber-squad");
        WriteSquadSkillAndAgent(source);
        WriteCompanion(repository, CompanionName);

        ISquadCompanionSkills companions = new InMemorySquadServices(_state.Files).CompanionSkills(repository);
        IReadOnlyList<(string RelativePath, string SourcePath)> collected =
            companions.Collect(source, companions.ResolveRoot(source));

        (string relativePath, string sourcePath) = Assert.Single(collected);
        Assert.Equal($"skills/{CompanionName}/SKILL.md", relativePath);
        Assert.Equal(Path.Combine(repository, ".apm", "skills", CompanionName, "SKILL.md"), sourcePath);
    }

    /// <summary>A companion name equal to a Squad skill or agent, in any case, fails closed naming both sides.</summary>
    [Theory]
    [InlineData("task-gate", "products/kyber-squad/skills/task-gate")]
    [InlineData("Task-Gate", "products/kyber-squad/skills/task-gate")]
    [InlineData("architect", "products/kyber-squad/agents/architect.md")]
    [InlineData("Architect", "products/kyber-squad/agents/architect.md")]
    public void CollectFailsClosedWhenACompanionNameClashesWithASquadName(string clashingName, string squadSource)
    {
        string repository = Path.GetFullPath("/repo");
        string source = Path.Combine(repository, "products", "kyber-squad");
        WriteSquadSkillAndAgent(source);
        WriteCompanion(repository, clashingName);
        ISquadCompanionSkills companions = new InMemorySquadServices(_state.Files).CompanionSkills(repository);

        SquadSourceValidationException failure = Assert.Throws<SquadSourceValidationException>(
            () => companions.Collect(source, companions.ResolveRoot(source)));

        Assert.Contains(squadSource, failure.Message, StringComparison.Ordinal);
        Assert.Contains($".apm/skills/{clashingName}", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A release that carries a companion skill installs it beside the Squad skills, a same-version update leaves the
    /// receipt unchanged, and uninstall removes the companion and the Squad skill.
    /// </summary>
    [Fact]
    public async Task InstallDeploysTheCompanionSkillBesideSquadSkillsAndUninstallRemovesThem()
    {
        string source = Path.GetFullPath("/kw/release-source");
        _ = CanonicalSquadTree.CopyInto(_state.Files, source);
        string companionDirectory = Path.Combine(source, "skills", CompanionName);
        _state.Files.CreateDirectory(companionDirectory);
        _state.Files.WriteAllText(
            Path.Combine(companionDirectory, "SKILL.md"),
            $"---\nname: {CompanionName}\ndescription: Fixture companion skill.\n---\n\n# {CompanionName}\n");

        string root = _state.CreateRoot("companion-skills");
        using FakeSquadReleaseSource release = FakeSquadReleaseSource.FromDirectory(source, _state.Files);
        ISquadRenderer renderer = new SquadRendererRegistry(
            SquadHostDefaults.CreateRenderers(),
            new InMemorySquadServices(_state.Files).RenderInputReader());
        SquadLifecycleService lifecycle = SquadTestCommands.InMemoryLifecycle(_state, release, renderer);

        SquadLifecycleResult install = await lifecycle.InstallAsync(
            new SquadInstallRequest(root, SquadDeploymentScope.Project, [SquadTarget.Claude], Version: "1.2.3"));
        Assert.True(install.Success, string.Join("; ", install.Errors ?? []));

        string skills = Path.Combine(root, ".claude", "skills");
        string[] names = [CompanionName, "task-gate"];
        Assert.All(names, name => Assert.True(_state.Files.FileExists(Path.Combine(skills, name, "SKILL.md")), name));
        SquadReceipt installed = Assert.IsType<SquadReceipt>(_state.Store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.Contains($".claude/skills/{CompanionName}/SKILL.md", installed.Files.Select(file => file.RelativePath.Replace('\\', '/')));

        SquadLifecycleResult update = await lifecycle.UpdateAsync(
            new SquadUpdateRequest(root, SquadDeploymentScope.Project, [SquadTarget.Claude], Version: "1.2.3"));
        Assert.True(update.Success, string.Join("; ", update.Errors ?? []));
        SquadReceipt updated = Assert.IsType<SquadReceipt>(_state.Store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.Equal(
            installed.Files.Select(file => (file.RelativePath, file.Sha256)).Order(),
            updated.Files.Select(file => (file.RelativePath, file.Sha256)).Order());

        SquadLifecycleResult uninstall = await lifecycle.UninstallAsync(
            new SquadUninstallRequest(root, SquadDeploymentScope.Project));
        Assert.True(uninstall.Success, string.Join("; ", uninstall.Errors ?? []));
        Assert.All(names, name => Assert.False(_state.Files.FileExists(Path.Combine(skills, name, "SKILL.md")), name));
    }

    private void WriteSquadSkillAndAgent(string source)
    {
        _state.Files.CreateDirectory(Path.Combine(source, "skills", "task-gate"));
        _state.Files.WriteAllText(Path.Combine(source, "skills", "task-gate", "SKILL.md"), "---\nname: task-gate\n---\n");
        _state.Files.CreateDirectory(Path.Combine(source, "agents"));
        _state.Files.WriteAllText(Path.Combine(source, "agents", "architect.md"), "---\nname: architect\n---\n");
    }

    private void WriteCompanion(string repository, string name)
    {
        string directory = Path.Combine(repository, ".apm", "skills", name);
        _state.Files.CreateDirectory(directory);
        _state.Files.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: Fixture.\n---\n");
    }
}

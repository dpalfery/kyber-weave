using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Proves R18/section 6a: under <c>Scope: Global</c>, every rendered file lands under its
/// target's real per-user global root with a bare <c>agents/</c> or <c>skills/</c> relative
/// path, honoring an override environment variable before the verified default. Project scope
/// stays byte-identical to today.
/// </summary>
/// <remarks>
/// <para>
/// Covers docs/archive/plans/2026-09-14-pi-harness-target.md section 7, row T12 (criteria 1-4). This
/// file declares no production type. It references a seam that does not exist yet, so the whole
/// test assembly fails to compile until T13 adds it — the intended RED signal. The predecessor
/// this file replaces declared <c>ISquadGlobalRootResolver</c> and its own fake inline, so it
/// compiled cleanly and asserted nothing real; it never touched <see cref="SquadLifecycleService"/>
/// at all.
/// </para>
/// <para>
/// The seam T13 must add, entirely under <c>KyberWeave.Core.Squad.Deployment</c>:
/// <list type="bullet">
/// <item><description>
/// <c>public interface ISquadGlobalRootResolver { string ResolveGlobalRoot(SquadTarget target); }</c>
/// </description></item>
/// <item><description>
/// <c>public sealed class SquadGlobalRoots : ISquadGlobalRootResolver</c> with constructor
/// <c>(Func&lt;string, string?&gt; getEnvironmentVariable, string homeDirectory)</c>, implementing
/// section 6a's table: for each of the six targets, read that target's override environment
/// variable name (none for Antigravity) through the injected delegate; when it returns a
/// non-empty value that is the root, otherwise the root is <c>homeDirectory</c> combined with the
/// verified default relative path (<c>.claude</c>, <c>.codex</c>, <c>.cursor</c>, <c>.copilot</c>,
/// <c>.gemini/config</c>, <c>.pi/agent</c>).
/// </description></item>
/// <item><description>
/// <see cref="SquadLifecycleService"/>'s constructor gains one new trailing optional parameter,
/// <c>ISquadGlobalRootResolver? globalRoots = null</c>, placed after the existing <c>observer</c>
/// parameter so every existing positional call site is unaffected. It is consulted only when
/// <c>SquadInstallRequest.Scope == SquadDeploymentScope.Global</c>.
/// </description></item>
/// <item><description>
/// <c>SquadDeploymentPlan</c> gains <c>internal string ResolvePhysicalPath(SquadOwnedFile file)</c>,
/// returning the absolute path this plan would write <c>file.RelativePath</c> to: under
/// <c>Scope: Project</c> that is today's only behavior (<c>PhysicalRootPath</c> combined with the
/// relative path); under <c>Scope: Global</c> it is the injected resolver's root for
/// <c>file.Target</c>, combined with the relative path. <c>internal</c> matches
/// <c>PhysicalRootPath</c>'s existing visibility; this project's <c>InternalsVisibleTo</c> grant
/// exposes it here, per this repository's "internals are in scope" test policy.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// No <c>Fakes/FakeSquadGlobalRoots.cs</c> is added. <c>SquadGlobalRoots</c>'s two constructor
/// inputs — an environment-lookup delegate and a home-directory string — are already trivially
/// fakeable inline; a wrapping fake would not earn its keep here and could drift from the real
/// resolution logic it exists to prove. No test reads the real <c>HOME</c> or
/// <c>Environment.GetEnvironmentVariable</c>.
/// </para>
/// </remarks>
public sealed class SquadGlobalRootTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---------------------------------------------------------------------------------------
    // Criterion 1: SquadGlobalRoots unit tests, a fake env dictionary, a temp home.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SquadTarget.Claude, ".claude")]
    [InlineData(SquadTarget.Codex, ".codex")]
    [InlineData(SquadTarget.Cursor, ".cursor")]
    [InlineData(SquadTarget.Copilot, ".copilot")]
    [InlineData(SquadTarget.Antigravity, ".gemini/config")]
    [InlineData(SquadTarget.Pi, ".pi/agent")]
    [InlineData(SquadTarget.OpenCode, ".config/opencode")]
    [InlineData(SquadTarget.Kilo, ".config/kilo")]
    public void ResolveGlobalRoot_NoOverrideSet_ReturnsHomeDirectoryDefault(
        SquadTarget target,
        string defaultRelativePath)
    {
        // Arrange
        string tempHome = Path.Combine(_temp.Path, "home-no-override");
        Directory.CreateDirectory(tempHome);
        SquadGlobalRoots resolver = new(_ => null, tempHome);
        string expectedRoot = Path.Combine(tempHome, defaultRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // Act
        string actualRoot = resolver.ResolveGlobalRoot(target);

        // Assert
        Assert.Equal(expectedRoot, actualRoot);
    }

    [Theory]
    [InlineData(SquadTarget.Claude, "CLAUDE_CONFIG_DIR")]
    [InlineData(SquadTarget.Codex, "CODEX_HOME")]
    [InlineData(SquadTarget.Cursor, "CURSOR_CONFIG_DIR")]
    [InlineData(SquadTarget.Copilot, "COPILOT_HOME")]
    [InlineData(SquadTarget.Pi, "PI_CODING_AGENT_DIR")]
    [InlineData(SquadTarget.OpenCode, "OPENCODE_CONFIG_DIR")]
    public void ResolveGlobalRoot_OverrideSetAndNonEmpty_ReturnsTheOverridePath(
        SquadTarget target,
        string overrideEnvironmentVariable)
    {
        // Arrange
        string tempHome = Path.Combine(_temp.Path, $"home-override-{overrideEnvironmentVariable}");
        Directory.CreateDirectory(tempHome);
        string overridePath = Path.Combine(tempHome, "override-root");
        SquadGlobalRoots resolver = new(
            name => string.Equals(name, overrideEnvironmentVariable, StringComparison.Ordinal) ? overridePath : null,
            tempHome);

        // Act
        string actualRoot = resolver.ResolveGlobalRoot(target);

        // Assert
        Assert.Equal(overridePath, actualRoot);
    }

    [Theory]
    [InlineData(SquadTarget.Claude, "CLAUDE_CONFIG_DIR", ".claude")]
    [InlineData(SquadTarget.Codex, "CODEX_HOME", ".codex")]
    [InlineData(SquadTarget.Cursor, "CURSOR_CONFIG_DIR", ".cursor")]
    [InlineData(SquadTarget.Copilot, "COPILOT_HOME", ".copilot")]
    [InlineData(SquadTarget.Pi, "PI_CODING_AGENT_DIR", ".pi/agent")]
    [InlineData(SquadTarget.OpenCode, "OPENCODE_CONFIG_DIR", ".config/opencode")]
    public void ResolveGlobalRoot_OverrideSetToEmptyString_FallsBackToTheDefault(
        SquadTarget target,
        string overrideEnvironmentVariable,
        string defaultRelativePath)
    {
        // Arrange
        string tempHome = Path.Combine(_temp.Path, $"home-empty-override-{overrideEnvironmentVariable}");
        Directory.CreateDirectory(tempHome);
        SquadGlobalRoots resolver = new(
            name => string.Equals(name, overrideEnvironmentVariable, StringComparison.Ordinal) ? string.Empty : null,
            tempHome);
        string expectedRoot = Path.Combine(tempHome, defaultRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // Act
        string actualRoot = resolver.ResolveGlobalRoot(target);

        // Assert
        Assert.Equal(expectedRoot, actualRoot);
    }

    [Theory]
    [InlineData("GEMINI_HOME")]
    [InlineData("GEMINI_CONFIG_DIR")]
    [InlineData("CLAUDE_CONFIG_DIR")]
    public void ResolveGlobalRoot_Antigravity_IgnoresEveryEnvironmentVariable(string probedVariableName)
    {
        // Arrange
        string tempHome = Path.Combine(_temp.Path, $"home-antigravity-{probedVariableName}");
        Directory.CreateDirectory(tempHome);
        SquadGlobalRoots resolver = new(_ => "should-never-be-used", tempHome);
        string expectedRoot = Path.Combine(tempHome, ".gemini", "config");

        // Act
        string actualRoot = resolver.ResolveGlobalRoot(SquadTarget.Antigravity);

        // Assert
        Assert.Equal(expectedRoot, actualRoot);
    }

    [Theory]
    [InlineData(SquadTarget.OpenCode, "opencode")]
    [InlineData(SquadTarget.Kilo, "kilo")]
    public void ResolveGlobalRoot_XdgConfigHomeSet_ReturnsXdgAppDirectory(
        SquadTarget target,
        string applicationDirectoryName)
    {
        string tempHome = Path.Combine(_temp.Path, $"home-xdg-{target}");
        Directory.CreateDirectory(tempHome);
        string xdgConfigHome = Path.Combine(tempHome, "xdg-config");
        SquadGlobalRoots resolver = new(
            name => string.Equals(name, "XDG_CONFIG_HOME", StringComparison.Ordinal) ? xdgConfigHome : null,
            tempHome);

        string actualRoot = resolver.ResolveGlobalRoot(target);

        Assert.Equal(Path.Combine(xdgConfigHome, applicationDirectoryName), actualRoot);
    }

    [Fact]
    public void ResolveGlobalRoot_OpenCode_ConfigDirOverrideBeatsXdgConfigHome()
    {
        string tempHome = Path.Combine(_temp.Path, "home-opencode-override-beats-xdg");
        Directory.CreateDirectory(tempHome);
        string overridePath = Path.Combine(tempHome, "opencode-override");
        string xdgConfigHome = Path.Combine(tempHome, "xdg-config");
        SquadGlobalRoots resolver = new(
            name => name switch
            {
                "OPENCODE_CONFIG_DIR" => overridePath,
                "XDG_CONFIG_HOME" => xdgConfigHome,
                _ => null
            },
            tempHome);

        Assert.Equal(overridePath, resolver.ResolveGlobalRoot(SquadTarget.OpenCode));
    }

    // ---------------------------------------------------------------------------------------
    // Criterion 2: a global dry run per target plans bare relative paths under the resolved root.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SquadTarget.Claude)]
    [InlineData(SquadTarget.Codex)]
    [InlineData(SquadTarget.Cursor)]
    [InlineData(SquadTarget.Copilot)]
    [InlineData(SquadTarget.Antigravity)]
    [InlineData(SquadTarget.Pi)]
    [InlineData(SquadTarget.OpenCode)]
    [InlineData(SquadTarget.Kilo)]
    public async Task InstallAsync_GlobalScopeDryRun_PlansEveryFileUnderTheResolvedTargetRootWithBareRelativePaths(
        SquadTarget target)
    {
        // Arrange
        string tempHome = Path.Combine(_temp.Path, $"home-{target}");
        Directory.CreateDirectory(tempHome);
        string projectRoot = Path.Combine(_temp.Path, $"project-{target}");
        Directory.CreateDirectory(projectRoot);
        string userData = Path.Combine(_temp.Path, $"user-data-{target}");

        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore, globalRoots: globalRoots);

        SquadInstallRequest request = new(
            TargetRoot: projectRoot,
            Scope: SquadDeploymentScope.Global,
            Targets: [target],
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
        Assert.Equal(ExpectedRenderedFileCount(), result.Receipt.Files.Count);

        bool sawAgentFile = false;
        bool sawSkillFile = false;
        foreach (SquadOwnedFile file in result.Receipt.Files)
        {
            Assert.False(
                file.RelativePath.StartsWith('.'),
                $"Global-scope path '{file.RelativePath}' for {target} still carries a project-scope dot-prefix.");

            if (file.RelativePath.StartsWith("agents/", StringComparison.Ordinal))
            {
                sawAgentFile = true;
            }
            else if (file.RelativePath.StartsWith("skills/", StringComparison.Ordinal))
            {
                sawSkillFile = true;
            }
            else
            {
                Assert.Fail($"Global-scope path '{file.RelativePath}' for {target} is neither agents/ nor skills/.");
            }

            string physicalPath = result.Plan.ResolvePhysicalPath(file);
            Assert.True(
                SquadFileSystemPathSemantics.IsWithin(expectedRoot, physicalPath),
                $"Physical path '{physicalPath}' for '{file.RelativePath}' ({target}) is not under " +
                $"the resolved global root '{expectedRoot}'.");
        }

        if (target == SquadTarget.Antigravity)
        {
            Assert.False(sawAgentFile, "Antigravity has no agent primitive; global scope must emit skills/ only.");
        }
        else
        {
            Assert.True(sawAgentFile, $"{target} is native and should emit at least one agents/ file.");
        }

        Assert.True(sawSkillFile, $"{target} should emit at least one skills/ file.");

        // Nothing is created on disk for a dry run.
        Assert.Empty(Directory.EnumerateFileSystemEntries(tempHome, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(projectRoot, "*", SearchOption.AllDirectories));
    }

    // ---------------------------------------------------------------------------------------
    // Criterion 3: project scope is unchanged (regression guard).
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SquadTarget.Claude, ".claude/")]
    [InlineData(SquadTarget.Codex, ".codex/")]
    [InlineData(SquadTarget.Cursor, ".cursor/")]
    [InlineData(SquadTarget.Copilot, ".github/")]
    [InlineData(SquadTarget.Antigravity, ".agents/skills/")]
    [InlineData(SquadTarget.Pi, ".pi/")]
    [InlineData(SquadTarget.OpenCode, ".opencode/")]
    [InlineData(SquadTarget.Kilo, ".kilo/")]
    public async Task InstallAsync_ProjectScopeDryRun_RelativePathsKeepTodaysHarnessPrefix(
        SquadTarget target,
        string expectedPrefix)
    {
        // Arrange
        string projectRoot = Path.Combine(_temp.Path, $"project-scope-{target}");
        Directory.CreateDirectory(projectRoot);
        string userData = Path.Combine(_temp.Path, $"user-data-project-{target}");

        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadInstallRequest request = new(
            TargetRoot: projectRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [target],
            Version: "1.2.3",
            DryRun: true);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Receipt);
        Assert.NotEmpty(result.Receipt.Files);
        Assert.All(
            result.Receipt.Files,
            file => Assert.StartsWith(expectedPrefix, file.RelativePath, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // Criterion 4: a real global install for pi and claude writes only into the temp home.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SquadTarget.Claude)]
    [InlineData(SquadTarget.Pi)]
    public async Task InstallAsync_RealInstallGlobalScope_WritesOnlyUnderTheResolvedHomeSubtree(SquadTarget target)
    {
        // Arrange
        string tempHome = Path.Combine(_temp.Path, $"real-home-{target}");
        Directory.CreateDirectory(tempHome);
        string projectRoot = Path.Combine(_temp.Path, $"real-project-{target}");
        Directory.CreateDirectory(projectRoot);
        string userData = Path.Combine(_temp.Path, $"real-user-data-{target}");

        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore, globalRoots: globalRoots);

        SquadInstallRequest request = new(
            TargetRoot: projectRoot,
            Scope: SquadDeploymentScope.Global,
            Targets: [target],
            Version: "1.2.3");

        string expectedRoot = globalRoots.ResolveGlobalRoot(target);
        string expectedToken = SquadTargetCatalog.GetToken(target);
        int expectedFileCount = ExpectedRenderedFileCount();

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Receipt);
        Assert.Equal(expectedFileCount, result.Receipt.Files.Count);
        Assert.All(result.Receipt.Files, file => Assert.Equal(expectedToken, file.Target));

        Assert.True(
            Directory.Exists(expectedRoot),
            $"Expected {target}'s resolved global root '{expectedRoot}' to exist after a real install.");
        string[] writtenFiles = Directory.EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories).ToArray();
        Assert.Equal(result.Receipt.Files.Count, writtenFiles.Length);

        // The project root Squad was pointed at receives no rendered files under Global scope.
        Assert.Empty(Directory.EnumerateFileSystemEntries(projectRoot, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// R17: <c>Agents.Count + Σ agent resources + Skills.Count − shared-identity skills +
    /// Σ non-suppressed skill resources</c>, read from the loaded corpus rather than a
    /// hardcoded literal. Target-agnostic per R17: a lowered primary agent still contributes
    /// exactly one principal, whether it renders as an agent or a skill.
    /// </summary>
    private static int ExpectedRenderedFileCount()
    {
        string productRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        SquadSource source = SquadSourceLoader.Load(productRoot);
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);
        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));

        return source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + source.Skills.Count - suppressedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);
    }
}

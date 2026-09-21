using KyberWeave.Cli.Commands.Squad;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Proves under <c>Scope: Global</c>, every rendered file lands under its target's real
/// per-user global root with a bare <c>agents/</c>, <c>skills/</c>, or Factory <c>droids/</c>
/// relative path, honoring an override environment variable before the verified default.
/// Project scope stays byte-identical to today. <see cref="SquadGlobalRoots"/> rejects a
/// relative home directory or override so <c>--global</c> cannot resolve against the process
/// working directory.
/// </summary>
/// <remarks>
/// No <c>Fakes/FakeSquadGlobalRoots.cs</c> is added. <c>SquadGlobalRoots</c>'s two constructor
/// inputs — an environment-lookup delegate and a home-directory string — are already trivially
/// fakeable inline; a wrapping fake would not earn its keep here and could drift from the real
/// resolution logic it exists to prove. No test reads the real <c>HOME</c> or
/// <c>Environment.GetEnvironmentVariable</c>.
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
    [InlineData(SquadTarget.Factory, ".factory")]
    [InlineData(SquadTarget.Warp, ".warp")]
    [InlineData(SquadTarget.ZCode, ".zcode")]
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
    [InlineData(SquadTarget.ZCode, "ZCODE_STORAGE_DIR")]
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
    [InlineData(SquadTarget.ZCode, "ZCODE_STORAGE_DIR", ".zcode")]
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

    [Theory]
    [InlineData("FACTORY_HOME")]
    [InlineData("FACTORY_CONFIG_DIR")]
    [InlineData("CLAUDE_CONFIG_DIR")]
    public void ResolveGlobalRoot_Factory_IgnoresEveryEnvironmentVariable(string probedVariableName)
    {
        string tempHome = Path.Combine(_temp.Path, $"home-factory-{probedVariableName}");
        Directory.CreateDirectory(tempHome);
        SquadGlobalRoots resolver = new(_ => "should-never-be-used", tempHome);
        string expectedRoot = Path.Combine(tempHome, ".factory");

        string actualRoot = resolver.ResolveGlobalRoot(SquadTarget.Factory);

        Assert.Equal(expectedRoot, actualRoot);
    }

    [Fact]
    public void Constructor_RelativeHomeDirectory_ThrowsArgumentException()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new SquadGlobalRoots(_ => null, "relative-home"));

        Assert.Equal("homeDirectory", exception.ParamName);
        Assert.Contains("fully qualified", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveGlobalRoot_RelativeOverride_ThrowsArgumentException()
    {
        string tempHome = Path.Combine(_temp.Path, "home-relative-override");
        Directory.CreateDirectory(tempHome);
        SquadGlobalRoots resolver = new(
            name => string.Equals(name, "CLAUDE_CONFIG_DIR", StringComparison.Ordinal)
                ? "relative-override"
                : null,
            tempHome);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => resolver.ResolveGlobalRoot(SquadTarget.Claude));

        Assert.Equal("CLAUDE_CONFIG_DIR", exception.ParamName);
        Assert.Contains("fully qualified", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveGlobalRoot_RelativeXdgConfigHome_ThrowsArgumentException()
    {
        string tempHome = Path.Combine(_temp.Path, "home-relative-xdg");
        Directory.CreateDirectory(tempHome);
        SquadGlobalRoots resolver = new(
            name => string.Equals(name, "XDG_CONFIG_HOME", StringComparison.Ordinal)
                ? "relative-xdg"
                : null,
            tempHome);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => resolver.ResolveGlobalRoot(SquadTarget.Kilo));

        Assert.Equal("XDG_CONFIG_HOME", exception.ParamName);
        Assert.Contains("fully qualified", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Criterion 2: a global dry run per target plans bare relative paths under the resolved root.
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------
    // ZCode is the only target whose global root can come from a config file rather than an
    // environment variable, because it is the only one whose harness resolves the value
    // through a layered runtime config. ZCode's own layering (createConfig in
    // adapters/src/config/config-factory.ts, zai-org/ZCode 3.14.0) is: system defaults, user
    // config file, project config files, then ZCODE_* environment variables — so the
    // environment outranks the file, and these tests pin that order.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ResolveGlobalRoot_ZCode_ReadsStorageDirFromTheUserConfigFile()
    {
        string tempHome = Path.Combine(_temp.Path, "zcode-config-home");
        Directory.CreateDirectory(tempHome);
        string configured = Path.Combine(_temp.Path, "zcode-elsewhere");

        SquadGlobalRoots resolver = new(
            _ => null,
            tempHome,
            path => IsZCodeCliConfig(path, tempHome) ? StorageDirConfig(configured) : null);

        Assert.Equal(configured, resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    [Fact]
    public void ResolveGlobalRoot_ZCode_ExpandsAHomeRelativeConfiguredStorageDir()
    {
        string tempHome = Path.Combine(_temp.Path, "zcode-tilde-home");
        Directory.CreateDirectory(tempHome);

        SquadGlobalRoots resolver = new(
            _ => null,
            tempHome,
            path => IsZCodeCliConfig(path, tempHome) ? StorageDirConfig("~/.zcode-beta") : null);

        Assert.Equal(Path.Combine(tempHome, ".zcode-beta"), resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    [Fact]
    public void ResolveGlobalRoot_ZCode_EnvironmentOverrideOutranksTheConfigFile()
    {
        string tempHome = Path.Combine(_temp.Path, "zcode-precedence-home");
        Directory.CreateDirectory(tempHome);
        string fromEnvironment = Path.Combine(_temp.Path, "zcode-from-env");
        string fromFile = Path.Combine(_temp.Path, "zcode-from-file");

        SquadGlobalRoots resolver = new(
            name => string.Equals(name, "ZCODE_STORAGE_DIR", StringComparison.Ordinal)
                ? fromEnvironment
                : null,
            tempHome,
            path => IsZCodeCliConfig(path, tempHome) ? StorageDirConfig(fromFile) : null);

        Assert.Equal(fromEnvironment, resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    public static TheoryData<string, string> UnusableZCodeConfigs => new()
    {
        { "empty", "" },
        { "whitespace", "   " },
        { "not-json", "not json at all" },
        { "no-storage", "{}" },
        { "no-dir", "{\"storage\":{}}" },
        { "blank-dir", "{\"storage\":{\"dir\":\"\"}}" },
        { "numeric-dir", "{\"storage\":{\"dir\":42}}" },
        { "storage-not-object", "{\"storage\":\"not-an-object\"}" },
    };

    [Theory]
    [MemberData(nameof(UnusableZCodeConfigs))]
    public void ResolveGlobalRoot_ZCode_UnusableConfigFallsBackToTheDefaultRatherThanFailing(
        string caseName,
        string configContent)
    {
        // ZCode's own loader swallows a malformed config and falls back, so resolving to a
        // root ZCode will not use would be worse than agreeing with it.
        string tempHome = Path.Combine(_temp.Path, $"zcode-bad-config-{caseName}");
        Directory.CreateDirectory(tempHome);

        SquadGlobalRoots resolver = new(
            _ => null,
            tempHome,
            path => IsZCodeCliConfig(path, tempHome) ? configContent : null);

        Assert.Equal(Path.Combine(tempHome, ".zcode"), resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    [Fact]
    public void ResolveGlobalRoot_ZCode_RejectsARelativeConfiguredStorageDir()
    {
        // A relative root would be completed against the process working directory by
        // SquadPathPolicy.ResolveFile, so a --global install could land outside the home tree.
        string tempHome = Path.Combine(_temp.Path, "zcode-relative-home");
        Directory.CreateDirectory(tempHome);

        SquadGlobalRoots resolver = new(
            _ => null,
            tempHome,
            path => IsZCodeCliConfig(path, tempHome) ? StorageDirConfig("relative/zcode") : null);

        Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    [Fact]
    public void ResolveGlobalRoot_ZCode_WithNoFileReaderUsesTheDefault()
    {
        // Every pre-existing two-argument construction keeps working: no reader means the one
        // file-backed root simply falls through.
        string tempHome = Path.Combine(_temp.Path, "zcode-no-reader-home");
        Directory.CreateDirectory(tempHome);

        SquadGlobalRoots resolver = new(_ => null, tempHome);

        Assert.Equal(Path.Combine(tempHome, ".zcode"), resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    private static string StorageDirConfig(string directory) =>
        $"{{\"storage\":{{\"dir\":{JsonSerializer.Serialize(directory)}}}}}";

    private static bool IsZCodeCliConfig(string path, string homeDirectory) =>
        string.Equals(
            path,
            Path.Combine(homeDirectory, ".zcode", "cli", "config.json"),
            StringComparison.Ordinal);

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
        bool sawCommandFile = false;
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
            else if (target == SquadTarget.ZCode &&
                     file.RelativePath.StartsWith("commands/", StringComparison.Ordinal))
            {
                // ZCode is the only target with a third primitive: its primary agent lowers to
                // a slash command rather than to a skill.
                sawCommandFile = true;
            }
            else
            {
                Assert.Fail(
                    $"Global-scope path '{file.RelativePath}' for {target} is outside that " +
                    "target's declared global subtrees.");
            }

            string physicalPath = result.Plan.ResolvePhysicalPath(file);
            Assert.True(
                SquadFileSystemPathSemantics.IsWithin(expectedRoot, physicalPath),
                $"Physical path '{physicalPath}' for '{file.RelativePath}' ({target}) is not under " +
                $"the resolved global root '{expectedRoot}'.");
        }

        if (target == SquadTarget.Antigravity || target == SquadTarget.Warp)
        {
            Assert.False(sawAgentFile, $"{target} has no agent primitive; global scope must emit skills/ only.");
        }
        else
        {
            Assert.True(sawAgentFile, $"{target} is native and should emit at least one agents/ file.");
        }

        // Factory uses droids/, not agents/; do not add it to this theory.

        Assert.Equal(target == SquadTarget.ZCode, sawCommandFile);

        Assert.True(sawSkillFile, $"{target} should emit at least one skills/ file.");

        // Nothing is created on disk for a dry run.
        Assert.Empty(Directory.EnumerateFileSystemEntries(tempHome, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(projectRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InstallAsync_DryRunGlobalScope_ClaudeAndPiShareRelativePathsUnderDistinctRoots()
    {
        string tempHome = Path.Combine(_temp.Path, "multi-target-home");
        Directory.CreateDirectory(tempHome);
        string projectRoot = Path.Combine(_temp.Path, "multi-target-project");
        Directory.CreateDirectory(projectRoot);
        string userData = Path.Combine(_temp.Path, "multi-target-user-data");

        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        SquadLifecycleService service = new(
            releaseSource,
            SquadCommandComposition.ResolveRenderer(),
            stateStore,
            globalRoots: globalRoots);

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: projectRoot,
            Scope: SquadDeploymentScope.Global,
            Targets: [SquadTarget.Claude, SquadTarget.Pi],
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

        string claudePath = result.Plan.ResolvePhysicalPath(claudeArchitect);
        string piPath = result.Plan.ResolvePhysicalPath(piArchitect);
        Assert.NotEqual(claudePath, piPath);
        Assert.True(
            SquadFileSystemPathSemantics.IsWithin(globalRoots.ResolveGlobalRoot(SquadTarget.Claude), claudePath));
        Assert.True(
            SquadFileSystemPathSemantics.IsWithin(globalRoots.ResolveGlobalRoot(SquadTarget.Pi), piPath));
    }

    [Fact]
    public void CreateInstall_GlobalScope_RefusesPathOwnedByAnotherProjectsReceipt()
    {
        string tempHome = Path.Combine(_temp.Path, "sibling-home");
        Directory.CreateDirectory(tempHome);
        string projectRoot = Path.Combine(_temp.Path, "sibling-project");
        Directory.CreateDirectory(projectRoot);
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);

        byte[] content = "owned-elsewhere"u8.ToArray();
        SquadReceipt sibling = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            DateTimeOffset.UtcNow,
            [],
            [new SquadOwnedFile(
                "agents/architect.md",
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)),
                "pi",
                false)]);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(
            () => SquadDeploymentPlan.CreateInstall(
                projectRoot,
                SquadDeploymentScope.Global,
                GlobalLock(["pi"]),
                [new SquadDeploymentFile("agents/architect.md", content, "pi")],
                [],
                adopt: true,
                TimeProvider.System,
                globalRoots,
                [sibling]));

        Assert.Contains("already owned by another", exception.Message, StringComparison.Ordinal);
        Assert.Contains("agents/architect.md", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUninstall_GlobalScope_LeavesFileOwnedByAnotherProjectsReceipt()
    {
        string tempHome = Path.Combine(_temp.Path, "uninstall-sibling-home");
        Directory.CreateDirectory(tempHome);
        string projectRoot = Path.Combine(_temp.Path, "uninstall-sibling-project");
        Directory.CreateDirectory(projectRoot);
        SquadGlobalRoots globalRoots = new(_ => null, tempHome);

        byte[] content = "shared-global-bytes"u8.ToArray();
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        string physical = Path.Combine(globalRoots.ResolveGlobalRoot(SquadTarget.Pi), "agents", "architect.md");
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        File.WriteAllBytes(physical, content);

        SquadOwnedFile owned = new("agents/architect.md", digest, "pi", false);
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            DateTimeOffset.UtcNow,
            [],
            [owned]);
        SquadReceipt sibling = receipt;

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUninstall(
            projectRoot,
            SquadDeploymentScope.Global,
            receipt,
            globalRoots,
            [sibling]);

        Assert.Empty(plan.FileMutations);
        Assert.DoesNotContain(plan.Receipt.Files, file => file.RelativePath == "agents/architect.md");
        Assert.True(File.Exists(physical));
    }

    // ---------------------------------------------------------------------------------------
    // Criterion 3: project scope is unchanged (regression guard).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task InstallAsync_GlobalScopeDryRun_Factory_PlansDroidsAndSkillsUnderHomeFactory()
    {
        string tempHome = Path.Combine(_temp.Path, "home-factory-global");
        Directory.CreateDirectory(tempHome);
        string projectRoot = Path.Combine(_temp.Path, "project-factory-global");
        Directory.CreateDirectory(projectRoot);
        string userData = Path.Combine(_temp.Path, "user-data-factory-global");

        SquadGlobalRoots globalRoots = new(_ => null, tempHome);
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore, globalRoots: globalRoots);

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: projectRoot,
            Scope: SquadDeploymentScope.Global,
            Targets: [SquadTarget.Factory],
            Version: "1.2.3",
            DryRun: true));

        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Receipt);
        Assert.NotEmpty(result.Receipt.Files);
        Assert.Equal(ExpectedRenderedFileCount(), result.Receipt.Files.Count);

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
            {
                sawDroidFile = true;
            }
            else if (file.RelativePath.StartsWith("skills/", StringComparison.Ordinal))
            {
                sawSkillFile = true;
            }
            else
            {
                Assert.Fail($"Global-scope path '{file.RelativePath}' for Factory is neither droids/ nor skills/.");
            }

            string physicalPath = result.Plan.ResolvePhysicalPath(file);
            Assert.True(
                SquadFileSystemPathSemantics.IsWithin(expectedRoot, physicalPath),
                $"Physical path '{physicalPath}' for '{file.RelativePath}' (Factory) is not under " +
                $"the resolved global root '{expectedRoot}'.");
        }

        Assert.True(sawDroidFile, "Factory should emit at least one droids/ file.");
        Assert.True(sawSkillFile, "Factory should emit at least one skills/ file.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(tempHome, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(projectRoot, "*", SearchOption.AllDirectories));

        string agentsCompat = Path.Combine(tempHome, ".agents");
        string agentCompat = Path.Combine(tempHome, ".agent");
        Assert.False(Directory.Exists(agentsCompat), "Factory global install must not write ~/.agents.");
        Assert.False(Directory.Exists(agentCompat), "Factory global install must not write ~/.agent.");
    }

    [Fact]
    public async Task InstallAsync_ProjectScopeDryRun_Factory_UsesDroidsNotAgents()
    {
        string projectRoot = Path.Combine(_temp.Path, "project-scope-factory");
        Directory.CreateDirectory(projectRoot);
        string userData = Path.Combine(_temp.Path, "user-data-project-factory");

        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            TargetRoot: projectRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Factory],
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

    [Theory]
    [InlineData(SquadTarget.Claude, ".claude/")]
    [InlineData(SquadTarget.Codex, ".codex/")]
    [InlineData(SquadTarget.Cursor, ".cursor/")]
    [InlineData(SquadTarget.Copilot, ".github/")]
    [InlineData(SquadTarget.Antigravity, ".agents/skills/")]
    [InlineData(SquadTarget.Pi, ".pi/")]
    [InlineData(SquadTarget.OpenCode, ".opencode/")]
    [InlineData(SquadTarget.Kilo, ".kilo/")]
    [InlineData(SquadTarget.ZCode, ".zcode/")]
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
    [InlineData(SquadTarget.ZCode)]
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

    private static SquadLock GlobalLock(IReadOnlyList<string> targets) =>
        new(
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

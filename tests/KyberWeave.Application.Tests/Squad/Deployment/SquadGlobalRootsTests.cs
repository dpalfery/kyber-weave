using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
///     Pins how <see cref="SquadGlobalRoots" /> resolves a target's per-user global root: the verified
///     default beneath the home, the override environment variable that outranks it, the XDG and config
///     file layers OpenCode, Kilo, ZCode and Devin add, and the inputs it refuses — a relative path, or an
///     override pointing into the squad instances directory.
/// </summary>
/// <remarks>
///     Every case runs over an in-memory file system and a fake environment, so no test reads the real
///     <c>HOME</c> or process environment. The home and every other root are derived from the file
///     system's own root, so the same cases run on a Windows leg.
/// </remarks>
public sealed class SquadGlobalRootsTests
{
    private readonly InMemoryFileSystem _files = new();

    public static TheoryData<string, string> UnusableZCodeConfigs => new()
    {
        { "empty", "" },
        { "whitespace", "   " },
        { "not-json", "not json at all" },
        { "no-storage", "{}" },
        { "no-dir", "{\"storage\":{}}" },
        { "blank-dir", "{\"storage\":{\"dir\":\"\"}}" },
        { "numeric-dir", "{\"storage\":{\"dir\":42}}" },
        { "storage-not-object", "{\"storage\":\"not-an-object\"}" }
    };

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
        string tempHome = NewHome("home-no-override");
        SquadGlobalRoots resolver = new(NoEnvironment(), tempHome);
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
        string tempHome = NewHome($"home-override-{overrideEnvironmentVariable}");
        string overridePath = Path.Combine(tempHome, "override-root");
        SquadGlobalRoots resolver = new(
            Variables((overrideEnvironmentVariable, overridePath)),
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
        string tempHome = NewHome($"home-empty-override-{overrideEnvironmentVariable}");
        SquadGlobalRoots resolver = new(
            Variables((overrideEnvironmentVariable, string.Empty)),
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
        string tempHome = NewHome($"home-antigravity-{probedVariableName}");
        SquadGlobalRoots resolver = new(Variables((probedVariableName, "should-never-be-used")), tempHome);
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
        string tempHome = NewHome($"home-xdg-{target}");
        string xdgConfigHome = Path.Combine(tempHome, "xdg-config");
        SquadGlobalRoots resolver = new(
            Variables(("XDG_CONFIG_HOME", xdgConfigHome)),
            tempHome);

        string actualRoot = resolver.ResolveGlobalRoot(target);

        Assert.Equal(Path.Combine(xdgConfigHome, applicationDirectoryName), actualRoot);
    }

    [Fact]
    public void ResolveGlobalRoot_OpenCode_ConfigDirOverrideBeatsXdgConfigHome()
    {
        string tempHome = NewHome("home-opencode-override-beats-xdg");
        string overridePath = Path.Combine(tempHome, "opencode-override");
        string xdgConfigHome = Path.Combine(tempHome, "xdg-config");
        SquadGlobalRoots resolver = new(
            Variables(
                ("OPENCODE_CONFIG_DIR", overridePath),
                ("XDG_CONFIG_HOME", xdgConfigHome)),
            tempHome);

        Assert.Equal(overridePath, resolver.ResolveGlobalRoot(SquadTarget.OpenCode));
    }

    [Theory]
    [InlineData("FACTORY_HOME")]
    [InlineData("FACTORY_CONFIG_DIR")]
    [InlineData("CLAUDE_CONFIG_DIR")]
    public void ResolveGlobalRoot_Factory_IgnoresEveryEnvironmentVariable(string probedVariableName)
    {
        string tempHome = NewHome($"home-factory-{probedVariableName}");
        SquadGlobalRoots resolver = new(Variables((probedVariableName, "should-never-be-used")), tempHome);
        string expectedRoot = Path.Combine(tempHome, ".factory");

        string actualRoot = resolver.ResolveGlobalRoot(SquadTarget.Factory);

        Assert.Equal(expectedRoot, actualRoot);
    }

    [Fact]
    public void Constructor_RelativeHomeDirectory_ThrowsArgumentException()
    {
        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => new SquadGlobalRoots(NoEnvironment(), "relative-home"));

        Assert.Equal("homeDirectory", exception.ParamName);
        Assert.Contains("fully qualified", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveGlobalRoot_RelativeOverride_ThrowsArgumentException()
    {
        string tempHome = NewHome("home-relative-override");
        SquadGlobalRoots resolver = new(
            Variables(("CLAUDE_CONFIG_DIR", "relative-override")),
            tempHome);

        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(SquadTarget.Claude));

        Assert.Equal("CLAUDE_CONFIG_DIR", exception.ParamName);
        Assert.Contains("fully qualified", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveGlobalRoot_RelativeXdgConfigHome_ThrowsArgumentException()
    {
        string tempHome = NewHome("home-relative-xdg");
        SquadGlobalRoots resolver = new(
            Variables(("XDG_CONFIG_HOME", "relative-xdg")),
            tempHome);

        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(SquadTarget.Kilo));

        Assert.Equal("XDG_CONFIG_HOME", exception.ParamName);
        Assert.Contains("fully qualified", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Requirement 7.3, 7.8: SquadGlobalRoots throws ArgumentException naming CLAUDE_CONFIG_DIR
    ///     and the invalid path when the override points into or under the squad instances directory.
    /// </summary>
    [Theory]
    [InlineData(".kyber-weave/squad/instances")]
    [InlineData(".kyber-weave/squad/instances/")]
    [InlineData(".kyber-weave/squad/instances/claude/default")]
    [InlineData(".kyber-weave/squad/instances/claude/default/subpath")]
    public void ResolveGlobalRoot_ClaudeConfigDirPointingIntoOrUnderInstances_ThrowsArgumentException(
        string relativeInstancePath)
    {
        string tempHome = NewHome("home-claude-instances");
        (string invalidPath, SquadGlobalRoots resolver) = InstanceOverride(
            tempHome,
            "CLAUDE_CONFIG_DIR",
            relativeInstancePath);

        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(SquadTarget.Claude));

        Assert.Equal("CLAUDE_CONFIG_DIR", exception.ParamName);
        Assert.Contains(invalidPath, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Requirement 7.3, 7.8: SquadGlobalRoots throws ArgumentException naming OPENCODE_CONFIG_DIR
    ///     and the invalid path when the override points into or under the squad instances directory.
    /// </summary>
    [Theory]
    [InlineData(".kyber-weave/squad/instances")]
    [InlineData(".kyber-weave/squad/instances/")]
    [InlineData(".kyber-weave/squad/instances/opencode/default")]
    [InlineData(".kyber-weave/squad/instances/opencode/default/subpath")]
    public void ResolveGlobalRoot_OpenCodeConfigDirPointingIntoOrUnderInstances_ThrowsArgumentException(
        string relativeInstancePath)
    {
        string tempHome = NewHome("home-opencode-instances");
        (string invalidPath, SquadGlobalRoots resolver) = InstanceOverride(
            tempHome,
            "OPENCODE_CONFIG_DIR",
            relativeInstancePath);

        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(SquadTarget.OpenCode));

        Assert.Equal("OPENCODE_CONFIG_DIR", exception.ParamName);
        Assert.Contains(invalidPath, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Requirement 7.3, 7.8: SquadGlobalRoots throws ArgumentException naming ZCODE_STORAGE_DIR
    ///     and the invalid path when the override points into or under the squad instances directory.
    /// </summary>
    [Theory]
    [InlineData(".kyber-weave/squad/instances")]
    [InlineData(".kyber-weave/squad/instances/")]
    [InlineData(".kyber-weave/squad/instances/zcode/default")]
    [InlineData(".kyber-weave/squad/instances/zcode/default/subpath")]
    public void ResolveGlobalRoot_ZCodeStorageDirPointingIntoOrUnderInstances_ThrowsArgumentException(
        string relativeInstancePath)
    {
        string tempHome = NewHome("home-zcode-instances");
        (string invalidPath, SquadGlobalRoots resolver) = InstanceOverride(
            tempHome,
            "ZCODE_STORAGE_DIR",
            relativeInstancePath);

        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(SquadTarget.ZCode));

        Assert.Equal("ZCODE_STORAGE_DIR", exception.ParamName);
        Assert.Contains(invalidPath, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Requirement 7.3, 7.8: SquadGlobalRoots throws ArgumentException naming XDG_CONFIG_HOME
    ///     and the invalid path when the override points into or under the squad instances directory.
    /// </summary>
    [Theory]
    [InlineData(".kyber-weave/squad/instances")]
    [InlineData(".kyber-weave/squad/instances/")]
    [InlineData(".kyber-weave/squad/instances/kilo/default")]
    [InlineData(".kyber-weave/squad/instances/kilo/default/subpath")]
    public void ResolveGlobalRoot_XdgConfigHomePointingIntoOrUnderInstances_ThrowsArgumentException(
        string relativeInstancePath)
    {
        string tempHome = NewHome("home-xdg-instances");
        (string invalidPath, SquadGlobalRoots resolver) = InstanceOverride(
            tempHome,
            "XDG_CONFIG_HOME",
            relativeInstancePath);

        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(SquadTarget.Kilo));

        Assert.Equal("XDG_CONFIG_HOME", exception.ParamName);
        Assert.Contains(invalidPath, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Requirement 7.3, 7.8: Every harness target with an override environment variable
    ///     throws ArgumentException naming the variable and the invalid path when pointing into instances.
    /// </summary>
    [Theory]
    [InlineData(SquadTarget.Claude, "CLAUDE_CONFIG_DIR")]
    [InlineData(SquadTarget.Codex, "CODEX_HOME")]
    [InlineData(SquadTarget.Cursor, "CURSOR_CONFIG_DIR")]
    [InlineData(SquadTarget.Copilot, "COPILOT_HOME")]
    [InlineData(SquadTarget.Pi, "PI_CODING_AGENT_DIR")]
    [InlineData(SquadTarget.OpenCode, "OPENCODE_CONFIG_DIR")]
    [InlineData(SquadTarget.ZCode, "ZCODE_STORAGE_DIR")]
    public void ResolveGlobalRoot_EveryHarnessOverridePointingIntoOrUnderInstances_ThrowsArgumentException(
        SquadTarget target,
        string overrideEnvironmentVariable)
    {
        string tempHome = NewHome($"home-instances-{overrideEnvironmentVariable}");
        string invalidPath = Path.Combine(tempHome, ".kyber-weave", "squad", "instances", "preset-instance");

        SquadGlobalRoots resolver = new(
            Variables((overrideEnvironmentVariable, invalidPath)),
            tempHome);

        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(target));

        Assert.Equal(overrideEnvironmentVariable, exception.ParamName);
        Assert.Contains(invalidPath, exception.Message, StringComparison.Ordinal);
    }

    // Devin is the only target whose default root differs by operating system: Devin CLI
    // v3000.x documents its user configuration at ~/.config/devin on macOS and Linux and at
    // %APPDATA%\devin on Windows, and custom subagents and skills load from agents/ and
    // skills/ beneath that directory. The platform is injected so both branches run on every
    // CI leg rather than only on the one whose OS happens to match.

    [Fact]
    public void ResolveGlobalRoot_DevinOnUnix_DefaultsToTheDotConfigDirectory()
    {
        string tempHome = NewHome("devin-unix-home");
        SquadGlobalRoots resolver = new(NoEnvironment(), tempHome, isWindows: false);

        Assert.Equal(
            Path.Combine(tempHome, ".config", "devin"),
            resolver.ResolveGlobalRoot(SquadTarget.Devin));
    }

    [Fact]
    public void ResolveGlobalRoot_DevinOnUnix_HonoursXdgConfigHome()
    {
        string tempHome = NewHome("devin-xdg-home");
        string xdgConfigHome = Path.Combine(_files.Root, "devin-xdg-config");
        SquadGlobalRoots resolver = new(
            Variables(("XDG_CONFIG_HOME", xdgConfigHome)),
            tempHome,
            isWindows: false);

        Assert.Equal(
            Path.Combine(xdgConfigHome, "devin"),
            resolver.ResolveGlobalRoot(SquadTarget.Devin));
    }

    [Fact]
    public void ResolveGlobalRoot_DevinOnWindows_UsesTheRoamingAppDataDirectory()
    {
        string tempHome = NewHome("devin-windows-home");
        string appData = Path.Combine(_files.Root, "devin-appdata-roaming");
        SquadGlobalRoots resolver = new(
            Variables(("APPDATA", appData)),
            tempHome,
            isWindows: true);

        Assert.Equal(
            Path.Combine(appData, "devin"),
            resolver.ResolveGlobalRoot(SquadTarget.Devin));
    }

    [Fact]
    public void ResolveGlobalRoot_DevinOnWindowsWithoutAppData_FallsBackBeneathTheProfile()
    {
        string tempHome = NewHome("devin-windows-no-appdata");
        SquadGlobalRoots resolver = new(NoEnvironment(), tempHome, isWindows: true);

        Assert.Equal(
            Path.Combine(tempHome, "AppData", "Roaming", "devin"),
            resolver.ResolveGlobalRoot(SquadTarget.Devin));
    }

    [Fact]
    public void ResolveGlobalRoot_DevinOnWindows_IgnoresXdgConfigHome()
    {
        string tempHome = NewHome("devin-windows-xdg");
        string appData = Path.Combine(_files.Root, "devin-windows-xdg-appdata");
        string xdgConfigHome = Path.Combine(_files.Root, "devin-windows-xdg-config");
        SquadGlobalRoots resolver = new(
            Variables(
                ("APPDATA", appData),
                ("XDG_CONFIG_HOME", xdgConfigHome)),
            tempHome,
            isWindows: true);

        Assert.Equal(
            Path.Combine(appData, "devin"),
            resolver.ResolveGlobalRoot(SquadTarget.Devin));
    }

    [Fact]
    public void ResolveGlobalRoot_DevinOnWindows_RejectsARelativeAppData()
    {
        string tempHome = NewHome("devin-windows-relative");
        SquadGlobalRoots resolver = new(
            Variables(("APPDATA", Path.Combine("relative", "roaming"))),
            tempHome,
            isWindows: true);

        ArgumentException exception =
            Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(SquadTarget.Devin));
        Assert.Equal("APPDATA", exception.ParamName);
    }

    // ZCode is the only target whose global root can come from a config file rather than an
    // environment variable, because it is the only one whose harness resolves the value
    // through a layered runtime config. ZCode's own layering (createConfig in
    // adapters/src/config/config-factory.ts, zai-org/ZCode 3.14.0) is: system defaults, user
    // config file, project config files, then ZCODE_* environment variables — so the
    // environment outranks the file, and these tests pin that order.

    [Fact]
    public void ResolveGlobalRoot_ZCode_ReadsStorageDirFromTheUserConfigFile()
    {
        string tempHome = NewHome("zcode-config-home");
        string configured = Path.Combine(_files.Root, "zcode-elsewhere");
        WriteZCodeConfig(tempHome, StorageDirConfig(configured));

        SquadGlobalRoots resolver = new(NoEnvironment(), tempHome, FileReader());

        Assert.Equal(configured, resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    [Fact]
    public void ResolveGlobalRoot_ZCode_ExpandsAHomeRelativeConfiguredStorageDir()
    {
        string tempHome = NewHome("zcode-tilde-home");
        WriteZCodeConfig(tempHome, StorageDirConfig("~/.zcode-beta"));

        SquadGlobalRoots resolver = new(NoEnvironment(), tempHome, FileReader());

        Assert.Equal(Path.Combine(tempHome, ".zcode-beta"), resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    [Fact]
    public void ResolveGlobalRoot_ZCode_EnvironmentOverrideOutranksTheConfigFile()
    {
        string tempHome = NewHome("zcode-precedence-home");
        string fromEnvironment = Path.Combine(_files.Root, "zcode-from-env");
        string fromFile = Path.Combine(_files.Root, "zcode-from-file");
        WriteZCodeConfig(tempHome, StorageDirConfig(fromFile));

        SquadGlobalRoots resolver = new(
            Variables(("ZCODE_STORAGE_DIR", fromEnvironment)),
            tempHome,
            FileReader());

        Assert.Equal(fromEnvironment, resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    [Theory]
    [MemberData(nameof(UnusableZCodeConfigs))]
    public void ResolveGlobalRoot_ZCode_UnusableConfigFallsBackToTheDefaultRatherThanFailing(
        string caseName,
        string configContent)
    {
        // ZCode's own loader swallows a malformed config and falls back, so resolving to a
        // root ZCode will not use would be worse than agreeing with it.
        string tempHome = NewHome($"zcode-bad-config-{caseName}");
        WriteZCodeConfig(tempHome, configContent);

        SquadGlobalRoots resolver = new(NoEnvironment(), tempHome, FileReader());

        Assert.Equal(Path.Combine(tempHome, ".zcode"), resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    [Fact]
    public void ResolveGlobalRoot_ZCode_RejectsARelativeConfiguredStorageDir()
    {
        // A relative root would be completed against the process working directory by
        // SquadPathPolicy.ResolveFile, so a --global install could land outside the home tree.
        string tempHome = NewHome("zcode-relative-home");
        WriteZCodeConfig(tempHome, StorageDirConfig("relative/zcode"));

        SquadGlobalRoots resolver = new(NoEnvironment(), tempHome, FileReader());

        Assert.Throws<ArgumentException>(() => resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    [Fact]
    public void ResolveGlobalRoot_ZCode_WithNoFileReaderUsesTheDefault()
    {
        // Every pre-existing two-argument construction keeps working: no reader means the one
        // file-backed root simply falls through.
        string tempHome = NewHome("zcode-no-reader-home");
        WriteZCodeConfig(tempHome, StorageDirConfig("~/.zcode-beta"));

        SquadGlobalRoots resolver = new(NoEnvironment(), tempHome);

        Assert.Equal(Path.Combine(tempHome, ".zcode"), resolver.ResolveGlobalRoot(SquadTarget.ZCode));
    }

    /// <summary>An override pointing into the instances directory, with the path its error message must name.</summary>
    private static (string InvalidPath, SquadGlobalRoots Resolver) InstanceOverride(
        string homeDirectory,
        string variableName,
        string relativeInstancePath)
    {
        string normalizedRelative = relativeInstancePath.Replace('/', Path.DirectorySeparatorChar);
        string invalidPath = Path.Combine(homeDirectory, normalizedRelative);
        if (relativeInstancePath.EndsWith('/') && !invalidPath.EndsWith(Path.DirectorySeparatorChar))
            invalidPath += Path.DirectorySeparatorChar;

        return (invalidPath, new SquadGlobalRoots(Variables((variableName, invalidPath)), homeDirectory));
    }

    private string NewHome(string name)
    {
        string home = Path.Combine(_files.Root, name);
        _files.CreateDirectory(home);
        return home;
    }

    private void WriteZCodeConfig(string homeDirectory, string content)
    {
        string configDirectory = Path.Combine(homeDirectory, ".zcode", "cli");
        _files.CreateDirectory(configDirectory);
        _files.WriteAllText(Path.Combine(configDirectory, "config.json"), content);
    }

    /// <summary>Reads a file that exists in the in-memory file system and answers null for one that does not.</summary>
    private Func<string, string?> FileReader() =>
        path => _files.FileExists(path) ? _files.ReadAllText(path) : null;

    private static Func<string, string?> NoEnvironment() =>
        new FakeProcessEnvironment(readVariable: null).GetVariable;

    private static Func<string, string?> Variables(params (string Name, string? Value)[] variables)
    {
        return new FakeProcessEnvironment(
            readVariable: name =>
            {
                foreach ((string configuredName, string? value) in variables)
                    if (string.Equals(configuredName, name, StringComparison.Ordinal))
                        return value;
                return null;
            }).GetVariable;
    }

    private static string StorageDirConfig(string directory)
    {
        return $"{{\"storage\":{{\"dir\":{JsonSerializer.Serialize(directory)}}}}}";
    }
}

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Domain.CopilotPrompt;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Kernel.FileSystem;
using KyberWeave.Tests.Doctor;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests.CopilotPrompt;

/// <summary>
/// Pins the settings splice behind <c>kyber-weave squad copilot-prompt install</c> and <c>uninstall</c>.
/// </summary>
/// <remarks>
/// The override entry is inserted into each detected build's default-profile <c>settings.json</c> as text,
/// so every byte outside the insertion must survive: comments, trailing commas and a byte-order mark included.
/// A foreign <c>*</c> entry is never overwritten. Every test runs over an in-memory home, so none can touch the
/// real VS Code settings or the real home directory.
/// </remarks>
public sealed class CopilotPromptSettingsMergeTests
{
    private const string ModelOverridesSetting = VsCodeUserSettingsPaths.SettingId;
    private const string StarEntry = "*";

    private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly string Home = Path.Combine(new InMemoryFileSystem().Root, "home");

    private readonly InMemoryFileSystem _memory = new();
    private readonly InMemoryDoctorFileSystem _reads;
    private readonly FakePathResolver _paths;
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };

    public CopilotPromptSettingsMergeTests()
    {
        _reads = new InMemoryDoctorFileSystem(_memory);
        _paths = new FakePathResolver(_memory);
        _ansi.Profile.Width = 400;
    }

    /// <summary>What a command returned and what it printed.</summary>
    private sealed record Execution(int Result, string Output);

    private string PromptPath => CopilotPromptFile.InstalledPath(Home);

    /// <summary>An environment rooted at the temp home, so no command can resolve the real home or VS Code settings.</summary>
    private CopilotPromptEnvironment NewEnvironment(bool interactive) =>
        new(Home, null, OSPlatform.OSX, interactive, _ => false, _paths, Home);

    private string SettingsPath(VsCodeFlavour flavour) =>
        VsCodeUserSettingsPaths.SettingsFile(flavour, Home, null, OSPlatform.OSX);

    private string UserDirectory(VsCodeFlavour flavour) =>
        Path.GetDirectoryName(SettingsPath(flavour))!;

    private void DetectFlavour(VsCodeFlavour flavour) =>
        _memory.CreateDirectory(UserDirectory(flavour));

    private void WriteSettings(VsCodeFlavour flavour, string text) =>
        WriteSettings(flavour, Encoding.UTF8.GetBytes(text));

    private void WriteSettings(VsCodeFlavour flavour, byte[] bytes)
    {
        DetectFlavour(flavour);
        _memory.WriteAllBytes(SettingsPath(flavour), bytes);
    }

    private Execution Install(CopilotPromptSettings settings, bool interactive = true)
    {
        CopilotPromptInstallCommand command = new(NewEnvironment(interactive), _reads, _memory, ShippedCopilotPrompt.Resources, Console());
        return Run(command, settings);
    }

    /// <summary>
    /// The settings document is read through the injected file-system seam, not the process disk: the fake holds a
    /// foreign entry and the real settings file does not exist, so only the injected read can refuse the install.
    /// </summary>
    [Fact]
    public void TheSettingsFileIsReadThroughTheInjectedFileSystemSeam()
    {
        DetectFlavour(VsCodeFlavour.Stable);
        FakeDoctorFileSystem files = new();
        files.WithFile(
            SettingsPath(VsCodeFlavour.Stable),
            $$"""
            {
              "{{ModelOverridesSetting}}": {
                "*": { "promptOverrideFile": "/someone/elses/prompt.md" }
              }
            }
            """);
        CopilotPromptInstallCommand command = new(NewEnvironment(interactive: true), files, _memory, ShippedCopilotPrompt.Resources, Console());

        Execution execution = Run(command, new CopilotPromptSettings { Yes = true });

        Assert.Equal(1, execution.Result);
        Assert.Contains("not Kyber-Weave's", execution.Output, StringComparison.Ordinal);
    }

    private Execution Uninstall(CopilotPromptSettings settings, bool interactive = true)
    {
        CopilotPromptUninstallCommand command = new(NewEnvironment(interactive), _reads, _memory, Console());
        return Run(command, settings);
    }

    private SquadConsole Console() => new(_ansi, new FakeConsole(isInputRedirected: false));

    private Execution Run(Command<CopilotPromptSettings> command, CopilotPromptSettings settings)
    {
        int result = command.Execute(null!, settings, CancellationToken.None);
        return new Execution(result, _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static bool HasByteOrderMark(byte[] bytes) =>
        bytes.Length >= ByteOrderMark.Length && bytes.AsSpan(0, ByteOrderMark.Length).SequenceEqual(ByteOrderMark);

    private static ReadOnlyMemory<byte> WithoutByteOrderMark(byte[] bytes) =>
        HasByteOrderMark(bytes) ? bytes.AsMemory(ByteOrderMark.Length) : bytes.AsMemory();

    private static T ReadRoot<T>(byte[] bytes, Func<JsonElement, T> read)
    {
        using JsonDocument document = JsonDocument.Parse(WithoutByteOrderMark(bytes), LenientJson);
        return read(document.RootElement);
    }

    /// <summary>The <c>promptOverrideFile</c> of the <c>*</c> entry, or null when there is no such entry.</summary>
    private static string? OverrideFileOf(byte[] bytes) => ReadRoot<string?>(bytes, root =>
    {
        if (!root.TryGetProperty(ModelOverridesSetting, out JsonElement overrides))
            return null;
        if (!overrides.TryGetProperty(StarEntry, out JsonElement entry))
            return null;
        return entry.TryGetProperty("promptOverrideFile", out JsonElement file) ? file.GetString() : string.Empty;
    });

    private static List<string> ModelIdsOf(byte[] bytes) => ReadRoot<List<string>>(bytes, root =>
        root.TryGetProperty(ModelOverridesSetting, out JsonElement overrides)
            ? overrides.EnumerateObject().Select(property => property.Name).ToList()
            : new List<string>());

    /// <summary>
    /// True when <paramref name="updated"/> equals <paramref name="original"/> with some bytes inserted and
    /// nothing removed or changed. This is the splice contract: every original byte survives, in order.
    /// </summary>
    private static bool IsPureInsertion(byte[] original, byte[] updated)
    {
        int extra = updated.Length - original.Length;
        if (extra < 0)
            return false;

        for (int cut = 0; cut <= original.Length; cut++)
        {
            bool head = original.AsSpan(0, cut).SequenceEqual(updated.AsSpan(0, cut));
            bool tail = original.AsSpan(cut).SequenceEqual(updated.AsSpan(cut + extra));
            if (head && tail)
                return true;
        }

        return false;
    }

    [Fact]
    public void Install_AbsentSettingsFile_CreatesFileHoldingOverrideEntry()
    {
        DetectFlavour(VsCodeFlavour.Stable);

        Execution execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.True(_memory.FileExists(SettingsPath(VsCodeFlavour.Stable)), "settings.json must be created when the User directory exists");
        Assert.Equal(PromptPath, OverrideFileOf(_memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable))));
    }

    [Fact]
    public void Install_EmptyObjectSettings_GainsOverrideEntry()
    {
        WriteSettings(VsCodeFlavour.Stable, "{}");

        Execution execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.Equal(PromptPath, OverrideFileOf(_memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable))));
    }

    [Fact]
    public void Install_CommentsTrailingCommasAndByteOrderMark_InsertsWithoutChangingAnyOtherByte()
    {
        string text = "// VS Code user settings\n{\n  \"editor.fontSize\": 14, // keep this\n  \"workbench.colorTheme\": \"Dark\",\n}\n";
        byte[] original = [.. ByteOrderMark, .. Encoding.UTF8.GetBytes(text)];
        WriteSettings(VsCodeFlavour.Stable, original);

        Execution execution = Install(new CopilotPromptSettings { Yes = true });

        byte[] updated = _memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));
        Assert.Equal(0, execution.Result);
        Assert.Equal(ByteOrderMark, updated.AsSpan(0, ByteOrderMark.Length).ToArray());
        Assert.True(IsPureInsertion(original, updated), "install may only insert bytes; an original byte was changed or removed");
        Assert.Equal(PromptPath, OverrideFileOf(updated));
    }

    [Fact]
    public void Install_ExistingOtherModelOverride_KeepsThatModelId()
    {
        WriteSettings(
            VsCodeFlavour.Stable,
            "{\n  \"chat.agentHost.copilot.modelCapabilityOverrides\": {\n    \"gpt-5\": { \"temperature\": 1 }\n  }\n}\n");

        Execution execution = Install(new CopilotPromptSettings { Yes = true });

        byte[] updated = _memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));
        Assert.Equal(0, execution.Result);
        List<string> modelIds = ModelIdsOf(updated);
        Assert.Contains("gpt-5", modelIds);
        Assert.Contains(StarEntry, modelIds);
        Assert.Equal(PromptPath, OverrideFileOf(updated));
    }

    [Fact]
    public void Install_RunTwice_SecondRunChangesNoByteAndExitsZero()
    {
        WriteSettings(VsCodeFlavour.Stable, "{}");
        Install(new CopilotPromptSettings { Yes = true });
        byte[] afterFirst = _memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));

        Execution second = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, second.Result);
        Assert.Equal(afterFirst, _memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable)));
    }

    [Theory]
    [InlineData("{ \"chat.agentHost.copilot.modelCapabilityOverrides\": { \"*\": { \"promptOverrideFile\": \"/elsewhere/prompt.yaml\" } } }")]
    [InlineData("{ \"chat.agentHost.copilot.modelCapabilityOverrides\": { \"*\": { \"temperature\": 0.2 } } }")]
    public void Install_ForeignStarEntry_RefusesWithExit1NamesEntryAndLeavesBytesUnchanged(string text)
    {
        WriteSettings(VsCodeFlavour.Stable, text);
        byte[] original = _memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));

        Execution execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(1, execution.Result);
        Assert.Equal(original, _memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable)));
        Assert.Contains(StarEntry, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_MalformedSettings_RefusesWithExit1AndLeavesBytesUnchanged()
    {
        byte[] original = Encoding.UTF8.GetBytes("{\n  \"editor.fontSize\": 14,\n  \"chat.agentHost.copilot.modelCapabilityOverrides\": {");
        WriteSettings(VsCodeFlavour.Stable, original);

        Execution execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(1, execution.Result);
        Assert.Equal(original, _memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable)));
    }

    [Fact]
    public void Uninstall_OwnEntry_RemovesOnlyStarEntryKeepsCommentsAndDeletesPromptFile()
    {
        WriteSettings(VsCodeFlavour.Stable, "// keep me\n{\n  \"editor.fontSize\": 14, // size\n}\n");
        Install(new CopilotPromptSettings { Yes = true });
        Assert.True(_memory.FileExists(PromptPath), "precondition: install must have written the prompt file");

        Execution execution = Uninstall(new CopilotPromptSettings { Yes = true });

        byte[] after = _memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));
        string text = Encoding.UTF8.GetString(after);
        Assert.Equal(0, execution.Result);
        Assert.Null(OverrideFileOf(after));
        Assert.Contains("// keep me", text, StringComparison.Ordinal);
        Assert.Contains("// size", text, StringComparison.Ordinal);
        Assert.Contains("editor.fontSize", text, StringComparison.Ordinal);
        Assert.False(_memory.FileExists(PromptPath), "uninstall must delete the installed prompt file");
    }

    [Fact]
    public void Uninstall_ForeignStarEntry_LeavesSettingsBytesUnchanged()
    {
        byte[] original = Encoding.UTF8.GetBytes(
            "{\n  \"chat.agentHost.copilot.modelCapabilityOverrides\": {\n    \"*\": { \"promptOverrideFile\": \"/elsewhere/prompt.yaml\" }\n  }\n}\n");
        WriteSettings(VsCodeFlavour.Stable, original);

        Uninstall(new CopilotPromptSettings { Yes = true });

        Assert.Equal(original, _memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable)));
    }

    [Fact]
    public void Install_OnlyStableDetected_WritesStableAndCreatesNoInsidersFile()
    {
        DetectFlavour(VsCodeFlavour.Stable);

        Execution execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.True(_memory.FileExists(SettingsPath(VsCodeFlavour.Stable)));
        Assert.False(_memory.DirectoryExists(UserDirectory(VsCodeFlavour.Insiders)), "an undetected build must not gain a User directory");
        Assert.False(_memory.FileExists(SettingsPath(VsCodeFlavour.Insiders)));
    }

    [Fact]
    public void Install_FlavourInsiders_WritesOnlyInsiders()
    {
        DetectFlavour(VsCodeFlavour.Stable);
        DetectFlavour(VsCodeFlavour.Insiders);

        Execution execution = Install(new CopilotPromptSettings { Flavour = "insiders", Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.Equal(PromptPath, OverrideFileOf(_memory.ReadAllBytes(SettingsPath(VsCodeFlavour.Insiders))));
        Assert.False(_memory.FileExists(SettingsPath(VsCodeFlavour.Stable)), "--flavour insiders must not write the stable build");
    }

    [Fact]
    public void Install_DryRun_WritesNothing()
    {
        DetectFlavour(VsCodeFlavour.Stable);

        Install(new CopilotPromptSettings { DryRun = true, Yes = true });

        Assert.False(_memory.FileExists(SettingsPath(VsCodeFlavour.Stable)));
        Assert.False(_memory.FileExists(PromptPath));
    }

    [Fact]
    public void Install_NonInteractiveWithoutYes_Exits2AndWritesNothing()
    {
        DetectFlavour(VsCodeFlavour.Stable);

        Execution execution = Install(new CopilotPromptSettings(), interactive: false);

        Assert.Equal(2, execution.Result);
        Assert.False(_memory.FileExists(SettingsPath(VsCodeFlavour.Stable)));
        Assert.False(_memory.FileExists(PromptPath));
    }

    [Theory]
    [InlineData(VsCodeFlavour.Stable, "Code")]
    [InlineData(VsCodeFlavour.Insiders, "Code - Insiders")]
    public void SettingsFile_MacOs_UsesApplicationSupportUserDirectory(VsCodeFlavour flavour, string buildDirectory)
    {
        string expected = Path.Combine(Home, "Library", "Application Support", buildDirectory, "User", "settings.json");

        Assert.Equal(expected, VsCodeUserSettingsPaths.SettingsFile(flavour, Home, null, OSPlatform.OSX));
    }

    [Theory]
    [InlineData(VsCodeFlavour.Stable, "Code")]
    [InlineData(VsCodeFlavour.Insiders, "Code - Insiders")]
    public void SettingsFile_Linux_UsesDotConfigUserDirectory(VsCodeFlavour flavour, string buildDirectory)
    {
        string expected = Path.Combine(Home, ".config", buildDirectory, "User", "settings.json");

        Assert.Equal(expected, VsCodeUserSettingsPaths.SettingsFile(flavour, Home, null, OSPlatform.Linux));
    }

    [Theory]
    [InlineData(VsCodeFlavour.Stable, "Code")]
    [InlineData(VsCodeFlavour.Insiders, "Code - Insiders")]
    public void SettingsFile_Windows_UsesAppDataUserDirectory(VsCodeFlavour flavour, string buildDirectory)
    {
        string appData = Path.Combine(Home, "AppData", "Roaming");
        string expected = Path.Combine(appData, buildDirectory, "User", "settings.json");

        Assert.Equal(expected, VsCodeUserSettingsPaths.SettingsFile(flavour, Home, appData, OSPlatform.Windows));
    }

    [Theory]
    [InlineData(VsCodeFlavour.Stable, "Code")]
    [InlineData(VsCodeFlavour.Insiders, "Code - Insiders")]
    public void SettingsFile_Linux_HonoursAbsoluteXdgConfigHome(VsCodeFlavour flavour, string buildDirectory)
    {
        string xdgConfigHome = Path.Combine(Home, "xdg-config");
        string expected = Path.Combine(xdgConfigHome, buildDirectory, "User", "settings.json");

        Assert.Equal(expected, VsCodeUserSettingsPaths.SettingsFile(flavour, Home, null, OSPlatform.Linux, xdgConfigHome));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/config")]
    public void SettingsFile_Linux_IgnoresBlankOrRelativeXdgConfigHome(string xdgConfigHome)
    {
        string expected = Path.Combine(Home, ".config", "Code", "User", "settings.json");

        Assert.Equal(expected, VsCodeUserSettingsPaths.SettingsFile(VsCodeFlavour.Stable, Home, null, OSPlatform.Linux, xdgConfigHome));
    }

    [Fact]
    public void SettingsFile_MacOs_IgnoresXdgConfigHome()
    {
        string expected = Path.Combine(Home, "Library", "Application Support", "Code", "User", "settings.json");

        Assert.Equal(expected, VsCodeUserSettingsPaths.SettingsFile(VsCodeFlavour.Stable, Home, null, OSPlatform.OSX, Path.Combine(Home, "xdg-config")));
    }

    [Fact]
    public void Detect_Linux_FindsBuildUnderXdgConfigHome()
    {
        string xdgConfigHome = Path.Combine(Home, "xdg-config");
        _memory.CreateDirectory(Path.Combine(xdgConfigHome, "Code"));

        Assert.Equal([VsCodeFlavour.Stable], VsCodeUserSettingsPaths.Detect(Home, null, OSPlatform.Linux, _memory.DirectoryExists, xdgConfigHome));
        Assert.Empty(VsCodeUserSettingsPaths.Detect(Home, null, OSPlatform.Linux, _memory.DirectoryExists));
    }

    [Fact]
    public void Install_SymlinkedSettingsFile_WritesThroughToTargetAndKeepsLink()
    {
        string dotfiles = Path.Combine(Home, "dotfiles");
        _memory.CreateDirectory(dotfiles);
        string target = Path.Combine(dotfiles, "vscode-settings.json");
        _memory.WriteAllText(target, "{}");
        DetectFlavour(VsCodeFlavour.Stable);
        _memory.CreateFileLink(SettingsPath(VsCodeFlavour.Stable), target);

        Execution execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.Equal(target, _memory.GetLinkTarget(SettingsPath(VsCodeFlavour.Stable)));
        Assert.Equal(PromptPath, OverrideFileOf(_memory.ReadAllBytes(target)));
        Assert.Single(_memory.EnumerateFiles(dotfiles, "*", recursive: false));
    }

    [Fact]
    public void Uninstall_SymlinkedSettingsFile_WritesThroughToTargetAndKeepsLink()
    {
        string dotfiles = Path.Combine(Home, "dotfiles");
        _memory.CreateDirectory(dotfiles);
        string target = Path.Combine(dotfiles, "vscode-settings.json");
        _memory.WriteAllText(target, "{}");
        DetectFlavour(VsCodeFlavour.Stable);
        _memory.CreateFileLink(SettingsPath(VsCodeFlavour.Stable), target);
        Assert.Equal(0, Install(new CopilotPromptSettings { Yes = true }).Result);

        Execution execution = Uninstall(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.Equal(target, _memory.GetLinkTarget(SettingsPath(VsCodeFlavour.Stable)));
        Assert.DoesNotContain(StarEntry, ModelIdsOf(_memory.ReadAllBytes(target)));
    }

    [Fact]
    public void EnvironmentFrom_HomeAndUserProfileDiffer_UsesHomeLikeDoctor()
    {
        string other = Path.Combine(Home, "profile");
        Dictionary<string, string?> variables = new()
        {
            ["HOME"] = Home,
            ["USERPROFILE"] = other,
            ["XDG_CONFIG_HOME"] = Path.Combine(Home, "xdg-config"),
        };

        CopilotPromptEnvironment environment = CopilotPromptCommandSupport.EnvironmentFrom(
            name => variables.GetValueOrDefault(name), OSPlatform.Linux, false, _ => false, _paths, Home);

        Assert.Equal(Home, environment.HomeDirectory);
        Assert.Equal(CopilotPromptHome.FromEnvironment(name => variables.GetValueOrDefault(name)), environment.HomeDirectory);
        Assert.Equal(Path.Combine(Home, "xdg-config"), environment.XdgConfigHome);
    }

    [Theory]
    [InlineData("/xdg-config")]
    [InlineData("  /xdg-config  ")]
    public void SettingsFile_Linux_HonoursLinuxAbsoluteXdgConfigHomeFromAnyHost(string xdgConfigHome)
    {
        string expected = Path.Combine("/xdg-config", "Code", "User", "settings.json");

        Assert.Equal(expected, VsCodeUserSettingsPaths.SettingsFile(VsCodeFlavour.Stable, Home, null, OSPlatform.Linux, xdgConfigHome));
    }

    [Fact]
    public void Install_SymlinkedSettingsFileWithMissingTarget_CreatesTargetAndKeepsLink()
    {
        string dotfiles = Path.Combine(Home, "dotfiles");
        _memory.CreateDirectory(dotfiles);
        string target = Path.Combine(dotfiles, "vscode-settings.json");
        DetectFlavour(VsCodeFlavour.Stable);
        _memory.CreateFileLink(SettingsPath(VsCodeFlavour.Stable), target);

        Execution execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.Equal(target, _memory.GetLinkTarget(SettingsPath(VsCodeFlavour.Stable)));
        Assert.Equal(PromptPath, OverrideFileOf(_memory.ReadAllBytes(target)));
    }

    [Fact]
    public void Install_NoHomeDirectory_RefusesAndWritesNothing()
    {
        DetectFlavour(VsCodeFlavour.Stable);
        CopilotPromptInstallCommand command = new(new CopilotPromptEnvironment(null, null, OSPlatform.OSX, true, _ => false, _paths, Home), _reads, _memory, ShippedCopilotPrompt.Resources, Console());

        Execution execution = Run(command, new CopilotPromptSettings { Yes = true });

        Assert.Equal(2, execution.Result);
        Assert.False(_memory.FileExists(SettingsPath(VsCodeFlavour.Stable)));
        Assert.False(_memory.FileExists(PromptPath));
    }

    [Fact]
    public void EnvironmentFrom_NoAbsoluteHome_LeavesHomeNullRatherThanFallingBack()
    {
        CopilotPromptEnvironment environment = CopilotPromptCommandSupport.EnvironmentFrom(
            name => name == "HOME" ? "relative/home" : null, OSPlatform.Linux, false, _ => false, _paths, Home);

        Assert.Null(environment.HomeDirectory);
    }

    [Fact]
    public void Install_RefusesANullFileSystemAtConstruction()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new CopilotPromptInstallCommand((IDoctorFileSystem)null!, _memory, ShippedCopilotPrompt.Resources, new FakeProcessEnvironment(), _paths, Console()));

        Assert.Equal("files", exception.ParamName);
        _ = Assert.Throws<ArgumentNullException>(
            () => new CopilotPromptInstallCommand(NewEnvironment(interactive: true), null!, _memory, ShippedCopilotPrompt.Resources, Console()));
    }

    [Fact]
    public void Uninstall_RefusesANullFileSystemAtConstruction()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new CopilotPromptUninstallCommand((IDoctorFileSystem)null!, _memory, new FakeProcessEnvironment(), _paths, Console()));

        Assert.Equal("files", exception.ParamName);
        _ = Assert.Throws<ArgumentNullException>(
            () => new CopilotPromptUninstallCommand(NewEnvironment(interactive: true), null!, _memory, Console()));
    }

    [Fact]
    public void Status_RefusesANullFileSystemAtConstruction()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new CopilotPromptStatusCommand((IDoctorFileSystem)null!, new FakeProcessEnvironment(), _paths, Console()));

        Assert.Equal("files", exception.ParamName);
        _ = Assert.Throws<ArgumentNullException>(
            () => new CopilotPromptStatusCommand(NewEnvironment(interactive: true), null!, Console()));
    }
}

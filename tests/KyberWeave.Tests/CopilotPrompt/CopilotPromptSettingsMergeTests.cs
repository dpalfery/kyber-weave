using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.CopilotPrompt;
using Xunit;

namespace KyberWeave.Tests.CopilotPrompt;

/// <summary>
/// Pins the settings splice behind <c>kyber-weave squad copilot-prompt install</c> and <c>uninstall</c>.
/// </summary>
/// <remarks>
/// The override entry is inserted into each detected build's default-profile <c>settings.json</c> as text,
/// so every byte outside the insertion must survive: comments, trailing commas and a byte-order mark included.
/// A foreign <c>*</c> entry is never overwritten. Every test runs against a temp home, so none can touch the
/// real VS Code settings or the real home directory.
/// </remarks>
public sealed class CopilotPromptSettingsMergeTests : IDisposable
{
    private const string ModelOverridesSetting = VsCodeUserSettingsPaths.SettingId;
    private const string StarEntry = "*";

    private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    private string Home => _home.Path;

    private string PromptPath => CopilotPromptFile.InstalledPath(Home);

    /// <summary>An environment rooted at the temp home, so no command can resolve the real home or VS Code settings.</summary>
    private CopilotPromptEnvironment NewEnvironment(bool interactive) =>
        new(Home, null, OSPlatform.OSX, interactive, _ => false);

    private string SettingsPath(VsCodeFlavour flavour) =>
        VsCodeUserSettingsPaths.SettingsFile(flavour, Home, null, OSPlatform.OSX);

    private string UserDirectory(VsCodeFlavour flavour) =>
        Path.GetDirectoryName(SettingsPath(flavour))!;

    private void DetectFlavour(VsCodeFlavour flavour) =>
        Directory.CreateDirectory(UserDirectory(flavour));

    private void WriteSettings(VsCodeFlavour flavour, string text) =>
        WriteSettings(flavour, Encoding.UTF8.GetBytes(text));

    private void WriteSettings(VsCodeFlavour flavour, byte[] bytes)
    {
        DetectFlavour(flavour);
        File.WriteAllBytes(SettingsPath(flavour), bytes);
    }

    private CapturedConsoleExecution<int> Install(CopilotPromptSettings settings, bool interactive = true)
    {
        CopilotPromptInstallCommand command = new(NewEnvironment(interactive));
        return ProcessConsoleCapture.Run(() => command.Execute(null!, settings, CancellationToken.None));
    }

    private CapturedConsoleExecution<int> Uninstall(CopilotPromptSettings settings, bool interactive = true)
    {
        CopilotPromptUninstallCommand command = new(NewEnvironment(interactive));
        return ProcessConsoleCapture.Run(() => command.Execute(null!, settings, CancellationToken.None));
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

        CapturedConsoleExecution<int> execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.True(File.Exists(SettingsPath(VsCodeFlavour.Stable)), "settings.json must be created when the User directory exists");
        Assert.Equal(PromptPath, OverrideFileOf(File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable))));
    }

    [Fact]
    public void Install_EmptyObjectSettings_GainsOverrideEntry()
    {
        WriteSettings(VsCodeFlavour.Stable, "{}");

        CapturedConsoleExecution<int> execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.Equal(PromptPath, OverrideFileOf(File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable))));
    }

    [Fact]
    public void Install_CommentsTrailingCommasAndByteOrderMark_InsertsWithoutChangingAnyOtherByte()
    {
        string text = "// VS Code user settings\n{\n  \"editor.fontSize\": 14, // keep this\n  \"workbench.colorTheme\": \"Dark\",\n}\n";
        byte[] original = [.. ByteOrderMark, .. Encoding.UTF8.GetBytes(text)];
        WriteSettings(VsCodeFlavour.Stable, original);

        CapturedConsoleExecution<int> execution = Install(new CopilotPromptSettings { Yes = true });

        byte[] updated = File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));
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

        CapturedConsoleExecution<int> execution = Install(new CopilotPromptSettings { Yes = true });

        byte[] updated = File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));
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
        byte[] afterFirst = File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));

        CapturedConsoleExecution<int> second = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, second.Result);
        Assert.Equal(afterFirst, File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable)));
    }

    [Theory]
    [InlineData("{ \"chat.agentHost.copilot.modelCapabilityOverrides\": { \"*\": { \"promptOverrideFile\": \"/elsewhere/prompt.yaml\" } } }")]
    [InlineData("{ \"chat.agentHost.copilot.modelCapabilityOverrides\": { \"*\": { \"temperature\": 0.2 } } }")]
    public void Install_ForeignStarEntry_RefusesWithExit1NamesEntryAndLeavesBytesUnchanged(string text)
    {
        WriteSettings(VsCodeFlavour.Stable, text);
        byte[] original = File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));

        CapturedConsoleExecution<int> execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(1, execution.Result);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable)));
        Assert.Contains(StarEntry, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_MalformedSettings_RefusesWithExit1AndLeavesBytesUnchanged()
    {
        byte[] original = Encoding.UTF8.GetBytes("{\n  \"editor.fontSize\": 14,\n  \"chat.agentHost.copilot.modelCapabilityOverrides\": {");
        WriteSettings(VsCodeFlavour.Stable, original);

        CapturedConsoleExecution<int> execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(1, execution.Result);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable)));
    }

    [Fact]
    public void Uninstall_OwnEntry_RemovesOnlyStarEntryKeepsCommentsAndDeletesPromptFile()
    {
        WriteSettings(VsCodeFlavour.Stable, "// keep me\n{\n  \"editor.fontSize\": 14, // size\n}\n");
        Install(new CopilotPromptSettings { Yes = true });
        Assert.True(File.Exists(PromptPath), "precondition: install must have written the prompt file");

        CapturedConsoleExecution<int> execution = Uninstall(new CopilotPromptSettings { Yes = true });

        byte[] after = File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable));
        string text = Encoding.UTF8.GetString(after);
        Assert.Equal(0, execution.Result);
        Assert.Null(OverrideFileOf(after));
        Assert.Contains("// keep me", text, StringComparison.Ordinal);
        Assert.Contains("// size", text, StringComparison.Ordinal);
        Assert.Contains("editor.fontSize", text, StringComparison.Ordinal);
        Assert.False(File.Exists(PromptPath), "uninstall must delete the installed prompt file");
    }

    [Fact]
    public void Uninstall_ForeignStarEntry_LeavesSettingsBytesUnchanged()
    {
        byte[] original = Encoding.UTF8.GetBytes(
            "{\n  \"chat.agentHost.copilot.modelCapabilityOverrides\": {\n    \"*\": { \"promptOverrideFile\": \"/elsewhere/prompt.yaml\" }\n  }\n}\n");
        WriteSettings(VsCodeFlavour.Stable, original);

        Uninstall(new CopilotPromptSettings { Yes = true });

        Assert.Equal(original, File.ReadAllBytes(SettingsPath(VsCodeFlavour.Stable)));
    }

    [Fact]
    public void Install_OnlyStableDetected_WritesStableAndCreatesNoInsidersFile()
    {
        DetectFlavour(VsCodeFlavour.Stable);

        CapturedConsoleExecution<int> execution = Install(new CopilotPromptSettings { Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.True(File.Exists(SettingsPath(VsCodeFlavour.Stable)));
        Assert.False(Directory.Exists(UserDirectory(VsCodeFlavour.Insiders)), "an undetected build must not gain a User directory");
        Assert.False(File.Exists(SettingsPath(VsCodeFlavour.Insiders)));
    }

    [Fact]
    public void Install_FlavourInsiders_WritesOnlyInsiders()
    {
        DetectFlavour(VsCodeFlavour.Stable);
        DetectFlavour(VsCodeFlavour.Insiders);

        CapturedConsoleExecution<int> execution = Install(new CopilotPromptSettings { Flavour = "insiders", Yes = true });

        Assert.Equal(0, execution.Result);
        Assert.Equal(PromptPath, OverrideFileOf(File.ReadAllBytes(SettingsPath(VsCodeFlavour.Insiders))));
        Assert.False(File.Exists(SettingsPath(VsCodeFlavour.Stable)), "--flavour insiders must not write the stable build");
    }

    [Fact]
    public void Install_DryRun_WritesNothing()
    {
        DetectFlavour(VsCodeFlavour.Stable);

        Install(new CopilotPromptSettings { DryRun = true, Yes = true });

        Assert.False(File.Exists(SettingsPath(VsCodeFlavour.Stable)));
        Assert.False(File.Exists(PromptPath));
    }

    [Fact]
    public void Install_NonInteractiveWithoutYes_Exits2AndWritesNothing()
    {
        DetectFlavour(VsCodeFlavour.Stable);

        CapturedConsoleExecution<int> execution = Install(new CopilotPromptSettings(), interactive: false);

        Assert.Equal(2, execution.Result);
        Assert.False(File.Exists(SettingsPath(VsCodeFlavour.Stable)));
        Assert.False(File.Exists(PromptPath));
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
}

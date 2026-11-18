using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.CopilotPrompt;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests.CopilotPrompt;

/// <summary>
/// What the three Copilot prompt commands print, ask and delete over a fake environment, fake disks and a test console.
/// </summary>
public sealed class CopilotPromptCommandsConsoleTests
{
    private const string Home = "/h";

    private readonly TestConsole _ansi = Wide();
    private readonly FakeDoctorFileSystem _reads = new();
    private readonly InMemoryFileSystem _writes = new();
    private bool _redirected = true;
    private string? _home = Home;

    private static string PromptPath => CopilotPromptFile.InstalledPath(Home);

    private static string SettingsPath => VsCodeUserSettingsPaths.SettingsFile(
        VsCodeFlavour.Stable, Home, null, System.Runtime.InteropServices.OSPlatform.Linux);

    [Fact]
    public void Status_NoHomeDirectory_RefusesWithExit2()
    {
        _home = null;

        int exit = Status().Execute(null!, new CopilotPromptSettings(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.StartsWith("kyber-weave squad copilot-prompt: error: no home directory is known.", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_NothingInstalled_SaysSoForThePromptAndEachBuild()
    {
        int exit = Status().Execute(null!, new CopilotPromptSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(
            $"Prompt file: not installed (expected {PromptPath}).\n" +
            "stable: VS Code is not installed for this user.\n" +
            "insiders: VS Code is not installed for this user.\n",
            Output());
    }

    [Fact]
    public void Status_PromptFilePresent_ReportsItInstalled()
    {
        _reads.WithFile(PromptPath, "prompt");

        Status().Execute(null!, new CopilotPromptSettings { Flavour = "stable" }, CancellationToken.None);

        Assert.StartsWith($"Prompt file: installed at {PromptPath}.\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_NoTerminalAndNoYes_RefusesBeforeReadingAnySetting()
    {
        int exit = Install().Execute(null!, new CopilotPromptSettings(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("Pass --yes to confirm when no terminal is attached.", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_AtATerminal_AsksOnTheConsoleAndDeclinesOnNo()
    {
        _redirected = false;
        _reads.WithDirectory(Path.GetDirectoryName(Path.GetDirectoryName(SettingsPath))!);
        _ansi.Profile.Capabilities.Interactive = true;
        _ansi.Input.PushTextWithEnter("n");

        int exit = Install().Execute(null!, new CopilotPromptSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Set the Copilot system prompt in", Output(), StringComparison.Ordinal);
        Assert.EndsWith("No change was made.\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_DryRun_NamesThePromptItWouldDelete()
    {
        _reads.WithFile(PromptPath, "prompt");

        int exit = Uninstall().Execute(null!, new CopilotPromptSettings { DryRun = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal($"Dry run: would delete {PromptPath}.\nDry run: nothing was changed.\n", Output());
    }

    [Fact]
    public void Uninstall_PromptInstalled_DeletesItThroughTheWritableSeam()
    {
        _reads.WithFile(PromptPath, "prompt");
        _writes.CreateDirectory(Path.GetDirectoryName(PromptPath)!);
        _writes.WriteAllText(PromptPath, "prompt");

        int exit = Uninstall().Execute(null!, new CopilotPromptSettings { Yes = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(_writes.FileExists(PromptPath));
        Assert.Equal($"Deleted {PromptPath}.\nRestart VS Code to apply it.\n", Output());
    }

    [Fact]
    public void Install_WritesThePromptAndTheSettingThroughTheWritableSeam()
    {
        ArrangeVsCode("{}");

        int exit = Install().Execute(null!, new CopilotPromptSettings { Yes = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(_writes.FileExists(PromptPath));
        Assert.Contains(PromptPath, _writes.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_ASettingsFileThatIsALink_IsUpdatedWhereTheLinkPointsAndStaysALink()
    {
        const string Target = "/h/dotfiles/settings.json";
        ArrangeVsCode("{}");
        _writes.CreateDirectory("/h/dotfiles");
        _writes.WriteAllText(Target, "{}");
        _writes.CreateFileLink(SettingsPath, Target);

        int exit = Install().Execute(null!, new CopilotPromptSettings { Yes = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(Target, _writes.GetLinkTarget(SettingsPath));
        Assert.Contains(PromptPath, _writes.ReadAllText(Target), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_ASettingsFileThatIsALinkToAMissingFile_CreatesWhereTheLinkPoints()
    {
        const string Target = "/h/dotfiles/missing.json";
        ArrangeVsCode("{}");
        _writes.CreateDirectory("/h/dotfiles");
        _writes.CreateFileLink(SettingsPath, Target);

        int exit = Install().Execute(null!, new CopilotPromptSettings { Yes = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(Target, _writes.GetLinkTarget(SettingsPath));
        Assert.Contains(PromptPath, _writes.ReadAllText(Target), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_ASettingsFileThatIsNotALink_IsWrittenInPlace()
    {
        ArrangeVsCode("{}");

        _ = Install().Execute(null!, new CopilotPromptSettings { Yes = true }, CancellationToken.None);

        Assert.Null(_writes.GetLinkTarget(SettingsPath));
        Assert.True(_writes.FileExists(SettingsPath));
    }

    [Fact]
    public void Install_AWriteThatFails_ExitsOneAndLeavesTheSettingUntouched()
    {
        ArrangeVsCode("{}");
        _writes.AddFault(FileSystemFault.FailWhen(operation => operation.Name == nameof(IWritableFileSystem.WriteAllBytesAtomic), new IOException("disk full")));

        int exit = Install().Execute(null!, new CopilotPromptSettings { Yes = true }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(_writes.FileExists(SettingsPath));
    }

    [Fact]
    public void Uninstall_ASettingsFileThatIsALink_IsUpdatedWhereTheLinkPoints()
    {
        const string Target = "/h/dotfiles/settings.json";
        string entry = $"{{\"{VsCodeUserSettingsPaths.SettingId}\": {{\"*\": {{\"promptOverrideFile\": \"{PromptPath}\"}}}}}}";
        ArrangeVsCode(entry);
        _writes.CreateDirectory("/h/dotfiles");
        _writes.WriteAllText(Target, entry);
        _writes.CreateFileLink(SettingsPath, Target);

        int exit = Uninstall().Execute(null!, new CopilotPromptSettings { Yes = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(Target, _writes.GetLinkTarget(SettingsPath));
        Assert.DoesNotContain(PromptPath, _writes.ReadAllText(Target), StringComparison.Ordinal);
    }

    private void ArrangeVsCode(string settings)
    {
        _reads.WithDirectory(Path.GetDirectoryName(Path.GetDirectoryName(SettingsPath))!);
        _reads.WithDirectory(Path.GetDirectoryName(SettingsPath)!);
        _reads.WithFile(SettingsPath, settings);
        _writes.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
    }

    private static TestConsole Wide()
    {
        TestConsole console = new() { EmitAnsiSequences = false };
        console.Profile.Width = 400;
        return console;
    }

    private FakeProcessEnvironment Environment() => new(
        operatingSystem: OperatingSystemKind.Linux,
        isInputRedirected: _redirected,
        readVariable: name => name == "HOME" ? _home : null);

    private SquadConsole Console() => new(_ansi, new FakeConsole(_redirected));

    private CopilotPromptStatusCommand Status() => new(_reads, Environment(), Console());

    private CopilotPromptInstallCommand Install() => new(_reads, _writes, new FakePathResolver(_writes), ShippedCopilotPrompt.Resources, Environment(), Console());

    private CopilotPromptUninstallCommand Uninstall() => new(_reads, _writes, new FakePathResolver(_writes), Environment(), Console());

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
}

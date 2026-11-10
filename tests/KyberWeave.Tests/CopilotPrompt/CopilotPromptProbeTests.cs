using System.Runtime.InteropServices;
using System.Text.Json;
using KyberWeave.Core.CopilotPrompt;
using KyberWeave.Core.Doctor;
using Xunit;

namespace KyberWeave.Tests.CopilotPrompt;

/// <summary>
/// Pins the doctor checks for the Copilot system-prompt override: the prompt file is present and valid,
/// the stable build's settings point at it, and the newest AgentHost log shows the override in use.
/// </summary>
/// <remarks>
/// The probe reads a real directory tree under a temp home. Log lines are fixtures; the probe never runs VS Code.
/// </remarks>
public sealed class CopilotPromptProbeTests : IDisposable
{
    private const string ValidPrompt = "systemPrompt: |\n  Probe fixture prompt.\n";
    private const string FixCommand = "kyber-weave squad copilot-prompt install";
    private const string ReplaceLine = "[Copilot:abc] Resolved system message: mode=replace (content length 120)";
    private const string AppendLine = "[Copilot:abc] Resolved system message: mode=append (content length 120)";

    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    private string Home => _home.Path;

    private string PromptPath => CopilotPromptFile.InstalledPath(Home);

    private string SettingsPath => VsCodeUserSettingsPaths.SettingsFile(VsCodeFlavour.Stable, Home, null, OSPlatform.OSX);

    private string UserDataDirectory => VsCodeUserSettingsPaths.UserDataDirectory(VsCodeFlavour.Stable, Home, null, OSPlatform.OSX);

    private IReadOnlyList<DoctorCheck> Run(DoctorMode mode = DoctorMode.Check) =>
        CopilotPromptProbe.Run(mode, Home, null, OSPlatform.OSX);

    private void WritePrompt(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PromptPath)!);
        File.WriteAllText(PromptPath, text);
    }

    /// <summary>Writes the stable build's settings so its <c>*</c> override entry points at the installed prompt file.</summary>
    private void PointSettingsAtPrompt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string file = JsonSerializer.Serialize(PromptPath);
        string json = "{ \"chat.agentHost.copilot.modelCapabilityOverrides\": { \"*\": { \"promptOverrideFile\": " + file + " } } }";
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>Writes one AgentHost log under a timestamp folder of the stable build's user data.</summary>
    private void WriteLog(string timestamp, params string[] lines)
    {
        string directory = Path.Combine(UserDataDirectory, "logs", timestamp);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "agenthost.log"), lines);
    }

    /// <summary>A fully installed override: a valid prompt file and settings that point at it.</summary>
    private void Healthy()
    {
        WritePrompt(ValidPrompt);
        PointSettingsAtPrompt();
    }

    [Fact]
    public void Run_NoPromptFile_ReturnsNoChecks()
    {
        IReadOnlyList<DoctorCheck> checks = Run();

        Assert.Empty(checks);
    }

    [Fact]
    public void Run_PromptFilePresentButNoSettingsPointAtIt_FailsWithInstallFix()
    {
        WritePrompt(ValidPrompt);

        IReadOnlyList<DoctorCheck> checks = Run();

        Assert.Contains(checks, check => !check.Passed && check.Fix?.Contains(FixCommand, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Run_SettingPresentAndPromptInvalidYaml_Fails()
    {
        WritePrompt("systemPrompt: [unclosed\n");
        PointSettingsAtPrompt();

        IReadOnlyList<DoctorCheck> checks = Run();

        Assert.Contains(checks, check => !check.Passed);
    }

    [Fact]
    public void Run_SettingPresentAndPromptUnknownKey_Fails()
    {
        WritePrompt("systemPrompt: |\n  Probe fixture prompt.\nextra: 1\n");
        PointSettingsAtPrompt();

        IReadOnlyList<DoctorCheck> checks = Run();

        Assert.Contains(checks, check => !check.Passed);
    }

    [Fact]
    public void Run_LastMatchingLogLineIsReplace_Passes()
    {
        Healthy();
        WriteLog("2026-11-10T09-00-00", AppendLine, ReplaceLine, "2026-11-10 09:00:02 info unrelated line");

        IReadOnlyList<DoctorCheck> checks = Run();

        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.True(check.Passed, check.Reason));
    }

    [Fact]
    public void Run_LastMatchingLogLineIsAppend_Fails()
    {
        Healthy();
        WriteLog("2026-11-10T09-00-00", ReplaceLine, AppendLine);

        IReadOnlyList<DoctorCheck> checks = Run();

        Assert.Contains(checks, check => !check.Passed);
    }

    [Fact]
    public void Run_NoMatchingLogLine_PassesWithWarningReason()
    {
        Healthy();
        WriteLog("2026-11-10T09-00-00", "2026-11-10 09:00:00 info startup complete");

        IReadOnlyList<DoctorCheck> checks = Run();

        Assert.All(checks, check => Assert.True(check.Passed, check.Reason));
        Assert.Contains(checks, check => check.Reason?.StartsWith("warning:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Run_OnlyNewestTimestampFolderCounts()
    {
        Healthy();
        WriteLog("2026-11-08T09-00-00", ReplaceLine);
        WriteLog("2026-11-10T09-00-00", AppendLine);

        IReadOnlyList<DoctorCheck> checks = Run();

        Assert.Contains(checks, check => !check.Passed);
    }

    [Fact]
    public void Run_EnsureMode_ReturnsEveryFailureAsPassedWithWarningReason()
    {
        WritePrompt(ValidPrompt);

        IReadOnlyList<DoctorCheck> strict = Run(DoctorMode.Check);
        IReadOnlyList<DoctorCheck> lenient = Run(DoctorMode.Ensure);

        int failures = strict.Count(check => !check.Passed);
        Assert.True(failures > 0, "the fixture must fail in Check mode for this test to mean anything");
        Assert.All(lenient, check => Assert.True(check.Passed, check.Reason));
        Assert.Equal(failures, lenient.Count(check => check.Reason?.StartsWith("warning:", StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData("/home/a", "/profile/b", "/home/a")]
    [InlineData(null, "/profile/b", "/profile/b")]
    [InlineData("", "/profile/b", "/profile/b")]
    [InlineData("  ", null, null)]
    [InlineData(null, null, null)]
    public void FromEnvironment_PrefersNonBlankHomeThenUserProfile(string? home, string? userProfile, string? expected)
    {
        Dictionary<string, string?> variables = new() { ["HOME"] = home, ["USERPROFILE"] = userProfile };

        Assert.Equal(expected, CopilotPromptHome.FromEnvironment(name => variables.GetValueOrDefault(name)));
    }

    [Fact]
    public void Run_LinuxWithXdgConfigHome_ReadsSettingsUnderIt()
    {
        WritePrompt(ValidPrompt);
        string xdgConfigHome = Path.Combine(Home, "xdg-config");
        string settings = VsCodeUserSettingsPaths.SettingsFile(VsCodeFlavour.Stable, Home, null, OSPlatform.Linux, xdgConfigHome);
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        string file = JsonSerializer.Serialize(PromptPath);
        File.WriteAllText(settings, "{ \"chat.agentHost.copilot.modelCapabilityOverrides\": { \"*\": { \"promptOverrideFile\": " + file + " } } }");

        IReadOnlyList<DoctorCheck> checks = CopilotPromptProbe.Run(DoctorMode.Check, Home, null, OSPlatform.Linux, xdgConfigHome);

        Assert.Contains(checks, check => check.Id == "copilot.prompt.stable.setting" && check.Passed);
    }
}

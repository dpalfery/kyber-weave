using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task 7 test suite: Pins the <c>kyber-weave squad preset</c> CLI command surface,
/// verbs (<c>use</c>, <c>clear</c>, <c>list</c>, <c>show</c>), outcomes, and exit codes.
/// Acceptance criteria covered:
/// - 2.1: <c>squad preset use &lt;name&gt;</c> applies preset through update transaction.
/// - 2.4: <c>--dry-run</c> prints diffs and writes nothing.
/// - 3.3: <c>squad preset clear</c> restores bundled models.
/// - 4.1: Unknown names/targets exit non-zero naming offending key.
/// - 5.1: <c>squad preset list</c> output.
/// - 5.2: <c>squad preset show</c> output.
/// </summary>
/// <remarks>
/// Drives the built CLI host as a child process rather than calling command classes directly,
/// verifying that command routing, argument parsing, strict option validation, and process
/// exit codes follow the CLI contract. Running the CLI binary compiles against the current
/// tree where the preset branch is not yet implemented, recording authentic runtime RED
/// failure evidence.
/// </remarks>
public sealed class SquadPresetCliTests : IDisposable
{
    private const string FastPresetYaml = """
                                          schema: kyber-squad.model-preset/v1
                                          profiles:
                                            architect:
                                              default: gpt-4o-mini
                                              claude: gpt-4o-mini
                                            general:
                                              default: gpt-4o-mini
                                              claude: gpt-4o-mini
                                          """;

    private readonly TempDirectory _temp = new();
    private readonly string _home;
    private readonly string _projectDir;
    private readonly FakeSquadUserPaths _userPaths;
    private readonly SquadModelPresetSelectionStore _selectionStore;
    private readonly SquadStateStore _stateStore;

    public SquadPresetCliTests()
    {
        _home = Path.Combine(_temp.Path, "user-home");
        Directory.CreateDirectory(_home);
        _projectDir = Path.Combine(_temp.Path, "project");
        Directory.CreateDirectory(_projectDir);
        _userPaths = new FakeSquadUserPaths(_home);
        Directory.CreateDirectory(_userPaths.HomeDirectory);
        _selectionStore = new SquadModelPresetSelectionStore(_userPaths);
        _stateStore = new SquadStateStore(_userPaths);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    #region Acceptance Criterion 4.1: Unknown Names and Targets Exit Non-Zero

    [Fact]
    public void UseWithUnknownPresetNameExitsWithCode2AndListsExistingPresets()
    {
        WritePreset("fast", FastPresetYaml);
        WritePreset("custom", FastPresetYaml);

        CliResult result = Run("squad", "preset", "use", "unknown-preset", "--target", "claude");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unknown-preset", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fast", result.Output, StringComparison.Ordinal);
        Assert.Contains("custom", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void UseWithUnknownTargetExitsWithCode2AndNamesOffendingTarget()
    {
        WritePreset("fast", FastPresetYaml);

        CliResult result = Run("squad", "preset", "use", "fast", "--target", "unknown-harness");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unknown-harness", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearWithUnknownTargetExitsWithCode2AndNamesOffendingTarget()
    {
        CliResult result = Run("squad", "preset", "clear", "--target", "unknown-harness");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unknown-harness", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowWithUnknownPresetNameExitsWithCode2AndNamesOffendingPreset()
    {
        CliResult result = Run("squad", "preset", "show", "nonexistent-preset", "--target", "claude");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("nonexistent-preset", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowWithUnknownTargetExitsWithCode2AndNamesOffendingTarget()
    {
        WritePreset("fast", FastPresetYaml);

        CliResult result = Run("squad", "preset", "show", "fast", "--target", "invalid-target");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("invalid-target", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Global Deployment Requirement

    [Fact]
    public void UseWhenTargetNotDeployedGloballyExitsWithCode2AndInstallGuidance()
    {
        WritePreset("fast", FastPresetYaml);

        CliResult result = Run("squad", "preset", "use", "fast", "--target", "claude", "--global");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("claude is not deployed globally", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kyber-weave squad install --global --target claude", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearWhenTargetNotDeployedGloballyExitsWithCode2AndInstallGuidance()
    {
        WritePreset("fast", FastPresetYaml);

        CliResult result = Run("squad", "preset", "clear", "--target", "claude", "--global");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("claude is not deployed globally", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kyber-weave squad install --global --target claude", result.Output, StringComparison.Ordinal);
    }

    #endregion

    #region Target Not Deployed in Project Scope

    [Fact]
    public void UseWhenTargetNotDeployedInProjectRecordsSelectionAndExitsZeroWithGuidance()
    {
        WritePreset("fast", FastPresetYaml);

        CliResult result = Run("squad", "preset", "use", "fast", "--target", "claude");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Recorded `fast` for `claude` in this project; it applies on install or `squad run`.",
            result.Output, StringComparison.Ordinal);

        SquadModelPresetSelection? selection = _selectionStore.Read(_projectDir);
        Assert.NotNull(selection);
        Assert.Equal("fast", selection.ModelPresets[SquadTarget.Claude]);
        Assert.Empty(Directory.GetFileSystemEntries(_projectDir));
    }

    #endregion

    #region Acceptance Criterion 2.1: Use on Deployed Target Updates Files via Transaction

    [Fact]
    public void UseOnDeployedProjectTargetUpdatesFilesThroughTransactionAndRecordsSelection()
    {
        WritePreset("fast", FastPresetYaml);

        string initialAgentContent = """
                                     ---
                                     name: architect
                                     model: bundled-model
                                     ---
                                     You are architect.
                                     """;
        SeedProjectDeployment(_projectDir, "claude", (".claude/agents/architect.md", initialAgentContent));

        CliResult result = Run("squad", "preset", "use", "fast", "--target", "claude");

        Assert.Equal(0, result.ExitCode);

        SquadModelPresetSelection? selection = _selectionStore.Read(_projectDir);
        Assert.NotNull(selection);
        Assert.Equal("fast", selection.ModelPresets[SquadTarget.Claude]);

        string updatedAgent = File.ReadAllText(Path.Combine(_projectDir, ".claude", "agents", "architect.md"));
        Assert.Contains("model: gpt-4o-mini", updatedAgent, StringComparison.Ordinal);
    }

    #endregion

    #region Acceptance Criterion 2.4: Dry-Run Prints Diffs and Writes Nothing

    [Fact]
    public void UseWithDryRunPrintsAgentDiffsAndWritesNothing()
    {
        WritePreset("fast", FastPresetYaml);

        string initialAgentContent = """
                                     ---
                                     name: architect
                                     model: bundled-model
                                     ---
                                     You are architect.
                                     """;
        SeedProjectDeployment(_projectDir, "claude", (".claude/agents/architect.md", initialAgentContent));

        CliResult result = Run("squad", "preset", "use", "fast", "--target", "claude", "--dry-run");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("architect: bundled-model -> gpt-4o-mini", result.Output, StringComparison.Ordinal);

        string agentContentAfter = File.ReadAllText(Path.Combine(_projectDir, ".claude", "agents", "architect.md"));
        Assert.Equal(initialAgentContent, agentContentAfter);

        SquadModelPresetSelection? selection = _selectionStore.Read(_projectDir);
        Assert.Null(selection);
    }

    #endregion

    #region Acceptance Criterion 3.3: Clear Restores Bundled Models

    [Fact]
    public void ClearOnDeployedProjectTargetRestoresBundledModelsAndClearsSelection()
    {
        string presetAgentContent = """
                                    ---
                                    name: architect
                                    model: gpt-4o-mini
                                    ---
                                    You are architect.
                                    """;
        SeedProjectDeployment(_projectDir, "claude", (".claude/agents/architect.md", presetAgentContent));
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");

        CliResult result = Run("squad", "preset", "clear", "--target", "claude");

        Assert.Equal(0, result.ExitCode);

        SquadModelPresetSelection? selection = _selectionStore.Read(_projectDir);
        Assert.Null(selection);

        string restoredAgent = File.ReadAllText(Path.Combine(_projectDir, ".claude", "agents", "architect.md"));
        Assert.DoesNotContain("gpt-4o-mini", restoredAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearWhenTargetNotDeployedInProjectClearsSelectionAndExitsZero()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");

        CliResult result = Run("squad", "preset", "clear", "--target", "claude");

        Assert.Equal(0, result.ExitCode);
        SquadModelPresetSelection? selection = _selectionStore.Read(_projectDir);
        Assert.Null(selection);
    }

    #endregion

    #region Acceptance Criterion 5.1: Squad Preset List Output

    [Fact]
    public void ListWhenPresetsExistPrintsNamePathAndUsedBy()
    {
        WritePreset("fast", FastPresetYaml);
        WritePreset("pro", FastPresetYaml);

        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");

        CliResult result = Run("squad", "preset", "list");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fast", result.Output, StringComparison.Ordinal);
        Assert.Contains("pro", result.Output, StringComparison.Ordinal);
        Assert.Contains(".kyber-weave/squad/presets/fast.yml".Replace('/', Path.DirectorySeparatorChar), result.Output,
            StringComparison.Ordinal);
        Assert.Contains("used by: project:claude", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("used by: none", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListWhenNoPresetsInUseShowsUsedByNone()
    {
        WritePreset("fast", FastPresetYaml);

        CliResult result = Run("squad", "preset", "list");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fast", result.Output, StringComparison.Ordinal);
        Assert.Contains("used by: none", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Acceptance Criterion 5.2: Squad Preset Show Output

    [Fact]
    public void ShowDisplaysResolvedModelsPerCategoryMarkingBundledValues()
    {
        WritePreset("fast", """
                            schema: kyber-squad.model-preset/v1
                            profiles:
                              general:
                                claude: custom-fast-claude
                            """);

        CliResult result = Run("squad", "preset", "show", "fast", "--target", "claude");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("general: custom-fast-claude", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("general: custom-fast-claude (bundled)", result.Output, StringComparison.Ordinal);
        Assert.Contains("(bundled)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowForWarpTargetPrintsWarpRendersNoModel()
    {
        WritePreset("fast", FastPresetYaml);

        CliResult result = Run("squad", "preset", "show", "fast", "--target", "warp");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Warp renders no model.", result.Output, StringComparison.Ordinal);
    }

    #endregion

    #region Fixtures and Helpers

    private void WritePreset(string name, string yaml)
    {
        string presetDir = Path.Combine(_userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetDir);
        string filePath = Path.Combine(presetDir, $"{name}.yml");
        File.WriteAllText(filePath, yaml, Encoding.UTF8);
    }

    private void SeedProjectDeployment(
        string targetRoot,
        string targetName,
        params (string RelativePath, string Content)[] files)
    {
        string receiptPath = _stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project);
        string lockPath = _stateStore.ResolveLockPath(targetRoot, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        List<SquadOwnedFile> ownedFiles = new();
        foreach ((string relPath, string content) in files)
        {
            string fullPath = Path.Combine(targetRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(fullPath, bytes);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ownedFiles.Add(new SquadOwnedFile(relPath, sha256, targetName, false));
        }

        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            DateTimeOffset.UtcNow,
            [],
            ownedFiles);

        SquadLock squadLock = new(
            "kyber-squad.lock/v1",
            "1.2.3",
            "1.2.3",
            "1.2.3",
            "full",
            [targetName],
            [],
            "best-effort",
            "a".PadRight(64, '0'),
            "b".PadRight(64, '0'),
            new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));

        File.WriteAllText(receiptPath, _stateStore.SerializeReceipt(receipt), Encoding.UTF8);
        File.WriteAllText(lockPath, _stateStore.SerializeLock(squadLock), Encoding.UTF8);
    }

    private CliResult Run(params string[] args)
    {
        return RunIn(_projectDir, args);
    }

    private CliResult RunIn(string workingDirectory, params string[] args)
    {
        string host = CliHostPath();
        ProcessStartInfo startInfo = new(host)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string userHome = _userPaths.HomeDirectory;
        Directory.CreateDirectory(userHome);
        startInfo.Environment["HOME"] = userHome;
        startInfo.Environment["USERPROFILE"] = userHome;
        startInfo.Environment["APPDATA"] = Path.Combine(_home, "AppData", "Roaming");
        startInfo.Environment["LOCALAPPDATA"] = Path.Combine(_home, "AppData", "Local");
        startInfo.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(userHome, ".claude");
        startInfo.Environment["CODEX_HOME"] = Path.Combine(userHome, ".codex");

        foreach (string key in new[] { "PATH", "PATHEXT", "SystemRoot", "ComSpec", "windir", "TEMP", "TMP" })
        {
            string? value = Environment.GetEnvironmentVariable(key);
            if (value is not null)
            {
                startInfo.Environment[key] = value;
            }
        }

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(30));
        return new CliResult(result.ExitCode, result.StandardOutput + "\n" + result.StandardError);
    }

    private static string CliHostPath()
    {
        string hostName = OperatingSystem.IsWindows() ? "kyber-weave.exe" : "kyber-weave";
        string host = Path.Combine(AppContext.BaseDirectory, hostName);
        Assert.True(
            File.Exists(host),
            $"The kyber-weave host '{host}' was not found in AppContext.BaseDirectory.");
        return host;
    }

    private sealed record CliResult(int ExitCode, string Output);

    #endregion
}

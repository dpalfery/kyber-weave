using System.Diagnostics;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit tests for <see cref="SquadRunCommand"/> (<c>squad run &lt;harness&gt; [-- &lt;args&gt;]</c>).
/// Acceptance criteria covered:
/// - 7.1: Concurrent instance execution with isolated roots.
/// - 7.2: Zero files written into project tree.
/// - 7.6: Harness-neutral command syntax (<c>squad run &lt;harness&gt;</c>).
/// - 7.7: Fails cleanly naming unsupported harness.
/// </summary>
public sealed class SquadRunCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _home;
    private readonly string _projectDir;
    private readonly FakeSquadUserPaths _userPaths;
    private readonly SquadModelPresetSelectionStore _selectionStore;

    public SquadRunCommandTests()
    {
        _home = Path.Combine(_temp.Path, "user-home");
        Directory.CreateDirectory(_home);
        _projectDir = Path.Combine(_temp.Path, "project");
        Directory.CreateDirectory(_projectDir);
        _userPaths = new FakeSquadUserPaths(_home);
        Directory.CreateDirectory(_userPaths.HomeDirectory);
        _selectionStore = new SquadModelPresetSelectionStore(_userPaths);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    #region Preset Selection Enforcement

    [Fact]
    public void NoSelectionRecordedForTargetExitsWithCodeTwoAndGuidance()
    {
        SquadRunCommand command = CreateCommand();
        SquadRunSettings settings = new()
        {
            Harness = "claude"
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        Assert.Equal(2, execution.Result);
        Assert.Contains(
            "No preset selected for claude in this project. Run `kyber-weave squad preset use <name> --target claude`.",
            execution.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPresetFileOnDiskExitsWithCodeOneAndDoesNotCreateStubFile()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "missing-preset");
        SquadRunCommand command = CreateCommand(
            _ => 0,
            name => $"/usr/bin/{name}");
        SquadRunSettings settings = new()
        {
            Harness = "claude"
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        Assert.Equal(1, execution.Result);
        Assert.Contains("missing-preset", execution.Output, StringComparison.OrdinalIgnoreCase);

        string presetPath = Path.Combine(_userPaths.HomeDirectory, ".kyber-weave", "squad", "presets",
            "missing-preset.yml");
        Assert.False(File.Exists(presetPath),
            "Synthetic preset stub must not be created on disk when preset is missing.");
    }

    #endregion

    #region Acceptance Criterion 7.7: Unsupported and SpikePending Harnesses Fail Cleanly

    [Fact]
    public void UnsupportedHarnessExitsWithCodeTwoAndNamesReason()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Warp, "fast");
        SquadRunCommand command = CreateCommand();
        SquadRunSettings settings = new()
        {
            Harness = "warp"
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        SquadInstanceRow warpRow = SquadInstanceCatalog.GetRow(SquadTarget.Warp);
        Assert.Equal(2, execution.Result);
        Assert.Contains("warp cannot run on a preset instance:", execution.Output, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(warpRow.Reason))
        {
            Assert.Contains(warpRow.Reason, execution.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SpikePendingHarnessExitsWithCodeTwoAndNamesReason()
    {
        _selectionStore.Set(_projectDir, SquadTarget.OpenCode, "fast");
        SquadRunCommand command = CreateCommand();
        SquadRunSettings settings = new()
        {
            Harness = "opencode"
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        Assert.Equal(2, execution.Result);
        Assert.Contains("opencode cannot run on a preset instance:", execution.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region PATH and Linking Failure Handling

    [Fact]
    public void HarnessMissingFromPathExitsWithCodeOne()
    {
        WritePreset("fast");
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");
        SquadRunCommand command = CreateCommand(findOnPath: _ => null);
        SquadRunSettings settings = new()
        {
            Harness = "claude"
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        Assert.Equal(1, execution.Result);
        Assert.Contains("claude", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SquadLinkExceptionDuringEnsureExitsWithCodeOne()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");
        SquadRunCommand command = CreateCommand(
            ensureOverride: (_, _) => throw new SquadLinkException("Simulated link creation failed."));
        SquadRunSettings settings = new()
        {
            Harness = "claude"
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        Assert.Equal(1, execution.Result);
        Assert.Contains("Simulated link creation failed.", execution.Output, StringComparison.Ordinal);
    }

    #endregion

    #region Successful Execution and Exit Code Pass-through

    [Fact]
    public void SuccessfulRunCallsEnsureAndLaunchesHarnessPassingThroughExitCode()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");

        bool ensureCalled = false;
        ProcessStartInfo? capturedStartInfo = null;
        int expectedExitCode = 42;

        SquadRunCommand command = CreateCommand(
            startInfo =>
            {
                capturedStartInfo = startInfo;
                return expectedExitCode;
            },
            ensureOverride: (target, preset) =>
            {
                ensureCalled = true;
                Assert.Equal(SquadTarget.Claude, target);
                Assert.Equal("fast", preset);
                return Task.FromResult(new SquadInstanceEnsureResult(
                    Path.Combine(_home, ".kyber-weave", "squad", "instances", "claude", "fast"),
                    []));
            },
            findOnPath: name => $"/usr/local/bin/{name}");

        SquadRunSettings settings = new()
        {
            Harness = "claude",
            Args = ["--model", "custom-model"]
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        Assert.True(ensureCalled, "EnsureAsync must be called before launching harness.");
        Assert.NotNull(capturedStartInfo);
        Assert.Equal("/usr/local/bin/claude", capturedStartInfo.FileName);
        Assert.Contains("--model", capturedStartInfo.ArgumentList);
        Assert.Contains("custom-model", capturedStartInfo.ArgumentList);
        Assert.Equal(expectedExitCode, execution.Result);
    }

    #endregion

    #region Acceptance Criterion 7.2: Zero Files Written Into Project Tree

    [Fact]
    public void SuccessfulRunLeavesProjectSourceTreeCompletelyUnmodified()
    {
        string sourceFile = Path.Combine(_projectDir, "Program.cs");
        File.WriteAllText(sourceFile, "// Original content", Encoding.UTF8);
        string[] entriesBefore = Directory.GetFileSystemEntries(_projectDir, "*", SearchOption.AllDirectories);

        WritePreset("fast");
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");
        SquadRunCommand command = CreateCommand(
            _ => 0,
            name => $"/usr/bin/{name}");

        SquadRunSettings settings = new()
        {
            Harness = "claude",
            Args = ["run"]
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        Assert.Equal(0, execution.Result);
        string[] entriesAfter = Directory.GetFileSystemEntries(_projectDir, "*", SearchOption.AllDirectories);
        Assert.Equal(entriesBefore, entriesAfter);
        Assert.Equal("// Original content", File.ReadAllText(sourceFile, Encoding.UTF8));
    }

    #endregion

    #region Acceptance Criterion 7.1: Concurrent Instance Execution

    [Fact]
    public void ConcurrentRunsExecuteWithIsolatedInstanceRoots()
    {
        WritePreset("fast");
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");
        _selectionStore.Set(_projectDir, SquadTarget.Codex, "fast");

        string? claudeRoot = null;
        string? codexRoot = null;

        SquadRunCommand claudeCommand = CreateCommand(
            startInfo =>
            {
                claudeRoot = startInfo.Environment["CLAUDE_CONFIG_DIR"];
                return 0;
            },
            name => $"/usr/bin/{name}");

        SquadRunCommand codexCommand = CreateCommand(
            startInfo =>
            {
                codexRoot = startInfo.Environment["CODEX_HOME"];
                return 0;
            },
            name => $"/usr/bin/{name}");

        CapturedConsoleExecution<int> exec1 = ProcessConsoleCapture.Run(() =>
            claudeCommand.Execute(null!, new SquadRunSettings { Harness = "claude" }));
        CapturedConsoleExecution<int> exec2 = ProcessConsoleCapture.Run(() =>
            codexCommand.Execute(null!, new SquadRunSettings { Harness = "codex" }));

        Assert.Equal(0, exec1.Result);
        Assert.Equal(0, exec2.Result);
        Assert.NotNull(claudeRoot);
        Assert.NotNull(codexRoot);
        Assert.NotEqual(claudeRoot, codexRoot);
        Assert.Contains("claude", claudeRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("codex", codexRoot, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Acceptance Criterion 7.6: Harness-Neutral Command Syntax

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("copilot")]
    [InlineData("pi")]
    public void HarnessNeutralSyntaxSupportsAnyApprovedReadyTarget(string harnessToken)
    {
        WritePreset("fast");
        SquadTarget target = SquadTargetCatalog.Parse([harnessToken])[0];
        _selectionStore.Set(_projectDir, target, "fast");

        bool launched = false;
        SquadRunCommand command = CreateCommand(
            _ =>
            {
                launched = true;
                return 0;
            },
            name => $"/bin/{name}");

        SquadRunSettings settings = new()
        {
            Harness = harnessToken
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        Assert.Equal(0, execution.Result);
        Assert.True(launched, $"Harness '{harnessToken}' must launch successfully via neutral syntax.");
    }

    #endregion

    #region Helper Methods

    private void WritePreset(string name, string? yaml = null)
    {
        string presetsDir = Path.Combine(_userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        string presetPath = Path.Combine(presetsDir, $"{name}.yml");
        string content = yaml ?? """
                                 schema: kyber-squad.model-preset/v1
                                 profiles:
                                   general:
                                     default: gpt-4o-mini
                                     claude: gpt-4o-mini
                                     codex: gpt-4o-mini
                                     copilot: gpt-4o-mini
                                     pi: gpt-4o-mini
                                 """;
        File.WriteAllText(presetPath, content, Encoding.UTF8);
    }

    private SquadRunCommand CreateCommand(
        Func<ProcessStartInfo, int>? runner = null,
        Func<string, string?>? findOnPath = null,
        Func<string, string?>? getEnv = null,
        Func<SquadTarget, string, Task<SquadInstanceEnsureResult>>? ensureOverride = null)
    {
        return new SquadRunCommand(
            _userPaths,
            _selectionStore,
            runner ?? (_ => 0),
            getEnv ?? (_ => null),
            findOnPath ?? (name => $"/usr/local/bin/{name}"),
            _projectDir,
            ensureOverride);
    }

    #endregion
}

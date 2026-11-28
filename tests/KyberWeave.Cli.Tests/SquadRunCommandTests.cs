using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit tests for <see cref="SquadRunCommand" /> (<c>squad run &lt;harness&gt; [-- &lt;args&gt;]</c>).
///     Acceptance criteria covered:
///     - 7.1: Concurrent instance execution with isolated roots.
///     - 7.6: Harness-neutral command syntax (<c>squad run &lt;harness&gt;</c>).
///     - 7.7: Fails cleanly naming unsupported harness.
/// </summary>
public sealed class SquadRunCommandTests
{
    private readonly InMemorySquadState _state = new();
    private readonly string _home;
    private readonly string _projectDir;
    private readonly SquadModelPresetSelectionStore _selectionStore;
    private readonly FakeSquadUserPaths _userPaths;

    public SquadRunCommandTests()
    {
        _home = _state.UserPaths.HomeDirectory;
        _projectDir = _state.CreateRoot("project");
        _userPaths = _state.UserPaths;
        _selectionStore = new SquadModelPresetSelectionStore(_userPaths, _state.Files, _state.Semantics);
    }

    #region Successful Execution and Exit Code Pass-through

    [Fact]
    public async Task SuccessfulRunCallsEnsureAndLaunchesHarnessPassingThroughExitCode()
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

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(
            () => command.ExecuteAsync(null!, settings, CancellationToken.None));

        Assert.True(ensureCalled, "EnsureAsync must be called before launching harness.");
        Assert.NotNull(capturedStartInfo);
        Assert.Equal("/usr/local/bin/claude", capturedStartInfo.FileName);
        Assert.Contains("--model", capturedStartInfo.ArgumentList);
        Assert.Contains("custom-model", capturedStartInfo.ArgumentList);
        Assert.Equal(expectedExitCode, execution.Result);
    }

    #endregion

    #region Acceptance Criterion 7.1: Concurrent Instance Execution

    [Fact]
    public async Task ConcurrentRunsExecuteWithIsolatedInstanceRoots()
    {
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
            name => $"/usr/bin/{name}",
            ensureOverride: EnsuredInstance);

        SquadRunCommand codexCommand = CreateCommand(
            startInfo =>
            {
                codexRoot = startInfo.Environment["CODEX_HOME"];
                return 0;
            },
            name => $"/usr/bin/{name}",
            ensureOverride: EnsuredInstance);

        CapturedConsoleExecution<int> exec1 = await ProcessConsoleCapture.RunAsync(() =>
            claudeCommand.ExecuteAsync(null!, new SquadRunSettings { Harness = "claude" }, CancellationToken.None));
        CapturedConsoleExecution<int> exec2 = await ProcessConsoleCapture.RunAsync(() =>
            codexCommand.ExecuteAsync(null!, new SquadRunSettings { Harness = "codex" }, CancellationToken.None));

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
    public async Task HarnessNeutralSyntaxSupportsAnyApprovedReadyTarget(string harnessToken)
    {
        SquadTarget target = SquadTargetCatalog.Parse([harnessToken])[0];
        _selectionStore.Set(_projectDir, target, "fast");

        bool launched = false;
        SquadRunCommand command = CreateCommand(
            _ =>
            {
                launched = true;
                return 0;
            },
            name => $"/bin/{name}",
            ensureOverride: EnsuredInstance);

        SquadRunSettings settings = new()
        {
            Harness = harnessToken
        };

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(
            () => command.ExecuteAsync(null!, settings, CancellationToken.None));

        Assert.Equal(0, execution.Result);
        Assert.True(launched, $"Harness '{harnessToken}' must launch successfully via neutral syntax.");
    }

    #endregion

    #region Preset Selection Enforcement

    [Fact]
    public async Task NoSelectionRecordedForTargetExitsWithCodeTwoAndGuidance()
    {
        SquadRunCommand command = CreateCommand();
        SquadRunSettings settings = new()
        {
            Harness = "claude"
        };

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(
            () => command.ExecuteAsync(null!, settings, CancellationToken.None));

        Assert.Equal(2, execution.Result);
        Assert.Contains(
            "No preset selected for claude in this project. Run `kyber-weave squad preset use <name> --target claude`.",
            execution.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPresetFileOnDiskExitsWithCodeOneAndDoesNotCreateStubFile()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "missing-preset");
        SquadRunCommand command = CreateCommand(
            _ => 0,
            name => $"/usr/bin/{name}");
        SquadRunSettings settings = new()
        {
            Harness = "claude"
        };

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(
            () => command.ExecuteAsync(null!, settings, CancellationToken.None));

        Assert.Equal(1, execution.Result);
        Assert.Contains("missing-preset", execution.Output, StringComparison.OrdinalIgnoreCase);

        string presetPath = Path.Combine(_userPaths.HomeDirectory, ".kyber-weave", "squad", "presets",
            "missing-preset.yml");
        Assert.False(_state.Files.FileExists(presetPath),
            "Synthetic preset stub must not be created on disk when preset is missing.");
    }

    #endregion

    #region Acceptance Criterion 7.7: Unsupported and SpikePending Harnesses Fail Cleanly

    [Fact]
    public async Task UnsupportedHarnessExitsWithCodeTwoAndNamesReason()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Warp, "fast");
        SquadRunCommand command = CreateCommand();
        SquadRunSettings settings = new()
        {
            Harness = "warp"
        };

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(
            () => command.ExecuteAsync(null!, settings, CancellationToken.None));

        SquadInstanceRow warpRow = SquadInstanceCatalog.GetRow(SquadTarget.Warp);
        Assert.Equal(2, execution.Result);
        Assert.Contains("warp cannot run on a preset instance:", execution.Output, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(warpRow.Reason))
            Assert.Contains(warpRow.Reason, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpikePendingHarnessExitsWithCodeTwoAndNamesReason()
    {
        _selectionStore.Set(_projectDir, SquadTarget.OpenCode, "fast");
        SquadRunCommand command = CreateCommand();
        SquadRunSettings settings = new()
        {
            Harness = "opencode"
        };

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(
            () => command.ExecuteAsync(null!, settings, CancellationToken.None));

        Assert.Equal(2, execution.Result);
        Assert.Contains("opencode cannot run on a preset instance:", execution.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region PATH and Linking Failure Handling

    [Fact]
    public async Task HarnessMissingFromPathExitsWithCodeOne()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");
        SquadRunCommand command = CreateCommand(findOnPath: _ => null, ensureOverride: EnsuredInstance);
        SquadRunSettings settings = new()
        {
            Harness = "claude"
        };

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(
            () => command.ExecuteAsync(null!, settings, CancellationToken.None));

        Assert.Equal(1, execution.Result);
        Assert.Contains("claude", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SquadLinkExceptionDuringEnsureExitsWithCodeOne()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");
        SquadRunCommand command = CreateCommand(
            ensureOverride: (_, _) => throw new SquadLinkException("Simulated link creation failed."));
        SquadRunSettings settings = new()
        {
            Harness = "claude"
        };

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(
            () => command.ExecuteAsync(null!, settings, CancellationToken.None));

        Assert.Equal(1, execution.Result);
        Assert.Contains("Simulated link creation failed.", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgumentContainingShellMetacharacterExitsWithCodeTwo()
    {
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");
        SquadRunCommand command = CreateCommand(ensureOverride: EnsuredInstance);
        SquadRunSettings settings = new()
        {
            Harness = "claude",
            Args = ["echo", "foo&bar"]
        };

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(
            () => command.ExecuteAsync(null!, settings, CancellationToken.None));

        Assert.Equal(2, execution.Result);
        Assert.Contains("dangerous characters", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    ///     A stand-in for the real <c>squad instance ensure</c>: the instance root a preset instance would live in,
    ///     without installing one. A real ensure renders and commits a whole bundle, which takes seconds, and the rows
    ///     that use this assert what <c>squad run</c> does with the root it is given, not that the install works.
    ///     <c>SquadRunCommandRealEnsureTests</c> runs the real ensure end to end.
    /// </summary>
    private Task<SquadInstanceEnsureResult> EnsuredInstance(SquadTarget target, string preset) =>
        Task.FromResult(new SquadInstanceEnsureResult(
            Path.Combine(_home, ".kyber-weave", "squad", "instances", SquadTargetCatalog.GetToken(target), preset),
            []));

    private SquadRunCommand CreateCommand(
        Func<ProcessStartInfo, int>? runner = null,
        Func<string, string?>? findOnPath = null,
        Func<string, string?>? getEnv = null,
        Func<SquadTarget, string, Task<SquadInstanceEnsureResult>>? ensureOverride = null)
    {
        return SquadTestCommands.InMemoryRun(
            _state,
            _selectionStore,
            runner ?? (_ => 0),
            getEnv ?? (_ => null),
            findOnPath ?? (name => $"/usr/local/bin/{name}"),
            _projectDir,
            ensureOverride);
    }

    #endregion
}

using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     CLI tests for Kyber-Squad harness instances:
///     - <c>squad instance remove --target &lt;t&gt; --preset &lt;name&gt; [--yes]</c>
///     - <c>squad status --global</c> (Instances section)
///     - <c>squad update --global</c> (instance refresh step)
///     Acceptance criteria:
///     - 7.3: Instance folder management under ~/.kyber-weave/.
///     - 7.8: Safe deletion/refresh of instance entries without affecting target files in main home.
/// </summary>
public sealed class SquadInstanceCliTests
{
    private readonly InMemorySquadState _state = new();
    private readonly string _source;
    private readonly ISquadRenderer _renderer;

    public SquadInstanceCliTests()
    {
        _source = Path.Combine(_state.Files.Root, "release", "kyber-squad");
        _ = CanonicalSquadTree.CopyInto(_state.Files, _source);
        _renderer = new SquadRendererRegistry(
            SquadHostDefaults.CreateRenderers(),
            new InMemorySquadServices(_state.Files).RenderInputReader());
    }

    #region squad instance remove Tests

    /// <summary>
    ///     Verifies that <c>squad instance remove --target claude --preset fast --yes</c> deletes the instance
    ///     directory and state directory under ~/.kyber-weave/ while leaving target files in the main home intact (AC 7.8).
    /// </summary>
    [Fact]
    public async Task InstanceRemoveDeletesInstanceDirectoryAndStateWhenYesFlagSpecified()
    {
        ISquadUserPaths userPaths = _state.UserPaths;
        string mainClaudeHome = Path.Combine(userPaths.HomeDirectory, ".claude");
        _state.Files.CreateDirectory(mainClaudeHome);
        string mainFile = Path.Combine(mainClaudeHome, "main-config.json");
        _state.Files.WriteAllText(mainFile, "{\"preserved\": true}");

        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));

        string home = userPaths.HomeDirectory;
        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", "claude", "fast");
        string instanceStateDir = Path.Combine(home, ".kyber-weave", "squad", "instance-state", "claude", "fast");

        Assert.True(_state.Files.DirectoryExists(instanceRoot));
        Assert.True(_state.Files.DirectoryExists(instanceStateDir));

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = "fast",
                Yes = true
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.False(_state.Files.DirectoryExists(instanceRoot), "Instance directory must be deleted.");
        Assert.False(_state.Files.DirectoryExists(instanceStateDir), "Instance state directory must be deleted.");
        Assert.True(_state.Files.FileExists(mainFile), "Target files in main harness home must remain intact (AC 7.8).");
    }

    /// <summary>
    ///     Verifies that declining the interactive confirmation prompt preserves instance directories and state,
    ///     exiting with code 2 without mutating filesystem (similar to uninstall command).
    /// </summary>
    [Fact]
    public async Task InstanceRemoveDeclinedConfirmationPreservesInstanceAndExitsTwo()
    {
        ISquadUserPaths userPaths = _state.UserPaths;
        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));

        string home = userPaths.HomeDirectory;
        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", "claude", "fast");
        string instanceStateDir = Path.Combine(home, ".kyber-weave", "squad", "instance-state", "claude", "fast");

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: true,
            readAnswer: static _ => false);

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = "fast",
                Yes = false
            },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Declined", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(_state.Files.DirectoryExists(instanceRoot), "Instance directory must be preserved when prompt is declined.");
        Assert.True(_state.Files.DirectoryExists(instanceStateDir),
            "Instance state directory must be preserved when prompt is declined.");
    }

    /// <summary>
    ///     Verifies that accepting the interactive confirmation prompt removes the instance and exits 0.
    /// </summary>
    [Fact]
    public async Task InstanceRemoveConfirmedPromptDeletesInstanceAndExitsZero()
    {
        ISquadUserPaths userPaths = _state.UserPaths;
        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));

        string home = userPaths.HomeDirectory;
        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", "claude", "fast");
        string instanceStateDir = Path.Combine(home, ".kyber-weave", "squad", "instance-state", "claude", "fast");

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: true,
            readAnswer: static _ => true);

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = "fast",
                Yes = false
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.False(_state.Files.DirectoryExists(instanceRoot));
        Assert.False(_state.Files.DirectoryExists(instanceStateDir));
    }

    /// <summary>
    ///     Verifies that attempting to remove an unknown or uninstalled instance exits with code 2.
    /// </summary>
    [Fact]
    public async Task InstanceRemoveExitsTwoWhenInstanceDoesNotExist()
    {
        ISquadUserPaths userPaths = _state.UserPaths;

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = "nonexistent",
                Yes = true
            },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("not found", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies that passing an unknown or invalid target token exits with code 2 before any filesystem mutation.
    /// </summary>
    [Fact]
    public async Task InstanceRemoveExitsTwoWhenTargetIsInvalid()
    {
        ISquadUserPaths userPaths = _state.UserPaths;

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "invalid-target",
                Preset = "fast",
                Yes = true
            },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Unknown Squad target", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies that omitting the required preset parameter exits with code 2.
    /// </summary>
    [Fact]
    public async Task InstanceRemoveExitsTwoWhenPresetIsMissing()
    {
        ISquadUserPaths userPaths = _state.UserPaths;

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = null,
                Yes = true
            },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("preset", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Regression test: Path traversal attempt in preset name exits with code 2 without mutating
    ///     directories outside the designated instance path.
    /// </summary>
    [Theory]
    [InlineData("../../etc")]
    [InlineData("../traversal")]
    public async Task InstanceRemoveRejectsPathTraversalAttemptExitingTwoWithoutTouchingExternalDirectories(
        string invalidPreset)
    {
        ISquadUserPaths userPaths = _state.UserPaths;
        string outsideDir = Path.Combine(userPaths.HomeDirectory, "outside");
        _state.Files.CreateDirectory(outsideDir);
        string canaryFile = Path.Combine(outsideDir, "canary.txt");
        _state.Files.WriteAllText(canaryFile, "protected");

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = invalidPreset,
                Yes = true
            },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.True(_state.Files.DirectoryExists(outsideDir));
        Assert.True(_state.Files.FileExists(canaryFile));
        Assert.Equal("protected", _state.Files.ReadAllText(canaryFile));
    }

    #endregion

    #region squad status --global Instances Listing Tests

    /// <summary>
    ///     Verifies that <c>squad status --global</c> displays deployed instances under a new <c>Instances</c>
    ///     heading in the format <c>&lt;target&gt;/&lt;preset&gt;  &lt;version&gt;</c>.
    /// </summary>
    [Fact]
    public void StatusGlobalListsDeployedInstancesUnderInstancesHeading()
    {
        ISquadUserPaths userPaths = _state.UserPaths;

        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));
        SeedInstance(userPaths, SquadTarget.Codex, "precise", "1.2.4",
            (".codex/agents/architect.toml", "name = \"architect\""));

        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(_state);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Global = true
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Instances:", execution.Output, StringComparison.Ordinal);
        Assert.Contains("claude/fast", execution.Output, StringComparison.Ordinal);
        Assert.Contains("1.2.3", execution.Output, StringComparison.Ordinal);
        Assert.Contains("codex/precise", execution.Output, StringComparison.Ordinal);
        Assert.Contains("1.2.4", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that <c>squad status --global</c> displays <c>none</c> under the Instances heading when
    ///     a global deployment exists but no harness instances have been deployed.
    /// </summary>
    [Fact]
    public void StatusGlobalShowsNoneUnderInstancesHeadingWhenNoInstancesExist()
    {
        ISquadUserPaths userPaths = _state.UserPaths;
        SeedGlobalDeployment(userPaths);

        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(_state);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Global = true
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Instances:", execution.Output, StringComparison.Ordinal);

        int instancesIndex = execution.Output.IndexOf("Instances:", StringComparison.Ordinal);
        string outputAfterInstances = execution.Output[instancesIndex..];
        Assert.Contains("none", outputAfterInstances, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies that <c>squad status --global</c> exits 1 with a no-deployment message when neither
    ///     a global deployment receipt nor any harness instances exist.
    /// </summary>
    [Fact]
    public void StatusGlobalWithNeitherDeploymentNorInstancesKeepsNoDeploymentExitOne()
    {
        ISquadUserPaths userPaths = _state.UserPaths;

        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(_state);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Global = true
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("No Kyber-Squad deployment found", execution.Output, StringComparison.Ordinal);
    }

    #endregion

    #region squad update --global Instance Refresh Tests

    /// <summary>
    ///     Verifies that <c>squad update --global</c> invokes instance refresh across all deployed instances,
    ///     updating their files and state safely without affecting files in the main home (AC 7.3, AC 7.8).
    /// </summary>
    [Fact]
    public async Task UpdateGlobalRefreshesDeployedInstances()
    {
        ISquadUserPaths userPaths = _state.UserPaths;

        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.0",
            (".claude/agents/architect.md", "Old content"));

        SquadUpdateCommand command = UpdateCommand();

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Global = true,
                Yes = true
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);

        string home = userPaths.HomeDirectory;
        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", "claude", "fast");
        Assert.True(_state.Files.DirectoryExists(instanceRoot), "Instance directory must still exist after refresh.");
    }

    /// <summary>
    ///     Verifies that <c>squad update --global</c> runs instance refresh even when no global deployment
    ///     exists at the root home (AC 7.3).
    /// </summary>
    [Fact]
    public async Task UpdateGlobalRefreshesDeployedInstancesEvenWithoutGlobalDeployment()
    {
        ISquadUserPaths userPaths = _state.UserPaths;

        // Seed instance state only — no global receipt in user-home
        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));

        SquadUpdateCommand command = UpdateCommand();

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Global = true,
                Yes = true
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
    }

    /// <summary>
    ///     Verifies that any failure during instance refresh causes <c>squad update --global</c> to exit with code 1.
    /// </summary>
    [Fact]
    public async Task UpdateGlobalExitsOneWhenInstanceRefreshFails()
    {
        ISquadUserPaths userPaths = _state.UserPaths;

        // Seed an instance with corrupted receipt JSON to force a refresh failure
        string home = userPaths.HomeDirectory;
        string instanceStateDir = Path.Combine(home, ".kyber-weave", "squad", "instance-state", "claude", "broken");
        _state.Files.CreateDirectory(instanceStateDir);
        _state.Files.WriteAllText(Path.Combine(instanceStateDir, "squad.receipt.json"), "{ invalid json }");

        SquadUpdateCommand command = UpdateCommand();

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new SquadUpdateSettings
            {
                Global = true,
                Yes = true
            },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
    }

    #endregion

    #region Helpers

    private SquadInstanceRemoveCommand CreateRemoveCommand(
        ISquadUserPaths userPaths,
        bool isInteractive,
        Func<string, bool>? readAnswer = null)
    {
        Assert.Same(_state.UserPaths, userPaths);
        return SquadTestCommands.InMemoryInstanceRemove(_state, Instances(), isInteractive, readAnswer);
    }

    private SquadInstanceService Instances() =>
        SquadTestCommands.InMemoryInstances(_state, ReleaseSource(), _renderer);

    private FakeSquadReleaseSource ReleaseSource() =>
        FakeSquadReleaseSource.FromDirectory(_source, _state.Files);

    private SquadUpdateCommand UpdateCommand() =>
        SquadTestCommands.InMemoryUpdate(_state, ReleaseSource(), _renderer, instances: Instances());

    private void SeedGlobalDeployment(
        ISquadUserPaths userPaths,
        string version = "1.2.3")
    {
        string home = userPaths.HomeDirectory;
        string globalStateDir = Path.Combine(home, ".kyber-weave", "squad", "global");
        _state.Files.CreateDirectory(globalStateDir);

        SquadReceipt receipt = new(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Global,
            ".",
            DateTimeOffset.UtcNow,
            [],
            []);

        SquadLock squadLock = new(
            SquadStateSchemas.LockSchemaV1,
            version,
            version,
            version,
            "full",
            ["codex"],
            [],
            "best-effort",
            "a".PadRight(64, '0'),
            "b".PadRight(64, '0'),
            new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));

        _state.Files.WriteAllText(
            Path.Combine(globalStateDir, "squad.receipt.json"),
            _state.Store.SerializeReceipt(receipt));
        _state.Files.WriteAllText(
            Path.Combine(globalStateDir, "squad.lock.yml"),
            _state.Store.SerializeLock(squadLock));
    }

    private void SeedInstance(
        ISquadUserPaths userPaths,
        SquadTarget target,
        string presetName,
        string version,
        params (string RelativePath, string Content)[] instanceFiles)
    {
        string targetToken = SquadTargetCatalog.GetToken(target);
        string home = userPaths.HomeDirectory;
        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", targetToken, presetName);
        string instanceStateDir =
            Path.Combine(home, ".kyber-weave", "squad", "instance-state", targetToken, presetName);

        _state.Files.CreateDirectory(instanceRoot);
        _state.Files.CreateDirectory(instanceStateDir);

        string presetsDir = Path.Combine(home, ".kyber-weave", "squad", "presets");
        _state.Files.CreateDirectory(presetsDir);
        string presetFilePath = Path.Combine(presetsDir, $"{presetName}.yml");
        if (!_state.Files.FileExists(presetFilePath))
        {
            string defaultPresetYaml = $"""
                                        schema: kyber-squad.model-preset/v1
                                        profiles:
                                          general:
                                            default: gpt-4o-mini
                                            {targetToken}: gpt-4o-mini
                                        """;
            _state.Files.WriteAllText(presetFilePath, defaultPresetYaml);
        }

        byte[] presetBytes = _state.Files.ReadAllBytes(presetFilePath);
        string presetDigest = Convert.ToHexStringLower(SHA256.HashData(presetBytes));

        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relPath, string content) in instanceFiles)
        {
            string fullPath = Path.Combine(instanceRoot, relPath);
            _state.Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            _state.Files.WriteAllBytes(fullPath, bytes);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ownedFiles.Add(new SquadOwnedFile(relPath, sha256, targetToken, false));
        }

        SquadReceipt receipt = new(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Instance,
            instanceRoot,
            DateTimeOffset.UtcNow,
            [],
            ownedFiles);

        SquadLock squadLock = new(
            SquadStateSchemas.LockSchemaV2,
            version,
            version,
            version,
            "full",
            [targetToken],
            [],
            "best-effort",
            "a".PadRight(64, '0'),
            "b".PadRight(64, '0'),
            new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')),
            new Dictionary<SquadTarget, SquadAppliedPreset>
            {
                [target] = new(presetName, presetDigest)
            });

        _state.Files.WriteAllText(
            Path.Combine(instanceStateDir, "squad.receipt.json"),
            _state.Store.SerializeReceipt(receipt));
        _state.Files.WriteAllText(
            Path.Combine(instanceStateDir, "squad.lock.yml"),
            _state.Store.SerializeLock(squadLock));
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private static async Task<CommandExecution> CaptureAsync(Func<Task<int>> execute)
    {
        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    #endregion
}

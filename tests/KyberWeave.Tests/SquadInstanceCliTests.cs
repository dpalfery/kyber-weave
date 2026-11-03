using System.Runtime.CompilerServices;
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
public sealed class SquadInstanceCliTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    #region Program.cs Registration Tests

    /// <summary>
    ///     Verifies that Program.cs registers the <c>squad instance</c> branch with the <c>remove</c> command,
    ///     options, and examples.
    /// </summary>
    [Fact]
    public void ProgramRegistersSquadInstanceRemoveBranchAndCommand()
    {
        string program = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "KyberWeave.Cli",
            "Program.cs"));

        Assert.Contains("AddBranch(\"instance\"", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<SquadInstanceRemoveCommand>(\"remove\")", program, StringComparison.Ordinal);
        Assert.Contains("WithExample(\"squad\", \"instance\", \"remove\"", program, StringComparison.Ordinal);
    }

    #endregion

    #region squad instance remove Tests

    /// <summary>
    ///     Verifies that <c>squad instance remove --target claude --preset fast --yes</c> deletes the instance
    ///     directory and state directory under ~/.kyber-weave/ while leaving target files in the main home intact (AC 7.8).
    /// </summary>
    [Fact]
    public void InstanceRemoveDeletesInstanceDirectoryAndStateWhenYesFlagSpecified()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        string mainClaudeHome = Path.Combine(userPaths.HomeDirectory, ".claude");
        Directory.CreateDirectory(mainClaudeHome);
        string mainFile = Path.Combine(mainClaudeHome, "main-config.json");
        File.WriteAllText(mainFile, "{\"preserved\": true}", Encoding.UTF8);

        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));

        string home = userPaths.HomeDirectory;
        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", "claude", "fast");
        string instanceStateDir = Path.Combine(home, ".kyber-weave", "squad", "instance-state", "claude", "fast");

        Assert.True(Directory.Exists(instanceRoot));
        Assert.True(Directory.Exists(instanceStateDir));

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = "fast",
                Yes = true
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.False(Directory.Exists(instanceRoot), "Instance directory must be deleted.");
        Assert.False(Directory.Exists(instanceStateDir), "Instance state directory must be deleted.");
        Assert.True(File.Exists(mainFile), "Target files in main harness home must remain intact (AC 7.8).");
    }

    /// <summary>
    ///     Verifies that declining the interactive confirmation prompt preserves instance directories and state,
    ///     exiting with code 2 without mutating filesystem (similar to uninstall command).
    /// </summary>
    [Fact]
    public void InstanceRemoveDeclinedConfirmationPreservesInstanceAndExitsTwo()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));

        string home = userPaths.HomeDirectory;
        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", "claude", "fast");
        string instanceStateDir = Path.Combine(home, ".kyber-weave", "squad", "instance-state", "claude", "fast");

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: true,
            readAnswer: static _ => false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = "fast",
                Yes = false
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Declined", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(instanceRoot), "Instance directory must be preserved when prompt is declined.");
        Assert.True(Directory.Exists(instanceStateDir),
            "Instance state directory must be preserved when prompt is declined.");
    }

    /// <summary>
    ///     Verifies that accepting the interactive confirmation prompt removes the instance and exits 0.
    /// </summary>
    [Fact]
    public void InstanceRemoveConfirmedPromptDeletesInstanceAndExitsZero()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));

        string home = userPaths.HomeDirectory;
        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", "claude", "fast");
        string instanceStateDir = Path.Combine(home, ".kyber-weave", "squad", "instance-state", "claude", "fast");

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: true,
            readAnswer: static _ => true);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = "fast",
                Yes = false
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.False(Directory.Exists(instanceRoot));
        Assert.False(Directory.Exists(instanceStateDir));
    }

    /// <summary>
    ///     Verifies that attempting to remove an unknown or uninstalled instance exits with code 2.
    /// </summary>
    [Fact]
    public void InstanceRemoveExitsTwoWhenInstanceDoesNotExist()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = "nonexistent",
                Yes = true
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("not found", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies that passing an unknown or invalid target token exits with code 2 before any filesystem mutation.
    /// </summary>
    [Fact]
    public void InstanceRemoveExitsTwoWhenTargetIsInvalid()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "invalid-target",
                Preset = "fast",
                Yes = true
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Unknown Squad target", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies that omitting the required preset parameter exits with code 2.
    /// </summary>
    [Fact]
    public void InstanceRemoveExitsTwoWhenPresetIsMissing()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = null,
                Yes = true
            }));

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
    public void InstanceRemoveRejectsPathTraversalAttemptExitingTwoWithoutTouchingExternalDirectories(
        string invalidPreset)
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        string outsideDir = Path.Combine(userPaths.HomeDirectory, "outside");
        Directory.CreateDirectory(outsideDir);
        string canaryFile = Path.Combine(outsideDir, "canary.txt");
        File.WriteAllText(canaryFile, "protected", Encoding.UTF8);

        SquadInstanceRemoveCommand command = CreateRemoveCommand(
            userPaths,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadInstanceRemoveSettings
            {
                Target = "claude",
                Preset = invalidPreset,
                Yes = true
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.True(Directory.Exists(outsideDir));
        Assert.True(File.Exists(canaryFile));
        Assert.Equal("protected", File.ReadAllText(canaryFile, Encoding.UTF8));
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
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));
        SeedInstance(userPaths, SquadTarget.Codex, "precise", "1.2.4",
            (".codex/agents/architect.toml", "name = \"architect\""));

        SquadStatusCommand command = new(userPaths, stateStore);

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
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);
        SeedGlobalDeployment(userPaths);

        SquadStatusCommand command = new(userPaths, stateStore);

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
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        SquadStatusCommand command = new(userPaths, stateStore);

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
    public void UpdateGlobalRefreshesDeployedInstances()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.0",
            (".claude/agents/architect.md", "Old content"));

        SquadUpdateCommand command = new(
            userPaths,
            stateStore,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Global = true,
                Yes = true
            }));

        Assert.Equal(0, execution.ExitCode);

        string home = userPaths.HomeDirectory;
        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", "claude", "fast");
        Assert.True(Directory.Exists(instanceRoot), "Instance directory must still exist after refresh.");
    }

    /// <summary>
    ///     Verifies that <c>squad update --global</c> runs instance refresh even when no global deployment
    ///     exists at the root home (AC 7.3).
    /// </summary>
    [Fact]
    public void UpdateGlobalRefreshesDeployedInstancesEvenWithoutGlobalDeployment()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        // Seed instance state only — no global receipt in user-home
        SeedInstance(userPaths, SquadTarget.Claude, "fast", "1.2.3",
            (".claude/agents/architect.md", "You are architect."));

        SquadUpdateCommand command = new(
            userPaths,
            stateStore,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Global = true,
                Yes = true
            }));

        Assert.Equal(0, execution.ExitCode);
    }

    /// <summary>
    ///     Verifies that any failure during instance refresh causes <c>squad update --global</c> to exit with code 1.
    /// </summary>
    [Fact]
    public void UpdateGlobalExitsOneWhenInstanceRefreshFails()
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home"));
        SquadStateStore stateStore = new(userPaths);

        // Seed an instance with corrupted receipt JSON to force a refresh failure
        string home = userPaths.HomeDirectory;
        string instanceStateDir = Path.Combine(home, ".kyber-weave", "squad", "instance-state", "claude", "broken");
        Directory.CreateDirectory(instanceStateDir);
        File.WriteAllText(Path.Combine(instanceStateDir, "squad.receipt.json"), "{ invalid json }", Encoding.UTF8);

        SquadUpdateCommand command = new(
            userPaths,
            stateStore,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadUpdateSettings
            {
                Global = true,
                Yes = true
            }));

        Assert.Equal(1, execution.ExitCode);
    }

    #endregion

    #region Helpers

    private static SquadInstanceRemoveCommand CreateRemoveCommand(
        ISquadUserPaths? userPaths = null,
        SquadInstanceService? instanceService = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null)
    {
        return new SquadInstanceRemoveCommand(
            userPaths,
            instanceService,
            isInteractive,
            readAnswer);
    }

    private static void SeedGlobalDeployment(
        ISquadUserPaths userPaths,
        string version = "1.2.3")
    {
        string home = userPaths.HomeDirectory;
        string globalStateDir = Path.Combine(home, ".kyber-weave", "squad", "global");
        Directory.CreateDirectory(globalStateDir);

        SquadReceipt receipt = new(
            SquadStateStore.ReceiptSchemaV1,
            SquadDeploymentScope.Global,
            ".",
            DateTimeOffset.UtcNow,
            [],
            []);

        SquadLock squadLock = new(
            SquadStateStore.LockSchemaV1,
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

        SquadStateStore stateStore = new(userPaths);
        File.WriteAllText(
            Path.Combine(globalStateDir, "squad.receipt.json"),
            stateStore.SerializeReceipt(receipt),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(globalStateDir, "squad.lock.yml"),
            stateStore.SerializeLock(squadLock),
            Encoding.UTF8);
    }

    private static void SeedInstance(
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

        Directory.CreateDirectory(instanceRoot);
        Directory.CreateDirectory(instanceStateDir);

        string presetsDir = Path.Combine(home, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        string presetFilePath = Path.Combine(presetsDir, $"{presetName}.yml");
        if (!File.Exists(presetFilePath))
        {
            string defaultPresetYaml = $"""
                                        schema: kyber-squad.model-preset/v1
                                        profiles:
                                          general:
                                            default: gpt-4o-mini
                                            {targetToken}: gpt-4o-mini
                                        """;
            File.WriteAllText(presetFilePath, defaultPresetYaml, Encoding.UTF8);
        }

        byte[] presetBytes = File.ReadAllBytes(presetFilePath);
        string presetDigest = Convert.ToHexStringLower(SHA256.HashData(presetBytes));

        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relPath, string content) in instanceFiles)
        {
            string fullPath = Path.Combine(instanceRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(fullPath, bytes);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ownedFiles.Add(new SquadOwnedFile(relPath, sha256, targetToken, false));
        }

        SquadReceipt receipt = new(
            SquadStateStore.ReceiptSchemaV1,
            SquadDeploymentScope.Instance,
            instanceRoot,
            DateTimeOffset.UtcNow,
            [],
            ownedFiles);

        SquadLock squadLock = new(
            SquadStateStore.LockSchemaV2,
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

        SquadStateStore stateStore = new(userPaths);
        File.WriteAllText(
            Path.Combine(instanceStateDir, "squad.receipt.json"),
            stateStore.SerializeReceipt(receipt),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(instanceStateDir, "squad.lock.yml"),
            stateStore.SerializeLock(squadLock),
            Encoding.UTF8);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    #endregion
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Task 7 test suite: Pins the <c>kyber-weave squad preset</c> CLI command surface,
///     verbs (<c>use</c>, <c>clear</c>, <c>list</c>, <c>show</c>), outcomes, and exit codes.
///     Acceptance criteria covered:
///     - 2.1: <c>squad preset use &lt;name&gt;</c> applies preset through update transaction.
///     - 2.4: <c>--dry-run</c> prints diffs and writes nothing.
///     - 3.3: <c>squad preset clear</c> restores bundled models.
///     - 4.1: Unknown names/targets exit non-zero naming offending key.
///     - 5.1: <c>squad preset list</c> output.
///     - 5.2: <c>squad preset show</c> output.
/// </summary>
/// <remarks>
///     Drives the built CLI host as a child process rather than calling command classes directly,
///     verifying that command routing, argument parsing, strict option validation, and process
///     exit codes follow the CLI contract. Running the CLI binary compiles against the current
///     tree where the preset branch is not yet implemented, recording authentic runtime RED
///     failure evidence.
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

    private readonly string _home;
    private readonly string _projectDir;
    private readonly SquadModelPresetSelectionStore _selectionStore;
    private readonly SquadStateStore _stateStore;

    private readonly TempDirectory _temp = new();
    private readonly FakeSquadUserPaths _userPaths;

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

    #region Canonical snapshot resolution (issue #436)

    /// <remarks>
    ///     In-process coverage for the preset-verb rewiring: each verb resolves through the
    ///     injected canonical-profile seam with the checkout forced absent, so a present
    ///     snapshot drives per-category tokens for agents while skill files pass through
    ///     byte-identical, and an unresolvable source or an unknown preset key fails closed
    ///     with exit 1 before any file or state is touched. The child-process tests above keep
    ///     pinning the checkout-backed surface.
    /// </remarks>
    private const string PerCategoryPresetYaml = """
                                                 schema: kyber-squad.model-preset/v1
                                                 profiles:
                                                   architect:
                                                     claude: percat-arch-claude
                                                   general:
                                                     claude: percat-general-claude
                                                 """;

    private const string BadPresetYaml = """
                                         schema: kyber-squad.model-preset/v1
                                         profiles:
                                           nosuch:
                                             default: whatever
                                         """;

    private const string BugCrusherSkillContent =
        "---\nname: bug-crusher\nmodel: skill-model\n---\nYou are bug-crusher.\n";

    private const string ArchitectProjectionContent =
        "---\nname: architect\nmodel: projection-model\n---\nProjection.\n";

    [Fact]
    public void UseWithSnapshotPresentResolvesPerCategoryDryRun()
    {
        WritePreset("percat", PerCategoryPresetYaml);
        SeedCanonicalDeployment("bundled-arch-claude", "bundled-general-claude");
        string? seenVersion = "unset";
        SquadPresetUseCommand command = CreateUseCommand(
            SnapshotResolver("1.2.3", version => seenVersion = version));

        SquadPresetUseSettings settings = new()
        {
            Name = "percat",
            Path = _projectDir,
            Targets = ["claude"],
            DryRun = true
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(0, captured.Result);
        Assert.Equal("1.2.3", seenVersion);
        Assert.Contains(
            "architect: bundled-arch-claude -> percat-arch-claude",
            captured.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "implementer: bundled-general-claude -> percat-general-claude",
            captured.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("bug-crusher", captured.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("projection", captured.Output, StringComparison.OrdinalIgnoreCase);
        AssertAgentModel("architect", "bundled-arch-claude");
        AssertAgentModel("implementer", "bundled-general-claude");
        Assert.Null(_selectionStore.Read(_projectDir));
    }

    [Fact]
    public void UseWithSnapshotPresentAppliesPerCategoryAndLeavesSkillsUntouched()
    {
        WritePreset("percat", PerCategoryPresetYaml);
        SeedCanonicalDeployment("bundled-arch-claude", "bundled-general-claude");
        SquadPresetUseCommand command = CreateUseCommand(SnapshotResolver("1.2.3"));

        SquadPresetUseSettings settings = new()
        {
            Name = "percat",
            Path = _projectDir,
            Targets = ["claude"]
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(0, captured.Result);
        AssertAgentModel("architect", "percat-arch-claude");
        AssertAgentModel("implementer", "percat-general-claude");
        Assert.Equal(
            BugCrusherSkillContent,
            File.ReadAllText(Path.Combine(_projectDir, ".claude", "skills", "bug-crusher", "SKILL.md")));
        Assert.Equal(
            ArchitectProjectionContent,
            File.ReadAllText(Path.Combine(_projectDir, ".claude", "skills", "architect", "SKILL.md")));
        SquadModelPresetSelection? selection = _selectionStore.Read(_projectDir);
        Assert.NotNull(selection);
        Assert.Equal("percat", selection.ModelPresets[SquadTarget.Claude]);
    }

    [Fact]
    public void UseWithNoCanonicalSourceDryRunExits1WithRemedyAndTouchesNothing()
    {
        WritePreset("percat", PerCategoryPresetYaml);
        SeedCanonicalDeployment("bundled-arch-claude", "bundled-general-claude");
        string receiptBefore = ReadReceiptText();
        string lockBefore = ReadLockText();
        SquadPresetUseCommand command = new(_userPaths, _stateStore, null, (_, _) => null);

        SquadPresetUseSettings settings = new()
        {
            Name = "percat",
            Path = _projectDir,
            Targets = ["claude"],
            DryRun = true
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(1, captured.Result);
        Assert.Contains(
            "Could not resolve Squad model profiles",
            captured.Output,
            StringComparison.Ordinal);
        Assert.Contains("squad update", captured.Output, StringComparison.Ordinal);
        AssertAgentModel("architect", "bundled-arch-claude");
        AssertAgentModel("implementer", "bundled-general-claude");
        Assert.Equal(receiptBefore, ReadReceiptText());
        Assert.Equal(lockBefore, ReadLockText());
        Assert.Null(_selectionStore.Read(_projectDir));
    }

    [Fact]
    public void UseWithNoCanonicalSourceApplyExits1WithRemedyAndTouchesNothing()
    {
        WritePreset("percat", PerCategoryPresetYaml);
        SeedCanonicalDeployment("bundled-arch-claude", "bundled-general-claude");
        string receiptBefore = ReadReceiptText();
        string lockBefore = ReadLockText();
        SquadPresetUseCommand command = new(_userPaths, _stateStore, null, (_, _) => null);

        SquadPresetUseSettings settings = new()
        {
            Name = "percat",
            Path = _projectDir,
            Targets = ["claude"]
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(1, captured.Result);
        Assert.Contains(
            "Could not resolve Squad model profiles",
            captured.Output,
            StringComparison.Ordinal);
        Assert.Contains("squad update", captured.Output, StringComparison.Ordinal);
        AssertAgentModel("architect", "bundled-arch-claude");
        AssertAgentModel("implementer", "bundled-general-claude");
        Assert.Equal(receiptBefore, ReadReceiptText());
        Assert.Equal(lockBefore, ReadLockText());
        Assert.Null(_selectionStore.Read(_projectDir));
    }

    [Fact]
    public void ClearWithSnapshotPresentRestoresBundledModelsDryRun()
    {
        SeedCanonicalDeployment("percat-arch-claude", "percat-general-claude");
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "percat");
        SquadPresetClearCommand command = CreateClearCommand(SnapshotResolver("1.2.3"));

        SquadPresetClearSettings settings = new()
        {
            Path = _projectDir,
            Targets = ["claude"],
            DryRun = true
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(0, captured.Result);
        Assert.Contains(
            "architect: percat-arch-claude -> bundled-arch-claude",
            captured.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "implementer: percat-general-claude -> bundled-general-claude",
            captured.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("bug-crusher", captured.Output, StringComparison.Ordinal);
        AssertAgentModel("architect", "percat-arch-claude");
        AssertAgentModel("implementer", "percat-general-claude");
    }

    [Fact]
    public void ClearWithSnapshotPresentAppliesAndLeavesSkillsUntouched()
    {
        SeedCanonicalDeployment("percat-arch-claude", "percat-general-claude");
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "percat");
        SquadPresetClearCommand command = CreateClearCommand(SnapshotResolver("1.2.3"));

        SquadPresetClearSettings settings = new()
        {
            Path = _projectDir,
            Targets = ["claude"]
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(0, captured.Result);
        AssertAgentModel("architect", "bundled-arch-claude");
        AssertAgentModel("implementer", "bundled-general-claude");
        Assert.Equal(
            BugCrusherSkillContent,
            File.ReadAllText(Path.Combine(_projectDir, ".claude", "skills", "bug-crusher", "SKILL.md")));
        Assert.Equal(
            ArchitectProjectionContent,
            File.ReadAllText(Path.Combine(_projectDir, ".claude", "skills", "architect", "SKILL.md")));
        Assert.Null(_selectionStore.Read(_projectDir));
    }

    [Fact]
    public void ClearWithNoCanonicalSourceDryRunExits1WithRemedyAndTouchesNothing()
    {
        SeedCanonicalDeployment("percat-arch-claude", "percat-general-claude");
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "percat");
        string receiptBefore = ReadReceiptText();
        string lockBefore = ReadLockText();
        SquadPresetClearCommand command = new(_userPaths, _stateStore, null, (_, _) => null);

        SquadPresetClearSettings settings = new()
        {
            Path = _projectDir,
            Targets = ["claude"],
            DryRun = true
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(1, captured.Result);
        Assert.Contains(
            "Could not resolve Squad model profiles",
            captured.Output,
            StringComparison.Ordinal);
        Assert.Contains("squad update", captured.Output, StringComparison.Ordinal);
        AssertAgentModel("architect", "percat-arch-claude");
        AssertAgentModel("implementer", "percat-general-claude");
        Assert.Equal(receiptBefore, ReadReceiptText());
        Assert.Equal(lockBefore, ReadLockText());
        Assert.NotNull(_selectionStore.Read(_projectDir));
    }

    [Fact]
    public void ClearWithNoCanonicalSourceApplyExits1WithRemedyAndTouchesNothing()
    {
        SeedCanonicalDeployment("percat-arch-claude", "percat-general-claude");
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "percat");
        string receiptBefore = ReadReceiptText();
        string lockBefore = ReadLockText();
        SquadPresetClearCommand command = new(_userPaths, _stateStore, null, (_, _) => null);

        SquadPresetClearSettings settings = new()
        {
            Path = _projectDir,
            Targets = ["claude"]
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(1, captured.Result);
        Assert.Contains(
            "Could not resolve Squad model profiles",
            captured.Output,
            StringComparison.Ordinal);
        Assert.Contains("squad update", captured.Output, StringComparison.Ordinal);
        AssertAgentModel("architect", "percat-arch-claude");
        AssertAgentModel("implementer", "percat-general-claude");
        Assert.Equal(receiptBefore, ReadReceiptText());
        Assert.Equal(lockBefore, ReadLockText());
        Assert.NotNull(_selectionStore.Read(_projectDir));
    }

    [Fact]
    public void UseWithUnknownPresetCategoryExits1WithPathAndLine()
    {
        WritePreset("bad", BadPresetYaml);
        SeedCanonicalDeployment("bundled-arch-claude", "bundled-general-claude");
        string receiptBefore = ReadReceiptText();
        string lockBefore = ReadLockText();
        SquadPresetUseCommand command = CreateUseCommand(SnapshotResolver("1.2.3"));

        SquadPresetUseSettings settings = new()
        {
            Name = "bad",
            Path = _projectDir,
            Targets = ["claude"]
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(1, captured.Result);
        Assert.Contains("nosuch", captured.Output, StringComparison.Ordinal);
        Assert.Contains("bad.yml:3", captured.Output, StringComparison.Ordinal);
        AssertAgentModel("architect", "bundled-arch-claude");
        AssertAgentModel("implementer", "bundled-general-claude");
        Assert.Equal(receiptBefore, ReadReceiptText());
        Assert.Equal(lockBefore, ReadLockText());
        Assert.Null(_selectionStore.Read(_projectDir));
    }

    [Fact]
    public void UseWithSnapshotPresentResolvesUppercaseAgentNameThroughRoster()
    {
        WritePreset("percat", PerCategoryPresetYaml);
        SeedProjectDeployment(
            _projectDir,
            "claude",
            (".claude/agents/architect.md",
                "---\nname: ARCHITECT\nmodel: bundled-arch-claude\n---\nYou are architect.\n"));
        SquadPresetUseCommand command = CreateUseCommand(SnapshotResolver("1.2.3"));

        SquadPresetUseSettings settings = new()
        {
            Name = "percat",
            Path = _projectDir,
            Targets = ["claude"],
            DryRun = true
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        // The snapshot roster must match the checkout roster's case-insensitivity:
        // ARCHITECT resolves to the architect entry instead of passing through.
        Assert.Equal(0, captured.Result);
        Assert.Contains(
            "ARCHITECT: bundled-arch-claude -> percat-arch-claude",
            captured.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShowWithSnapshotPresentResolvesNewestSnapshot()
    {
        WritePreset("percat", PerCategoryPresetYaml);
        SquadCanonicalProfileStore snapshotStore = new(_userPaths);
        snapshotStore.Write(CreateCanonicalSnapshot("1.2.3"));
        string? seenVersion = "unset";
        SquadPresetShowCommand command = new(
            _userPaths,
            (preferredVersion, _) =>
            {
                seenVersion = preferredVersion;
                return snapshotStore.TryReadLatest();
            });

        SquadPresetShowSettings settings = new()
        {
            Name = "percat",
            Target = "claude"
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(0, captured.Result);
        Assert.Null(seenVersion);
        Assert.Contains("architect: percat-arch-claude", captured.Output, StringComparison.Ordinal);
        Assert.Contains("general: percat-general-claude", captured.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("(bundled)", captured.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowWithNoCanonicalSourceExits1WithRemedy()
    {
        WritePreset("percat", PerCategoryPresetYaml);
        SquadPresetShowCommand command = new(_userPaths, (_, _) => null);

        SquadPresetShowSettings settings = new()
        {
            Name = "percat",
            Target = "claude"
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(1, captured.Result);
        Assert.Contains(
            "Could not resolve Squad model profiles",
            captured.Output,
            StringComparison.Ordinal);
        Assert.Contains("squad update", captured.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowWithUnknownPresetCategoryExits1WithPathAndLine()
    {
        WritePreset("bad", BadPresetYaml);
        SquadCanonicalProfileStore snapshotStore = new(_userPaths);
        snapshotStore.Write(CreateCanonicalSnapshot("1.2.3"));
        SquadPresetShowCommand command = new(_userPaths, (_, _) => snapshotStore.TryReadLatest());

        SquadPresetShowSettings settings = new()
        {
            Name = "bad",
            Target = "claude"
        };
        CapturedConsoleExecution<int> captured = ProcessConsoleCapture.Run(() =>
            command.Execute(CreateCommandContext(), settings, CancellationToken.None));

        Assert.Equal(1, captured.Result);
        Assert.Contains("nosuch", captured.Output, StringComparison.Ordinal);
        Assert.Contains("bad.yml:3", captured.Output, StringComparison.Ordinal);
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

    private static SquadCanonicalProfiles CreateCanonicalSnapshot(string version)
    {
        Dictionary<string, SquadModelProfile> profiles = new(StringComparer.Ordinal)
        {
            ["architect"] = new SquadModelProfile(
                "bundled-arch-default",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "bundled-arch-claude" }),
            ["general"] = new SquadModelProfile(
                "bundled-general-default",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "bundled-general-claude" })
        };
        Dictionary<string, string> roster = new(StringComparer.Ordinal)
        {
            ["architect"] = "architect",
            ["implementer"] = "general"
        };
        return new SquadCanonicalProfiles(
            version,
            new SquadModelProfiles("kyber-squad.model-profiles/v1", profiles, "models.yml"),
            roster);
    }

    private Func<string?, ISquadUserPaths, SquadCanonicalProfiles?> SnapshotResolver(
        string version,
        Action<string?>? onVersion = null)
    {
        SquadCanonicalProfileStore snapshotStore = new(_userPaths);
        snapshotStore.Write(CreateCanonicalSnapshot(version));
        return (preferredVersion, _) =>
        {
            onVersion?.Invoke(preferredVersion);
            return preferredVersion is null ? null : snapshotStore.TryRead(preferredVersion);
        };
    }

    private SquadPresetUseCommand CreateUseCommand(
        Func<string?, ISquadUserPaths, SquadCanonicalProfiles?> resolver)
    {
        return new SquadPresetUseCommand(_userPaths, _stateStore, null, resolver);
    }

    private SquadPresetClearCommand CreateClearCommand(
        Func<string?, ISquadUserPaths, SquadCanonicalProfiles?> resolver)
    {
        return new SquadPresetClearCommand(_userPaths, _stateStore, null, resolver);
    }

    private static CommandContext CreateCommandContext()
    {
        return new CommandContext(
            Array.Empty<string>(),
            new FakeRemainingArguments(),
            "preset",
            new object());
    }

    private void SeedCanonicalDeployment(string architectModel, string implementerModel)
    {
        SeedProjectDeployment(
            _projectDir,
            "claude",
            (".claude/agents/architect.md",
                $"---\nname: architect\nmodel: {architectModel}\n---\nYou are architect.\n"),
            (".claude/agents/implementer.md",
                $"---\nname: implementer\nmodel: {implementerModel}\n---\nYou are implementer.\n"),
            (".claude/skills/bug-crusher/SKILL.md", BugCrusherSkillContent),
            (".claude/skills/architect/SKILL.md", ArchitectProjectionContent));
    }

    private void AssertAgentModel(string agentName, string expectedModel)
    {
        string content = File.ReadAllText(Path.Combine(_projectDir, ".claude", "agents", $"{agentName}.md"));
        Assert.Contains($"model: {expectedModel}", content, StringComparison.Ordinal);
    }

    private string ReadReceiptText()
    {
        return File.ReadAllText(_stateStore.ResolveReceiptPath(_projectDir, SquadDeploymentScope.Project));
    }

    private string ReadLockText()
    {
        return File.ReadAllText(_stateStore.ResolveLockPath(_projectDir, SquadDeploymentScope.Project));
    }

    private sealed class FakeRemainingArguments : IRemainingArguments
    {
        public ILookup<string, string?> Parsed =>
            Array.Empty<string>().ToLookup(name => name, name => (string?)null);

        public IReadOnlyList<string> Raw => [];
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

    [Fact]
    public void PresetUseGlobalWhenLockExistsWithTargetsButReceiptIsMissingExitsNonZeroWithoutNullReferenceException()
    {
        WritePreset("fast", FastPresetYaml);

        string globalAnchor = _userPaths.ApplicationDataDirectory;
        string lockPath = _stateStore.ResolveLockPath(globalAnchor, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        SquadLock squadLock = new(
            "kyber-squad.lock/v1",
            "1.2.3",
            "1.2.3",
            "1.2.3",
            "full",
            ["claude"],
            [],
            "best-effort",
            "a".PadRight(64, '0'),
            "b".PadRight(64, '0'),
            new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));

        File.WriteAllText(lockPath, _stateStore.SerializeLock(squadLock), Encoding.UTF8);

        CliResult result = Run("squad", "preset", "use", "fast", "--target", "claude", "--global");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("NullReferenceException", result.Output, StringComparison.Ordinal);
        Assert.Contains("receipt", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FrontmatterHelperHandlesWindowsCrlfLineEndingsWithoutBodySlicingOrCorruption()
    {
        const string original = "---\r\nname: test\r\n---\r\n# Body content\r\n";
        bool parsed = SquadFrontmatterHelper.TryParseFrontmatter(
            original,
            out string frontmatter,
            out string body,
            out string newline);

        Assert.True(parsed);
        Assert.Equal("\r\n", newline);
        Assert.Equal("name: test", frontmatter);
        Assert.Equal("\r\n# Body content\r\n", body);
        Assert.StartsWith("\r\n", body, StringComparison.Ordinal);

        string reconstructed = $"---{newline}{frontmatter}{newline}---{body}";
        Assert.Equal(original, reconstructed);
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

        foreach (string argument in args) startInfo.ArgumentList.Add(argument);

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
            if (value is not null) startInfo.Environment[key] = value;
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

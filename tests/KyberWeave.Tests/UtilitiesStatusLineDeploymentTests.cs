using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Utilities.StatusLine;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract suite for the Kyber Utilities status-line deployment core (development mode:
/// test-first, docs/archive/plans/2026-09-28-kyber-utilities-status-line-slice.md, Test contract row T2).
/// Pins per-harness root resolution with environment overrides, the Kyber-owned staging segment and
/// the auto-load directories it must stay out of, that a plan targets only pinned staging paths and
/// never a settings path, that an unmanaged file at a target path is refused rather than
/// overwritten, that a redeploy refuses a Kyber-owned file the user edited rather than replacing
/// it, that the receipt records every owned file with its SHA-256, that remove deletes only
/// receipt-owned unmodified files, that no harness settings file is ever created or modified, and
/// that the deployment target set is exactly the three harnesses D9 keeps — Claude Code, <c>agy</c>,
/// and Pi — with no OpenCode target anywhere on the surface.
/// </summary>
/// <remarks>
/// Authored before <see cref="StatusLineTargetRoots"/>, <see cref="StatusLineDeploymentPlan"/>, and
/// <see cref="StatusLineReceipt"/> have an implementation, against a compile scaffold that stands in
/// for the types under <c>src/KyberWeave.Core/Utilities/StatusLine/</c> until task T3 replaces it.
///
/// <para>
/// Every expectation is a literal this suite owns — the harness roots, the staging segment
/// <c>kyber/statusline</c>, the three confirmed settings files, the one auto-load directory, and
/// the three-member target set — rather than a value read back from
/// <see cref="StatusLineTargetRoots"/>. A test that asked the type under test where it puts things
/// and then asserted that it put them there would pass against any implementation, including one
/// that plans nothing. Arranging and asserting against literal paths also keeps every contract row
/// failing on its own assertion: a scaffold that plans nothing fails the byte, digest, and delete
/// assertions directly instead of stopping at an empty-collection preflight that says nothing
/// about the contract.
/// </para>
///
/// <para>
/// Every test runs against an injected temp home and an injected environment lookup, so no test
/// reads or mutates the process environment and none of them can touch the operator's real harness
/// configuration. Filesystem writes stay inside the <see cref="TempDirectory"/> the test owns.
/// </para>
/// </remarks>
public sealed class UtilitiesStatusLineDeploymentTests : IDisposable
{
    private const string ClaudeConfigDirectoryVariable = "CLAUDE_CONFIG_DIR";
    private const string PiCodingAgentDirectoryVariable = "PI_CODING_AGENT_DIR";
    private const string AgyConfigDirectoryVariable = "AGY_CONFIG_DIR";

    /// <summary>
    /// The Kyber-owned staging segment beneath every harness root. A literal, so the suite pins where
    /// a deployment lands instead of restating whatever the type under test happens to return.
    /// </summary>
    private const string StagingSegment = "kyber/statusline";

    private const string PrimaryArtifactRelativePath = "statusline/statusline.sh";
    private const string SecondaryArtifactRelativePath = "statusline/manifest.json";
    private const string PrimaryArtifactContent = "#!/bin/sh\necho kyber\n";
    private const string SecondaryArtifactContent = "{\"harness\":\"synthetic\"}\n";
    private const string UnmanagedFileContent = "hand-written by the user\n";

    /// <summary>
    /// The bytes an operator leaves behind when they edit a file Kyber deployed. Shaped like the
    /// primary artifact but distinct from it, so a redeploy that replaces the file is observable as
    /// a byte difference rather than as a coincidence between two equal payloads.
    /// </summary>
    private const string LocallyEditedFileContent = "#!/bin/sh\necho edited by the operator\n";

    /// <summary>
    /// The harness settings files this slice must never create, open for write, or modify, relative
    /// to the injected home. Literals rather than values read back from
    /// <see cref="StatusLineTargetRoots.ResolveSettingsPaths"/>, so the guarantee is asserted against
    /// the operator's real settings locations and not against whatever the type under test names.
    /// </summary>
    private static readonly string[] ConfirmedSettingsRelativePaths =
    [
        ".claude/settings.json",
        ".gemini/antigravity-cli/settings.json",
        ".pi/agent/settings.json"
    ];

    /// <summary>
    /// The exact members <see cref="StatusLineTarget"/> is allowed to declare, in declaration order.
    /// Literals, for the same reason as the paths above: a target set compared against
    /// <see cref="Enum.GetNames{T}"/> says nothing if the expectation is read back from the type
    /// under test, and OpenCode is the member this slice dropped (D9).
    /// </summary>
    private static readonly string[] PinnedTargetNames = ["Claude", "Agy", "Pi"];

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    public static TheoryData<StatusLineTarget> AllTargets => new()
    {
        StatusLineTarget.Claude,
        StatusLineTarget.Agy,
        StatusLineTarget.Pi
    };

    /// <summary>
    /// Each harness's documented root override. The Antigravity CLI publishes none of its own, so
    /// <c>AGY_CONFIG_DIR</c> is the name this slice chose and is pinned here exactly like the other
    /// two.
    /// </summary>
    public static TheoryData<StatusLineTarget, string> DocumentedOverrideVariables => new()
    {
        { StatusLineTarget.Claude, ClaudeConfigDirectoryVariable },
        { StatusLineTarget.Agy, AgyConfigDirectoryVariable },
        { StatusLineTarget.Pi, PiCodingAgentDirectoryVariable }
    };

    /// <summary>
    /// A plausible-looking variable per harness that nothing consults, so an override lookup that
    /// read the wrong name would be caught rather than silently ignored.
    /// </summary>
    public static TheoryData<StatusLineTarget, string> UnrelatedEnvironmentVariables => new()
    {
        { StatusLineTarget.Claude, "CLAUDE_CONFIG_HOME" },
        { StatusLineTarget.Agy, "GEMINI_CONFIG_DIR" },
        { StatusLineTarget.Pi, "PI_AGENT_DIR" }
    };

    /// <summary>The one harness that loads a status-line file from a directory by itself.</summary>
    public static TheoryData<StatusLineTarget, string> AutoLoadingHarnesses => new()
    {
        { StatusLineTarget.Pi, ".pi/agent/extensions" }
    };

    /// <summary>The confirmed settings file per harness, as the literal path beneath the home.</summary>
    public static TheoryData<StatusLineTarget, string> ConfirmedSettingsFiles => new()
    {
        { StatusLineTarget.Claude, ".claude/settings.json" },
        { StatusLineTarget.Agy, ".gemini/antigravity-cli/settings.json" },
        { StatusLineTarget.Pi, ".pi/agent/settings.json" }
    };

    // -----------------------------------------------------------------------------------------
    // Target surface: exactly the three harnesses D9 keeps, and no OpenCode member.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheDeploymentTargetSetIsExactlyClaudeAgyAndPiAndDeclaresNoOpenCodeTarget()
    {
        string[] declaredTargetNames = [.. Enum.GetNames<StatusLineTarget>()];

        Assert.True(
            PinnedTargetNames.SequenceEqual(declaredTargetNames, StringComparer.Ordinal),
            $"StatusLineTarget declares [{Join(declaredTargetNames)}], not the pinned three-target " +
            $"scope [{Join(PinnedTargetNames)}]. OpenCode is deferred to a follow-up issue (D9), so " +
            "no OpenCode target may exist in this slice.");
        // The list comparison above already fails on an OpenCode member, but only as an unexplained
        // extra name. This states the removal the plan asks for by name, and it is what fails first
        // when a later change reintroduces the target.
        Assert.False(
            Enum.TryParse("OpenCode", true, out StatusLineTarget openCode),
            $"StatusLineTarget still declares the dropped OpenCode target '{openCode}'.");
    }

    // -----------------------------------------------------------------------------------------
    // Roots: the pinned staging segment, environment overrides, and the auto-load hazards.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllTargets))]
    public void EveryHarnessStagingRootIsThePinnedKyberOwnedSegmentBeneathItsHarnessRoot(StatusLineTarget target)
    {
        StatusLineTargetRoots roots = Roots();
        string expected = PinnedStagingRoot(target);

        string stagingRoot = roots.ResolveStagingRoot(target);

        Assert.True(
            IsSame(expected, stagingRoot),
            $"The {target} staging root is '{stagingRoot}', not the pinned '{expected}'.");
        Assert.True(
            Path.IsPathFullyQualified(stagingRoot),
            $"The {target} staging root '{stagingRoot}' is not fully qualified, so it would resolve " +
            "against the process working directory.");
        Assert.True(
            IsWithin(_temp.Path, stagingRoot),
            $"The {target} staging root '{stagingRoot}' is outside the injected home '{_temp.Path}'.");
    }

    [Theory]
    [MemberData(nameof(DocumentedOverrideVariables))]
    public void EachHarnessStagingRootHonoursItsDocumentedEnvironmentOverride(
        StatusLineTarget target,
        string variableName)
    {
        string overrideRoot = Path.Combine(_temp.Path, "override-" + variableName);
        string expected = Path.Combine(overrideRoot, ToNative(StagingSegment));
        // The lookup answers for this harness's variable and no other, so a resolver that consulted a
        // different name, or no name at all, would fall back to the home default and fail here.
        StatusLineTargetRoots roots = Roots(name =>
            string.Equals(name, variableName, StringComparison.Ordinal) ? overrideRoot : null);

        string stagingRoot = roots.ResolveStagingRoot(target);

        Assert.True(
            IsSame(expected, stagingRoot),
            $"The {target} staging root is '{stagingRoot}', not the pinned '{expected}' that " +
            $"{variableName}='{overrideRoot}' requires.");
    }

    [Theory]
    [MemberData(nameof(UnrelatedEnvironmentVariables))]
    public void AnUnrelatedEnvironmentVariableDoesNotMoveAStagingRoot(
        StatusLineTarget target,
        string unrelatedVariableName)
    {
        string decoyRoot = Path.Combine(_temp.Path, "decoy-" + unrelatedVariableName);
        StatusLineTargetRoots roots = Roots(name =>
            string.Equals(name, unrelatedVariableName, StringComparison.Ordinal) ? decoyRoot : null);

        string stagingRoot = roots.ResolveStagingRoot(target);

        Assert.True(
            IsSame(PinnedStagingRoot(target), stagingRoot),
            $"{unrelatedVariableName}='{decoyRoot}' moved the {target} staging root to " +
            $"'{stagingRoot}'; only the documented variable may move it, and the default is " +
            $"'{PinnedStagingRoot(target)}'.");
    }

    [Theory]
    [MemberData(nameof(DocumentedOverrideVariables))]
    public void AnEmptyOverrideFallsBackToThePinnedHomeDefaultRatherThanToARelativePath(
        StatusLineTarget target,
        string variableName)
    {
        StatusLineTargetRoots roots = Roots(name =>
            string.Equals(name, variableName, StringComparison.Ordinal) ? string.Empty : null);

        string stagingRoot = roots.ResolveStagingRoot(target);

        Assert.True(
            IsSame(PinnedStagingRoot(target), stagingRoot),
            $"An empty {variableName} left the {target} staging root at '{stagingRoot}', not the " +
            $"pinned home default '{PinnedStagingRoot(target)}'.");
    }

    [Theory]
    [MemberData(nameof(AutoLoadingHarnesses))]
    public void AnAutoLoadingHarnessResolvesExactlyItsDocumentedAutoLoadDirectoryOutsideItsStagingRoot(
        StatusLineTarget target,
        string autoLoadRelativePath)
    {
        StatusLineTargetRoots roots = Roots();
        string autoLoadDirectory = Path.Combine(_temp.Path, ToNative(autoLoadRelativePath));

        string[] autoLoadDirectories = [.. roots.ResolveAutoLoadDirectories(target)];
        string stagingRoot = roots.ResolveStagingRoot(target);

        Assert.True(
            autoLoadDirectories.Length == 1 && IsSame(autoLoadDirectories[0], autoLoadDirectory),
            $"{target} resolves auto-load directories [{Join(autoLoadDirectories)}], not exactly " +
            $"['{autoLoadDirectory}'].");
        Assert.False(
            IsSameOrWithin(autoLoadDirectory, stagingRoot),
            $"The {target} staging root '{stagingRoot}' is inside the auto-load directory " +
            $"'{autoLoadDirectory}', so a file deployed there would be active without the user " +
            "applying anything.");
    }

    [Theory]
    [InlineData(StatusLineTarget.Claude)]
    [InlineData(StatusLineTarget.Agy)]
    public void AHarnessWithoutADirectoryAutoLoadResolvesNoAutoLoadDirectory(StatusLineTarget target)
    {
        StatusLineTargetRoots roots = Roots();

        string[] autoLoadDirectories = [.. roots.ResolveAutoLoadDirectories(target)];
        string stagingRoot = roots.ResolveStagingRoot(target);

        Assert.True(
            autoLoadDirectories.Length == 0,
            $"{target} resolves auto-load directories [{Join(autoLoadDirectories)}], but it loads no " +
            "status-line file from a directory at all.");
        // The empty answer only means something alongside the staging root: a type that resolved
        // nothing whatsoever would satisfy the assertion above.
        Assert.True(
            IsSame(PinnedStagingRoot(target), stagingRoot),
            $"The {target} staging root is '{stagingRoot}', not the pinned '{PinnedStagingRoot(target)}'.");
    }

    // -----------------------------------------------------------------------------------------
    // Settings: the confirmed paths are resolved, and nothing ever creates or modifies one.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ConfirmedSettingsFiles))]
    public void EveryHarnessResolvesItsConfirmedSettingsFilePathBeneathTheInjectedHome(
        StatusLineTarget target,
        string settingsRelativePath)
    {
        StatusLineTargetRoots roots = Roots();
        string confirmedPath = SettingsSentinelPath(settingsRelativePath);

        IReadOnlyList<string> settingsPaths = roots.ResolveSettingsPaths(target);

        AssertContainsSettingPath(settingsPaths, confirmedPath, target);
        Assert.All(settingsPaths, settingsPath =>
        {
            Assert.True(
                Path.IsPathFullyQualified(settingsPath),
                $"Settings path '{settingsPath}' for {target} is not fully qualified.");
            Assert.True(
                IsWithin(_temp.Path, settingsPath),
                $"Settings path '{settingsPath}' for {target} is outside the injected home " +
                $"'{_temp.Path}'.");
        });
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public void DeployAndRemoveLeaveEveryConfirmedSettingsFileByteIdenticalAndNeverNameOne(
        StatusLineTarget target)
    {
        StatusLineTargetRoots roots = Roots();
        Dictionary<string, byte[]> sentinels = SeedConfirmedSettingsSentinels();
        string pinnedDeployedPath = PinnedStagingPath(target, PrimaryArtifactRelativePath);

        StatusLineDeploymentPlan plan = StatusLineDeploymentPlan.CreateDeploy(
            target,
            roots,
            [Artifact(PrimaryArtifactRelativePath, PrimaryArtifactContent)]);

        // A plan that is built but not applied is the dry run: it must already have written nothing.
        AssertSettingsSentinelsUnchanged(sentinels);
        AssertNoSettingsPathIsNamed(plan, sentinels, target);

        plan.Apply();

        // The settings guarantee is a negative one, so it is only observable once the deploy has
        // happened: a plan that wrote nothing would leave the sentinels alone for the wrong reason.
        Assert.True(
            File.Exists(pinnedDeployedPath),
            $"Deploy wrote nothing to '{pinnedDeployedPath}', so it says nothing about the settings " +
            "files it must leave untouched.");
        AssertSettingsSentinelsUnchanged(sentinels);

        StatusLineDeploymentPlan removal = StatusLineDeploymentPlan.CreateRemove(target, roots);
        AssertNoSettingsPathIsNamed(removal, sentinels, target);

        removal.Apply();

        AssertSettingsSentinelsUnchanged(sentinels);
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public void DeployAndRemoveNeverCreateAConfirmedSettingsFileThatWasNotAlreadyThere(
        StatusLineTarget target)
    {
        StatusLineTargetRoots roots = Roots();
        string pinnedDeployedPath = PinnedStagingPath(target, PrimaryArtifactRelativePath);

        StatusLineDeploymentPlan.CreateDeploy(
            target,
            roots,
            [Artifact(PrimaryArtifactRelativePath, PrimaryArtifactContent)]).Apply();

        // The never-create guarantee is a negative one, so it is only observable once a deploy has
        // happened: a plan that wrote nothing would leave the settings locations alone for the wrong
        // reason. This has to be asserted before the removal below, which deletes the very file it
        // proves was written.
        Assert.True(
            File.Exists(pinnedDeployedPath),
            $"Deploy wrote nothing to '{pinnedDeployedPath}', so it says nothing about the settings " +
            "files it must never create.");
        AssertNoSettingsFileWasCreated(target);

        StatusLineDeploymentPlan.CreateRemove(target, roots).Apply();

        AssertNoSettingsFileWasCreated(target);
    }

    // -----------------------------------------------------------------------------------------
    // Deploy: pinned staging paths, unmanaged-collision refusal, and the receipt's SHA-256.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllTargets))]
    public void DeployPlansOneWritePerSourceFileAtItsPinnedStagingPath(StatusLineTarget target)
    {
        StatusLineTargetRoots roots = Roots();

        StatusLineDeploymentPlan plan = StatusLineDeploymentPlan.CreateDeploy(target, roots, Artifacts());

        Assert.Equal(target, plan.Target);
        Assert.True(
            IsSame(PinnedStagingRoot(target), plan.StagingRoot),
            $"The {target} plan stages into '{plan.StagingRoot}', not the pinned " +
            $"'{PinnedStagingRoot(target)}'.");

        string[] plannedRelativePaths =
        [
            .. plan.PlannedFileChanges
                .Select(change => change.RelativePath)
                .Order(StringComparer.Ordinal)
        ];
        string[] expectedRelativePaths =
        [
            .. Artifacts()
                .Select(file => file.RelativePath)
                .Order(StringComparer.Ordinal)
        ];
        Assert.True(
            expectedRelativePaths.SequenceEqual(plannedRelativePaths, StringComparer.Ordinal),
            $"The {target} plan changes [{Join(plannedRelativePaths)}], not " +
            $"[{Join(expectedRelativePaths)}].");
        Assert.All(plan.PlannedFileChanges, change =>
            Assert.Equal(StatusLineFileChangeKind.Write, change.Kind));

        string[] plannedPhysicalPaths = [.. plan.PlannedPhysicalPaths.Order(StringComparer.Ordinal)];
        string[] expectedPhysicalPaths =
        [
            .. Artifacts()
                .Select(file => PinnedStagingPath(target, file.RelativePath))
                .Order(StringComparer.Ordinal)
        ];
        Assert.True(
            expectedPhysicalPaths.SequenceEqual(plannedPhysicalPaths, StringComparer.Ordinal),
            $"The {target} plan writes [{Join(plannedPhysicalPaths)}], not the pinned staging paths " +
            $"[{Join(expectedPhysicalPaths)}].");
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public void DeployWritesEverySourceFileByteForByteAtItsPinnedStagingPathAndRecordsItsSha256(
        StatusLineTarget target)
    {
        StatusLineTargetRoots roots = Roots();

        StatusLineDeploymentPlan.CreateDeploy(target, roots, Artifacts()).Apply();

        StatusLineReceipt? receipt = StatusLineDeploymentPlan.ReadReceipt(target, roots);
        foreach (StatusLineDeploymentFile file in Artifacts())
        {
            AssertReceiptRecordsSha256(receipt, target, file.RelativePath, file.Content);
            AssertFileBytes(PinnedStagingPath(target, file.RelativePath), file.Content);
        }

        Assert.All(receipt?.Files ?? [], owned => Assert.Matches("^[0-9a-fA-F]{64}$", owned.Sha256));
    }

    [Fact]
    public void DeployRefusesToOverwriteAnUnmanagedFileAtAPinnedStagingTargetPath()
    {
        StatusLineTargetRoots roots = Roots();
        string occupiedPath = PinnedStagingPath(StatusLineTarget.Claude, PrimaryArtifactRelativePath);
        Directory.CreateDirectory(RequireParent(occupiedPath));
        File.WriteAllText(occupiedPath, UnmanagedFileContent);

        StatusLineDeploymentConflictException exception = Assert.Throws<StatusLineDeploymentConflictException>(() =>
            StatusLineDeploymentPlan.CreateDeploy(
                StatusLineTarget.Claude,
                roots,
                [Artifact(PrimaryArtifactRelativePath, PrimaryArtifactContent)]));

        Assert.Contains(PrimaryArtifactRelativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal(UnmanagedFileContent, File.ReadAllText(occupiedPath));
    }

    /// <summary>
    /// A redeploy must not replace a Kyber-owned file the operator edited after the deploy that
    /// wrote it. <c>RefuseUnmanagedOccupant</c>'s own remarks name a locally-edited owned file as
    /// refused rather than replaced, and <c>CreateRemove</c> already preserves such a file, so a
    /// redeploy that overwrites it discards the operator's bytes while <c>status</c> goes on
    /// reporting the file as Kyber's.
    /// </summary>
    /// <remarks>
    /// The occupant check has to compare the receipt's digest against the bytes <em>on disk</em>.
    /// The incoming artifact's digest is the wrong side of that comparison: an unchanged artifact
    /// set carries the same digest the receipt recorded, so comparing against it accepts any
    /// occupant at all and turns every redeploy into an overwrite.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllTargets))]
    public void RedeployProtectsALocallyModifiedOwnedFile(StatusLineTarget target)
    {
        StatusLineTargetRoots roots = Roots();
        string deployedPath = PinnedStagingPath(target, PrimaryArtifactRelativePath);

        StatusLineDeploymentPlan.CreateDeploy(target, roots, Artifacts()).Apply();

        AssertFileBytes(deployedPath, PrimaryArtifact().Content);
        AssertReceiptRecordsSha256(
            StatusLineDeploymentPlan.ReadReceipt(target, roots),
            target,
            PrimaryArtifactRelativePath,
            PrimaryArtifact().Content);

        File.WriteAllText(deployedPath, LocallyEditedFileContent);

        StatusLineDeploymentConflictException exception =
            Assert.Throws<StatusLineDeploymentConflictException>(() =>
                StatusLineDeploymentPlan.CreateDeploy(target, roots, Artifacts()));

        Assert.Contains(PrimaryArtifactRelativePath, exception.Message, StringComparison.Ordinal);
        Assert.True(
            string.Equals(LocallyEditedFileContent, File.ReadAllText(deployedPath), StringComparison.Ordinal),
            $"The {target} redeploy replaced the operator's bytes at '{deployedPath}' with the " +
            "artifact's, even though the receipt still owns the file.");
    }

    // -----------------------------------------------------------------------------------------
    // Remove: only receipt-owned, unmodified files, at their pinned staging paths.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void RemoveDeletesEveryReceiptOwnedUnmodifiedFileAtItsPinnedStagingPathAndLeavesNoOwnershipBehind()
    {
        StatusLineTargetRoots roots = Roots();
        StatusLineDeploymentPlan.CreateDeploy(StatusLineTarget.Claude, roots, Artifacts()).Apply();

        foreach (StatusLineDeploymentFile file in Artifacts())
            AssertFileBytes(PinnedStagingPath(StatusLineTarget.Claude, file.RelativePath), file.Content);

        StatusLineDeploymentPlan removal = StatusLineDeploymentPlan.CreateRemove(StatusLineTarget.Claude, roots);

        AssertPlansADeletionOfEvery(removal, Artifacts(), StatusLineTarget.Claude);

        removal.Apply();

        foreach (StatusLineDeploymentFile file in Artifacts())
        {
            string pinnedPath = PinnedStagingPath(StatusLineTarget.Claude, file.RelativePath);
            Assert.False(
                File.Exists(pinnedPath),
                $"Remove left the receipt-owned file '{pinnedPath}' behind.");
        }

        int remainingCount = StatusLineDeploymentPlan.ReadReceipt(StatusLineTarget.Claude, roots)?.Files.Count ?? 0;
        Assert.True(
            remainingCount == 0,
            $"Remove left {remainingCount} owned file(s) recorded after deleting all of them.");
    }

    [Fact]
    public void RemoveKeepsALocallyModifiedOwnedFileAndNeverTouchesAFileItDoesNotOwn()
    {
        StatusLineTargetRoots roots = Roots();
        StatusLineDeploymentPlan.CreateDeploy(StatusLineTarget.Pi, roots, Artifacts()).Apply();

        string modifiedPath = PinnedStagingPath(StatusLineTarget.Pi, PrimaryArtifactRelativePath);
        string unmodifiedPath = PinnedStagingPath(StatusLineTarget.Pi, SecondaryArtifactRelativePath);
        string unownedPath = Path.Combine(PinnedStagingRoot(StatusLineTarget.Pi), "user-notes.md");
        AssertFileBytes(modifiedPath, PrimaryArtifact().Content);
        AssertFileBytes(unmodifiedPath, SecondaryArtifact().Content);

        File.WriteAllText(modifiedPath, "locally edited by the user\n");
        Directory.CreateDirectory(PinnedStagingRoot(StatusLineTarget.Pi));
        File.WriteAllText(unownedPath, "not kyber's file\n");

        StatusLineDeploymentPlan removal = StatusLineDeploymentPlan.CreateRemove(StatusLineTarget.Pi, roots);

        Assert.DoesNotContain(
            removal.PlannedFileChanges,
            change => string.Equals(change.RelativePath, PrimaryArtifactRelativePath, StringComparison.Ordinal));

        removal.Apply();

        Assert.Equal("locally edited by the user\n", File.ReadAllText(modifiedPath));
        Assert.Equal("not kyber's file\n", File.ReadAllText(unownedPath));
        Assert.False(
            File.Exists(unmodifiedPath),
            $"Remove kept the unmodified owned file '{unmodifiedPath}'.");

        IReadOnlyList<StatusLineOwnedFile> remaining =
            StatusLineDeploymentPlan.ReadReceipt(StatusLineTarget.Pi, roots)?.Files ?? [];
        foreach (StatusLineOwnedFile owned in remaining)
        {
            if (string.Equals(owned.RelativePath, PrimaryArtifactRelativePath, StringComparison.Ordinal))
                return;
        }

        Assert.Fail(
            $"The receipt for Pi no longer records '{PrimaryArtifactRelativePath}', so the file the " +
            "user edited is no longer owned and a later removal could not see it.");
    }

    [Fact]
    public void RemoveDropsMissingFilesFromReceipt()
    {
        StatusLineTargetRoots roots = Roots();
        StatusLineDeploymentPlan.CreateDeploy(StatusLineTarget.Claude, roots, Artifacts()).Apply();

        string primaryPath = PinnedStagingPath(StatusLineTarget.Claude, PrimaryArtifactRelativePath);
        File.Delete(primaryPath);

        StatusLineDeploymentPlan removal = StatusLineDeploymentPlan.CreateRemove(StatusLineTarget.Claude, roots);

        Assert.DoesNotContain(
            removal.Receipt.Files,
            file => string.Equals(file.RelativePath, PrimaryArtifactRelativePath, StringComparison.Ordinal));

        removal.Apply();

        StatusLineReceipt? remaining = StatusLineDeploymentPlan.ReadReceipt(StatusLineTarget.Claude, roots);
        Assert.Null(remaining);
    }

    [Fact]
    public void ReceiptRecordsStagingRootAndThrowsConflictWhenOverrideChanges()
    {
        string overrideDirA = Path.Combine(_temp.Path, "overrideA");
        string overrideDirB = Path.Combine(_temp.Path, "overrideB");
        Directory.CreateDirectory(overrideDirA);
        Directory.CreateDirectory(overrideDirB);

        string currentOverride = overrideDirA;
        StatusLineTargetRoots roots = Roots(varName => varName == ClaudeConfigDirectoryVariable ? currentOverride : null);

        StatusLineDeploymentPlan.CreateDeploy(StatusLineTarget.Claude, roots, Artifacts()).Apply();

        currentOverride = overrideDirB;
        StatusLineTargetRoots changedRoots = Roots(varName => varName == ClaudeConfigDirectoryVariable ? currentOverride : null);

        StatusLineDeploymentConflictException ex = Assert.Throws<StatusLineDeploymentConflictException>(() =>
            StatusLineDeploymentPlan.ReadReceipt(StatusLineTarget.Claude, changedRoots));

        Assert.Contains(overrideDirA, ex.Message, StringComparison.Ordinal);
        Assert.Contains(overrideDirB, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployRefusesSymlinkInStagingPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        StatusLineTargetRoots roots = Roots();
        string harnessRoot = Path.Combine(_temp.Path, HarnessRootRelativePath(StatusLineTarget.Pi));
        Directory.CreateDirectory(harnessRoot);

        string targetDir = Path.Combine(_temp.Path, "outside");
        Directory.CreateDirectory(targetDir);

        string linkPath = Path.Combine(harnessRoot, "kyber");
        File.CreateSymbolicLink(linkPath, targetDir);

        Assert.Throws<StatusLineDeploymentConflictException>(() =>
            StatusLineDeploymentPlan.CreateDeploy(StatusLineTarget.Pi, roots, Artifacts()));
    }

    // -----------------------------------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------------------------------

    private StatusLineTargetRoots Roots(Func<string, string?>? getEnvironmentVariable = null)
    {
        return new StatusLineTargetRoots(getEnvironmentVariable ?? (_ => null), _temp.Path);
    }

    private static StatusLineDeploymentFile Artifact(string relativePath, string content)
    {
        return new StatusLineDeploymentFile(relativePath, Encoding.UTF8.GetBytes(content));
    }

    private static StatusLineDeploymentFile PrimaryArtifact()
    {
        return Artifact(PrimaryArtifactRelativePath, PrimaryArtifactContent);
    }

    private static StatusLineDeploymentFile SecondaryArtifact()
    {
        return Artifact(SecondaryArtifactRelativePath, SecondaryArtifactContent);
    }

    private static StatusLineDeploymentFile[] Artifacts()
    {
        return [PrimaryArtifact(), SecondaryArtifact()];
    }

    private static string HarnessRootRelativePath(StatusLineTarget target)
    {
        return target switch
        {
            StatusLineTarget.Claude => ".claude",
            StatusLineTarget.Agy => ".gemini/antigravity-cli",
            StatusLineTarget.Pi => ".pi/agent",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "No pinned status-line root exists for this harness.")
        };
    }

    private string PinnedStagingRoot(StatusLineTarget target)
    {
        return Path.Combine(_temp.Path, ToNative(HarnessRootRelativePath(target)), ToNative(StagingSegment));
    }

    private string PinnedStagingPath(StatusLineTarget target, string relativePath)
    {
        return Path.Combine(PinnedStagingRoot(target), ToNative(relativePath));
    }

    private string SettingsSentinelPath(string settingsRelativePath)
    {
        return Path.Combine(_temp.Path, ToNative(settingsRelativePath));
    }

    private static string ToNative(string relativePath)
    {
        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string RequireParent(string path)
    {
        return Path.GetDirectoryName(path) ??
               throw new InvalidOperationException($"Path '{path}' has no parent directory.");
    }

    private static string Digest(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static string Join(IEnumerable<string> values)
    {
        return string.Join(", ", values.Select(value => $"'{value}'"));
    }

    private static string Describe(IReadOnlyList<string> digests)
    {
        return digests.Count == 0 ? "no SHA-256" : Join(digests);
    }

    /// <summary>
    /// Asserts that the bytes at <paramref name="path"/> are exactly <paramref name="expected"/>,
    /// reporting the path and both lengths rather than letting <see cref="File.ReadAllBytes(string)"/>
    /// throw when the deployment wrote nothing. A missing file is a failed deployment — the
    /// assertion this suite is making — and a thrown <see cref="FileNotFoundException"/> would be a
    /// fixture fault, which the plan's T2 row does not accept as RED evidence.
    /// </summary>
    private static void AssertFileBytes(string path, ReadOnlyMemory<byte> expected)
    {
        byte[] actual = File.Exists(path) ? File.ReadAllBytes(path) : [];
        Assert.True(
            expected.Span.SequenceEqual(actual),
            $"'{path}' holds {actual.Length} byte(s), not the {expected.Length} byte(s) the " +
            "deployment must have written there.");
    }

    /// <summary>
    /// Asserts the receipt records <paramref name="relativePath"/> with the SHA-256 of the deployed
    /// bytes, naming the digests it did record when it does not.
    /// </summary>
    private static void AssertReceiptRecordsSha256(
        StatusLineReceipt? receipt,
        StatusLineTarget target,
        string relativePath,
        ReadOnlyMemory<byte> content)
    {
        string expectedDigest = Digest(content.Span);
        string[] recorded =
        [
            .. (receipt?.Files ?? [])
            .Where(file => string.Equals(file.RelativePath, relativePath, StringComparison.Ordinal))
            .Select(file => file.Sha256)
        ];
        foreach (string digest in recorded)
        {
            if (string.Equals(digest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                return;
        }

        Assert.Fail(
            $"The receipt for {target} records {Describe(recorded)} for '{relativePath}', not the " +
            $"deployed bytes' SHA-256 '{expectedDigest}'.");
    }

    private static void AssertPlansADeletionOfEvery(
        StatusLineDeploymentPlan removal,
        IReadOnlyList<StatusLineDeploymentFile> owned,
        StatusLineTarget target)
    {
        string[] planned =
        [
            .. removal.PlannedFileChanges
                .Select(change => $"{change.Kind} {change.RelativePath}")
        ];
        foreach (StatusLineDeploymentFile file in owned)
        {
            bool deletes = removal.PlannedFileChanges.Any(change =>
                change.Kind == StatusLineFileChangeKind.Delete &&
                string.Equals(change.RelativePath, file.RelativePath, StringComparison.Ordinal));
            if (deletes)
                continue;

            Assert.Fail(
                $"The {target} removal plans [{Join(planned)}], so it does not delete the " +
                $"receipt-owned '{file.RelativePath}'.");
        }
    }

    private static void AssertContainsSettingPath(
        IReadOnlyList<string> settingsPaths,
        string confirmedPath,
        StatusLineTarget target)
    {
        foreach (string settingsPath in settingsPaths)
        {
            if (IsSame(settingsPath, confirmedPath))
                return;
        }

        Assert.Fail(
            $"The {target} settings paths [{Join(settingsPaths)}] do not include the confirmed " +
            $"'{confirmedPath}'.");
    }

    /// <summary>
    /// Asserts that none of the confirmed settings locations exists, so neither a deploy nor a removal
    /// created one by create-if-missing (C2).
    /// </summary>
    private void AssertNoSettingsFileWasCreated(StatusLineTarget target)
    {
        Assert.All(ConfirmedSettingsRelativePaths, settingsRelativePath =>
        {
            string settingsPath = SettingsSentinelPath(settingsRelativePath);
            Assert.False(
                File.Exists(settingsPath),
                $"'{settingsPath}' was created for {target}, but Kyber Utilities never owns a " +
                "settings file, not even by create-if-missing.");
        });
    }

    private Dictionary<string, byte[]> SeedConfirmedSettingsSentinels()
    {
        Dictionary<string, byte[]> sentinels = new(StringComparer.Ordinal);
        foreach (string settingsRelativePath in ConfirmedSettingsRelativePaths)
        {
            string settingsPath = SettingsSentinelPath(settingsRelativePath);
            Directory.CreateDirectory(RequireParent(settingsPath));
            byte[] sentinel = Encoding.UTF8.GetBytes("{\"sentinel\":\"never-touched-by-kyber\"}\n");
            File.WriteAllBytes(settingsPath, sentinel);
            sentinels[settingsPath] = sentinel;
        }

        return sentinels;
    }

    private static void AssertSettingsSentinelsUnchanged(IReadOnlyDictionary<string, byte[]> sentinels)
    {
        foreach ((string settingsPath, byte[] expected) in sentinels)
        {
            Assert.True(File.Exists(settingsPath), $"Settings sentinel '{settingsPath}' was deleted.");
            byte[] actual = File.ReadAllBytes(settingsPath);
            Assert.True(
                expected.AsSpan().SequenceEqual(actual),
                $"Settings file '{settingsPath}' was modified, which Kyber Utilities never does.");
        }
    }

    private static void AssertNoSettingsPathIsNamed(
        StatusLineDeploymentPlan plan,
        IReadOnlyDictionary<string, byte[]> sentinels,
        StatusLineTarget target)
    {
        foreach (string settingsPath in sentinels.Keys)
        {
            Assert.False(
                IsSameOrWithin(settingsPath, plan.StagingRoot),
                $"The {target} plan stages into the settings path '{settingsPath}'.");
            Assert.All(plan.PlannedPhysicalPaths, plannedPath =>
                Assert.False(
                    IsSameOrWithin(settingsPath, plannedPath),
                    $"The {target} plan touches the settings path '{settingsPath}' via '{plannedPath}'."));
        }
    }

    /// <summary>
    /// Containment over the same filesystem semantics the Squad deployment helpers use.
    /// </summary>
    /// <remarks>
    /// A path that is not fully qualified cannot be contained by anything, so this answers
    /// <see langword="false"/> instead of forwarding it. That keeps an unresolved staging root — which
    /// is exactly what the RED run produces — an assertion failure naming the empty value, rather than
    /// a <see cref="Path.GetFullPath(string)"/> exception thrown from inside the helper, which the
    /// plan's T2 row would not accept as valid RED evidence.
    /// </remarks>
    private static bool IsWithin(string root, string candidate)
    {
        return IsContainable(candidate) && SquadFileSystemPathSemantics.IsWithin(root, candidate);
    }

    private static bool IsSame(string first, string second)
    {
        return IsContainable(first) && IsContainable(second) && SquadFileSystemPathSemantics.AreSame(first, second);
    }

    private static bool IsContainable(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
    }

    private static bool IsSameOrWithin(string root, string candidate)
    {
        return IsSame(root, candidate) || IsWithin(root, candidate);
    }
}

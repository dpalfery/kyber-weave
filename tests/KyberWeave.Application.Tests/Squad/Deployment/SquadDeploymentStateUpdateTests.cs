using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Deployment-state contracts: update, replacement and locally edited files. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateUpdateTests : InMemoryDeploymentStateTestBase
{
    /// <summary>
    ///     Reads the raw owning target tokens recorded for a degraded path from the
    ///     <see cref="SquadDegradedFile"/> contract, so a change to its shape fails at compile
    ///     time rather than returning empty owners.
    /// </summary>
    private static IReadOnlyList<string> OwningTargetsOf(SquadDeploymentPlan plan, string relativePath) =>
        plan.DegradedFiles
            .Where(degraded => degraded.RelativePath == relativePath)
            .Select(degraded => degraded.Target)
            .ToList();

    [Fact]
    public void CreateUpdateLocallyEditedManagedFileWithoutReplacePreservesFileAndReceiptHash()
    {
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false));
        Write(Root, ".codex/agents/conductor.toml", "operator edit");
        SquadDeploymentPlan plan = Planner.CreateUpdate(
            Root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated body")],
            oldReceipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        TransactionAt(Root).Execute(plan);

        Assert.Equal(
            "operator edit",
            Read(Root, ".codex/agents/conductor.toml"));
        SquadOwnedFile retained = Assert.Single(plan.Receipt.Files);
        Assert.Equal(Digest("installed body"), retained.Sha256);
        Assert.False(retained.Adopted);
    }

    /// <summary>
    ///     An unreadable deployed entry cannot be proven unedited, so the plan keeps its record and leaves the
    ///     bytes alone. The path must also be named in <c>DegradedFiles</c>; without that the caller has no way
    ///     to tell a preserved-but-unverified file from one that was checked and found unchanged.
    /// </summary>
    [Fact]
    public void CreateUpdateWithAnUnreadableDeployedEntryRecordsThePathInDegradedFiles()
    {
        const string entry = ".codex/agents/conductor.toml";
        SquadReceipt oldReceipt = Receipt(new SquadOwnedFile(entry, Digest("installed body"), "codex", false));
        Write(Root, entry, "installed body");
        FailReadsOf(ToPlatformPath(Root, entry));

        SquadDeploymentPlan plan;
        try
        {
            plan = Planner.CreateUpdate(
                Root,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(entry, "updated body")],
                oldReceipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));
        }
        finally
        {
            Files.ClearFaults();
        }

        Assert.True(
            OwningTargetsOf(plan, entry).Contains("codex"),
            $"The degraded '{entry}' must record its owning target 'codex'; it records [{string.Join(", ", OwningTargetsOf(plan, entry))}].");
        Assert.Contains(plan.Receipt.Files, file => file.RelativePath == entry);
    }

    /// <summary>
    ///     An unreadable file with an unknown target token is still reported by name: the degraded
    ///     record keeps the raw token verbatim, so a corrupt or future token never reads as clean.
    /// </summary>
    [Fact]
    public void CreateUpdateWithAnUnreadableDeployedEntryWithAnUnknownTargetStillRecordsTheRawToken()
    {
        const string entry = ".codex/agents/conductor.toml";
        const string unknownTarget = "future-harness";
        SquadReceipt oldReceipt = Receipt(new SquadOwnedFile(entry, Digest("installed body"), unknownTarget, false));
        Write(Root, entry, "installed body");
        FailReadsOf(ToPlatformPath(Root, entry));

        SquadDeploymentPlan plan;
        try
        {
            plan = Planner.CreateUpdate(
                Root,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(entry, "updated body", unknownTarget)],
                oldReceipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));
        }
        finally
        {
            Files.ClearFaults();
        }

        Assert.True(
            OwningTargetsOf(plan, entry).Contains(unknownTarget),
            $"The degraded '{entry}' must record its raw owning token '{unknownTarget}'; it records [{string.Join(", ", OwningTargetsOf(plan, entry))}].");
        Assert.Contains(plan.Receipt.Files, file => file.RelativePath == entry);
    }

    /// <summary>
    ///     A plan can span several targets, and a degraded path is only meaningful with its owner: the warning
    ///     must name the target that owns the unreadable file. Without the owner recorded, a caller naming all
    ///     targets would blame a healthy harness for another target's unreadable file.
    /// </summary>
    [Fact]
    public void CreateUpdateRecordsTheOwningTargetOfEachDegradedFileWhenThePlanCoversSeveralTargets()
    {
        const string codexEntry = ".codex/agents/conductor.toml";
        const string claudeEntry = ".claude/agents/conductor.md";
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(codexEntry, Digest("codex body"), "codex", false),
            new SquadOwnedFile(claudeEntry, Digest("claude body"), "claude", false));
        Write(Root, codexEntry, "codex body");
        Write(Root, claudeEntry, "claude body");
        FailReadsOf(ToPlatformPath(Root, codexEntry));

        SquadDeploymentPlan plan;
        try
        {
            plan = Planner.CreateUpdate(
                Root,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [
                    Rendered(codexEntry, "codex body", "codex"),
                    Rendered(claudeEntry, "claude body", "claude")
                ],
                oldReceipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));
        }
        finally
        {
            Files.ClearFaults();
        }

        Assert.Single(plan.DegradedFiles);
        IReadOnlyList<string> owners = OwningTargetsOf(plan, codexEntry);
        Assert.True(
            owners.SequenceEqual(["codex"]),
            $"The degraded '{codexEntry}' must record exactly its owning target 'codex'; it records [{string.Join(", ", owners)}].");
        Assert.Empty(OwningTargetsOf(plan, claudeEntry));
    }

    /// <summary>
    ///     A degraded-file warning quotes a receipt-sourced token, so a hand-edited receipt cannot
    ///     inject a second log line: control characters are neutralized while known tokens pass
    ///     through byte-for-byte.
    /// </summary>
    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("future-harness", "future-harness")]
    [InlineData("evil\ninjected", "evil?injected")]
    [InlineData("carriage\rreturn", "carriage?return")]
    [InlineData("tab\there", "tab?here")]
    [InlineData("", "")]
    public void SanitizeTargetTokenForWarningKeepsKnownTokensAndNeutralizesControlCharacters(
        string token,
        string expected)
    {
        Assert.Equal(expected, SquadLifecycleService.SanitizeTargetTokenForWarning(token));
    }

    /// <summary>
    ///     Resource removal cannot broaden update authority: unchanged obsolete resources may be
    ///     deleted, while locally edited current or obsolete resources remain owned at their prior hash.
    /// </summary>
    [Fact]
    public void ResourceUpdateAndRemovalPreserveLocallyEditedReceiptOwnedFiles()
    {
        const string currentResource = ".codex/agents/conductor/references/plan-path.md";
        const string editedObsoleteResource = ".codex/agents/conductor/references/legacy.md";
        const string unchangedObsoleteResource = ".codex/skills/product-owner/references/legacy.md";
        SquadOwnedFile currentOwned = new(currentResource, Digest("installed plan\n"), "codex", false);
        SquadOwnedFile editedObsoleteOwned = new(
            editedObsoleteResource,
            Digest("installed legacy agent\n"),
            "codex",
            false);
        SquadOwnedFile unchangedObsoleteOwned = new(
            unchangedObsoleteResource,
            Digest("installed legacy skill\n"),
            "codex",
            false);
        SquadReceipt receipt = Receipt(currentOwned, editedObsoleteOwned, unchangedObsoleteOwned);
        Write(Root, currentResource, "operator plan edit\n");
        Write(Root, editedObsoleteResource, "operator legacy edit\n");
        Write(Root, unchangedObsoleteResource, "installed legacy skill\n");
        WriteState(Root, Lock(), receipt);
        SquadDeploymentPlan plan = Planner.CreateUpdate(
            Root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(currentResource, "updated plan\n")],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        TransactionAt(Root).Execute(plan);

        Assert.Equal("operator plan edit\n", Read(Root, currentResource));
        Assert.Equal("operator legacy edit\n", Read(Root, editedObsoleteResource));
        Assert.False(Files.FileExists(ToPlatformPath(Root, unchangedObsoleteResource)));
        SquadReceipt persisted = Assert.IsType<SquadReceipt>(StoreAt(Root).ReadReceipt(
            Root,
            SquadDeploymentScope.Project));
        Assert.Equal(
            [currentOwned, editedObsoleteOwned],
            persisted.Files);
    }

    /// <summary>
    ///     When updating an existing deployment from Antigravity's fallback role-skill lowering
    ///     shape (.agents/skills/&lt;name&gt;/SKILL.md) to its native agent shape (.agents/agents/&lt;name&gt;/agent.md),
    ///     receipt-diff logic in <see cref="SquadDeploymentPlanner.CreateUpdate" /> plans a Delete mutation
    ///     for orphaned fallback skill files whose on-disk bytes still match the previous receipt digest,
    ///     and retains orphaned skill files whose bytes were locally edited by an operator.
    /// </summary>
    [Fact]
    public void CreateUpdateReceiptDiffRetiresFallbackSkillPathsOnNativeAntigravityUpdateAndRetainsLocallyEdited()
    {
        const string target = "antigravity";
        const string retiredSkillPath = ".agents/skills/worker/SKILL.md";
        const string editedSkillPath = ".agents/skills/reviewer/SKILL.md";
        const string installedWorkerContent =
            "---\nname: worker\ndescription: fallback worker skill\n---\nFallback worker instructions\n";
        const string installedReviewerContent =
            "---\nname: reviewer\ndescription: fallback reviewer skill\n---\nFallback reviewer instructions\n";
        const string locallyEditedReviewerContent =
            "---\nname: reviewer\ndescription: operator edited reviewer\n---\nCustom operator instructions\n";

        const string nativeWorkerAgentPath = ".agents/agents/worker/agent.md";
        const string nativeReviewerAgentPath = ".agents/agents/reviewer/agent.md";
        const string nativeWorkerAgentContent =
            "---\nname: worker\ndescription: native worker agent\nenable_write_tools: true\n---\nNative worker instructions\n";
        const string nativeReviewerAgentContent =
            "---\nname: reviewer\ndescription: native reviewer agent\nenable_write_tools: true\n---\nNative reviewer instructions\n";

        SquadOwnedFile retiredSkillOwned = new(
            retiredSkillPath,
            Digest(installedWorkerContent),
            target,
            false);
        SquadOwnedFile editedSkillOwned = new(
            editedSkillPath,
            Digest(installedReviewerContent),
            target,
            false);
        SquadReceipt previousReceipt = Receipt(retiredSkillOwned, editedSkillOwned);

        Write(Root, retiredSkillPath, installedWorkerContent);
        Write(Root, editedSkillPath, locallyEditedReviewerContent);
        WriteState(Root, Lock(), previousReceipt);

        // Verify pre-update on-disk state: fallback files exist with their initial contents, native paths do not yet exist
        Assert.True(Files.FileExists(ToPlatformPath(Root, retiredSkillPath)));
        Assert.Equal(installedWorkerContent, Read(Root, retiredSkillPath));
        Assert.True(Files.FileExists(ToPlatformPath(Root, editedSkillPath)));
        Assert.Equal(locallyEditedReviewerContent, Read(Root, editedSkillPath));
        Assert.False(Files.FileExists(ToPlatformPath(Root, nativeWorkerAgentPath)));
        Assert.False(Files.FileExists(ToPlatformPath(Root, nativeReviewerAgentPath)));

        SquadDeploymentPlan plan = Planner.CreateUpdate(
            Root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [
                Rendered(nativeWorkerAgentPath, nativeWorkerAgentContent, target),
                Rendered(nativeReviewerAgentPath, nativeReviewerAgentContent, target)
            ],
            previousReceipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        // Verify planned changes: unmodified fallback path is planned for Delete; native paths are planned for Write; locally edited path is not touched
        Assert.Contains(
            plan.PlannedFileChanges,
            change => change.RelativePath == retiredSkillPath &&
                      change.Target == target &&
                      change.Kind == SquadFileMutationKind.Delete);
        Assert.Contains(
            plan.PlannedFileChanges,
            change => change.RelativePath == nativeWorkerAgentPath &&
                      change.Target == target &&
                      change.Kind == SquadFileMutationKind.Write);
        Assert.Contains(
            plan.PlannedFileChanges,
            change => change.RelativePath == nativeReviewerAgentPath &&
                      change.Target == target &&
                      change.Kind == SquadFileMutationKind.Write);
        Assert.DoesNotContain(
            plan.PlannedFileChanges,
            change => change.RelativePath == editedSkillPath);

        // Verify planned mutations and preconditions
        SquadFileMutation deleteMutation = Assert.Single(
            plan.FileMutations,
            m => m.RelativePath == retiredSkillPath);
        Assert.Equal(SquadFileMutationKind.Delete, deleteMutation.Kind);
        Assert.Equal(target, deleteMutation.Target);

        SquadFileMutation workerWrite = Assert.Single(
            plan.FileMutations,
            m => m.RelativePath == nativeWorkerAgentPath);
        Assert.Equal(SquadFileMutationKind.Write, workerWrite.Kind);
        Assert.Equal(target, workerWrite.Target);
        Assert.Equal(nativeWorkerAgentContent, Encoding.UTF8.GetString(workerWrite.Content!.Value.Span));

        SquadFileMutation reviewerWrite = Assert.Single(
            plan.FileMutations,
            m => m.RelativePath == nativeReviewerAgentPath);
        Assert.Equal(SquadFileMutationKind.Write, reviewerWrite.Kind);
        Assert.Equal(target, reviewerWrite.Target);
        Assert.Equal(nativeReviewerAgentContent, Encoding.UTF8.GetString(reviewerWrite.Content!.Value.Span));

        Assert.DoesNotContain(plan.FileMutations, m => m.RelativePath == editedSkillPath);

        SquadFilePrecondition deletePrecondition = Assert.Single(
            plan.FilePreconditions,
            p => p.RelativePath == retiredSkillPath);
        Assert.Equal(SquadFilePreconditionKind.ExactFile, deletePrecondition.Kind);
        Assert.Equal(Digest(installedWorkerContent), deletePrecondition.Sha256);

        SquadFilePrecondition workerPrecondition = Assert.Single(
            plan.FilePreconditions,
            p => p.RelativePath == nativeWorkerAgentPath);
        Assert.Equal(SquadFilePreconditionKind.Missing, workerPrecondition.Kind);

        SquadFilePrecondition reviewerPrecondition = Assert.Single(
            plan.FilePreconditions,
            p => p.RelativePath == nativeReviewerAgentPath);
        Assert.Equal(SquadFilePreconditionKind.Missing, reviewerPrecondition.Kind);

        // Verify planned receipt: retired skill is omitted; edited skill is retained at original digest; new native agents are owned
        Assert.DoesNotContain(plan.Receipt.Files, f => f.RelativePath == retiredSkillPath);

        SquadOwnedFile retainedSkill = Assert.Single(
            plan.Receipt.Files,
            f => f.RelativePath == editedSkillPath);
        Assert.Equal(Digest(installedReviewerContent), retainedSkill.Sha256);
        Assert.False(retainedSkill.Adopted);
        Assert.Equal(target, retainedSkill.Target);

        SquadOwnedFile plannedWorkerAgent = Assert.Single(
            plan.Receipt.Files,
            f => f.RelativePath == nativeWorkerAgentPath);
        Assert.Equal(Digest(nativeWorkerAgentContent), plannedWorkerAgent.Sha256);
        Assert.False(plannedWorkerAgent.Adopted);
        Assert.Equal(target, plannedWorkerAgent.Target);

        SquadOwnedFile plannedReviewerAgent = Assert.Single(
            plan.Receipt.Files,
            f => f.RelativePath == nativeReviewerAgentPath);
        Assert.Equal(Digest(nativeReviewerAgentContent), plannedReviewerAgent.Sha256);
        Assert.False(plannedReviewerAgent.Adopted);
        Assert.Equal(target, plannedReviewerAgent.Target);

        Assert.Equal(3, plan.Receipt.Files.Count);

        // Execute transaction and verify the file system and receipt store outcomes
        TransactionAt(Root).Execute(plan);

        // Unmodified fallback skill was deleted
        Assert.False(Files.FileExists(ToPlatformPath(Root, retiredSkillPath)));

        // Locally edited fallback skill was retained untouched with operator modifications intact
        Assert.True(Files.FileExists(ToPlatformPath(Root, editedSkillPath)));
        Assert.Equal(locallyEditedReviewerContent, Read(Root, editedSkillPath));

        // Native agent files now exist with rendered content
        Assert.True(Files.FileExists(ToPlatformPath(Root, nativeWorkerAgentPath)));
        Assert.Equal(nativeWorkerAgentContent, Read(Root, nativeWorkerAgentPath));
        Assert.True(Files.FileExists(ToPlatformPath(Root, nativeReviewerAgentPath)));
        Assert.Equal(nativeReviewerAgentContent, Read(Root, nativeReviewerAgentPath));

        // Persisted receipt matches planned receipt and omits retired fallback skill
        SquadReceipt persisted = Assert.IsType<SquadReceipt>(StoreAt(Root).ReadReceipt(
            Root,
            SquadDeploymentScope.Project));
        AssertReceiptEqual(plan.Receipt, persisted);
        Assert.DoesNotContain(persisted.Files, f => f.RelativePath == retiredSkillPath);
    }

    /// <summary>
    ///     Prevents a host that deployed <c>create-pull-request-github</c> before it was retired
    ///     into <c>create-pull-request</c> from keeping the retired skill forever after
    ///     <c>squad update</c>, or from losing an operator's edited copy of it. The rendered set is
    ///     the real canonical Claude render, so the test fails while the retired skill is still
    ///     canonical.
    /// </summary>
    [Fact]
    public async Task CreateUpdateRetiresCreatePullRequestGithubFromAPriorReleaseUnlessLocallyEdited()
    {
        const string retiredSkillPath = ".claude/skills/create-pull-request-github/SKILL.md";
        const string installedContent =
            "---\nname: create-pull-request-github\ndescription: Prior release\n---\nPrior release guidance\n";
        const string editedContent =
            "---\nname: create-pull-request-github\ndescription: Operator edited\n---\nOperator guidance\n";
        SquadRenderResult render = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Claude);
        Assert.True(render.Success, string.Join("; ", render.Errors));
        SquadReceipt previousReceipt = Receipt(new SquadOwnedFile(
            retiredSkillPath,
            Digest(installedContent),
            "claude",
            false));

        {
            DeploymentScope fixture = NewScope();
            Write(fixture.Path, retiredSkillPath, installedContent);
            WriteState(fixture.Path, Lock(), previousReceipt);

            SquadDeploymentPlan plan = Planner.CreateUpdate(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                render.Files,
                previousReceipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));

            Assert.Contains(
                plan.PlannedFileChanges,
                change => change.RelativePath == retiredSkillPath &&
                          change.Kind == SquadFileMutationKind.Delete);
            TransactionAt(fixture.Path).Execute(plan);

            Assert.False(Files.FileExists(ToPlatformPath(fixture.Path, retiredSkillPath)));
            SquadReceipt persisted = Assert.IsType<SquadReceipt>(StoreAt(fixture.Path).ReadReceipt(
                fixture.Path,
                SquadDeploymentScope.Project));
            Assert.DoesNotContain(persisted.Files, file => file.RelativePath == retiredSkillPath);
        }

        foreach (bool replaceManaged in new[] { false, true })
        {
            DeploymentScope fixture = NewScope();
            Write(fixture.Path, retiredSkillPath, editedContent);
            WriteState(fixture.Path, Lock(), previousReceipt);

            SquadDeploymentPlan plan = Planner.CreateUpdate(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                render.Files,
                previousReceipt,
                [],
                replaceManaged,
                new FixedTimeProvider(InstalledAt.AddDays(1)));

            Assert.DoesNotContain(plan.PlannedFileChanges, change => change.RelativePath == retiredSkillPath);
            TransactionAt(fixture.Path).Execute(plan);

            Assert.Equal(editedContent, Read(fixture.Path, retiredSkillPath));
            SquadReceipt persisted = Assert.IsType<SquadReceipt>(StoreAt(fixture.Path).ReadReceipt(
                fixture.Path,
                SquadDeploymentScope.Project));
            Assert.Contains(persisted.Files, file => file.RelativePath == retiredSkillPath);
        }
    }

    /// <summary>
    ///     A failed resource update must restore the exact prior bytes and ownership receipt, even
    ///     when the failure happens after the new resource after-image has been published.
    /// </summary>
    [Fact]
    public void ResourceUpdateFailureRollsBackPriorBytesAndReceipt()
    {
        const string resourcePath = ".codex/agents/conductor/references/plan-path.md";
        const string installedContent = "installed plan reference\n";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            resourcePath,
            Digest(installedContent),
            "codex",
            false));
        Write(Root, resourcePath, installedContent);
        WriteState(Root, Lock(), receipt);
        SquadDeploymentPlan plan = Planner.CreateUpdate(
            Root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(resourcePath, "updated plan reference\n")],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        LifecycleStepFailingObserver failure = new(
            Files,
            Root,
            SquadTransactionStepKind.FileApplied);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            TransactionAt(Root, failure).Execute(plan));

        Assert.Equal(installedContent, Read(Root, resourcePath));
        SquadReceipt restored = Assert.IsType<SquadReceipt>(StoreAt(Root).ReadReceipt(
            Root,
            SquadDeploymentScope.Project));
        AssertReceiptEqual(receipt, restored);
    }

    [Fact]
    public void CreateUpdateLocallyEditedManagedFileWithReplaceReplacesOnlyReceiptOwnedPath()
    {
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false));
        Write(Root, ".codex/agents/conductor.toml", "operator edit");
        SquadDeploymentPlan plan = Planner.CreateUpdate(
            Root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated body")],
            oldReceipt,
            [],
            true,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        TransactionAt(Root).Execute(plan);

        Assert.Equal(
            "updated body",
            Read(Root, ".codex/agents/conductor.toml"));
        SquadOwnedFile replaced = Assert.Single(plan.Receipt.Files);
        Assert.Equal(Digest("updated body"), replaced.Sha256);
    }

    [Fact]
    public void ExecuteUpdateFileChangedAfterExactHashPreflightRefusesToOverwriteRacedEdit()
    {
        const string relativePath = ".codex/agents/conductor.toml";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            relativePath,
            Digest("installed body"),
            "codex",
            false));
        Write(Root, relativePath, "installed body");
        WriteState(Root, Lock(), receipt);
        SquadDeploymentPlan plan = Planner.CreateUpdate(
            Root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(relativePath, "updated body")],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        Write(Root, relativePath, "raced operator edit");
        SortedDictionary<string, TreeEntry> before = CaptureTree(Root);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            TransactionAt(Root).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced operator edit", Read(Root, relativePath));
        AssertTreesEqual(before, CaptureTree(Root));
    }

    [Fact]
    public void CreateUpdateReplaceManagedDoesNotAuthorizeUnmanagedCollision()
    {
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false));
        Write(Root, ".codex/agents/conductor.toml", "installed body");
        Write(Root, ".codex/agents/reviewer.toml", "unmanaged file");
        SortedDictionary<string, byte[]> before = Snapshot(Root);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Planner.CreateUpdate(
                Root,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [
                    Rendered(".codex/agents/conductor.toml", "updated body"),
                    Rendered(".codex/agents/reviewer.toml", "generated body")
                ],
                oldReceipt,
                [],
                true,
                new FixedTimeProvider(InstalledAt.AddDays(1))));

        Assert.Contains(
            ".codex/agents/reviewer.toml",
            exception.Message,
            StringComparison.Ordinal);
        AssertSnapshotsEqual(before, Snapshot(Root));
    }

    /// <summary>Refuses every read of one path, as a file whose permissions deny the read does.</summary>
    private void FailReadsOf(string path) =>
        Files.FailWhen(
            operation => string.Equals(operation.Path, path, StringComparison.Ordinal) &&
                         operation.Name.StartsWith("Read", StringComparison.Ordinal),
            new UnauthorizedAccessException("Access to the path is denied."));
}

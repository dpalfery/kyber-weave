using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Deployment-state contracts: receipt layout and state resolution. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateLayoutTests : InMemoryDeploymentStateTestBase
{
    /// <summary>
    ///     Global state lives beneath the injected home at <c>.kyber-weave/squad/global</c>, not beneath
    ///     the platform application data directory, and the receipt names the home as its anchor.
    /// </summary>
    [Fact]
    public void ResolveStateGlobalScopeLivesUnderHomeAnchoredSquadState()
    {
        string applicationData = Path.Combine(Root, "application-data");
        string home = Path.Combine(applicationData, "home");
        Files.CreateDirectory(home);
        SquadStateStore store = StoreAt(applicationData);
        SquadDeploymentPlan plan = Planner.CreateInstall(
            Root,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "body")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        string physicalHome = SquadPhysicalRootIdentities.Resolve(home, Semantics).PhysicalPath;

        // The planner cannot know the home anchor, so it records the unanchored marker. The transaction
        // stamps the physical home on the plan as it writes, which is what this test asserts afterwards.
        Assert.Equal(SquadStateSchemas.UnanchoredTargetRoot, plan.Receipt.TargetRoot);
        TransactionAt(applicationData).Execute(plan);

        string stateDirectory = store.ResolveStateDirectory(
            Root,
            SquadDeploymentScope.Global);

        AssertSameLocation(
            Path.Combine(physicalHome, ".kyber-weave", "squad", "global"),
            stateDirectory);
        Assert.Equal(InstalledAt, plan.Receipt.InstalledAtUtc);
        Assert.Equal(physicalHome, plan.Receipt.TargetRoot);
        string receiptJson = store.SerializeReceipt(plan.Receipt);
        Assert.Contains("\"scope\": \"global\"", receiptJson, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Global state is one machine-wide record anchored on the home, so two unrelated target roots
    ///     address the same lock and receipt rather than each keeping a private binding and a sibling
    ///     receipt. The sibling-ownership check that used to read those siblings is retired with it.
    /// </summary>
    [Fact]
    public void GlobalStateForUnrelatedTargetRootsSharesOneHomeAnchoredReceipt()
    {
        string applicationData = Path.Combine(Root, "application-data");
        string home = Path.Combine(applicationData, "home");
        Files.CreateDirectory(home);
        string firstRoot = NewDirectory(Path.Combine(Root, "customer-alpha"));
        string secondRoot = NewDirectory(Path.Combine(Root, "customer-beta"));
        SquadStateStore store = StoreAt(applicationData);
        // Bare paths, not dot-prefixed: a fresh CreateInstall always renders through the modern
        // per-target layout (NormalizeRenderedFiles never treats a new render as legacy), so a
        // dot-prefixed path here would only manufacture a self-contradicting receipt of this
        // test's own making against the read-side layout cross-check (#91), not exercise
        // anything CreateInstall itself does.
        SquadDeploymentPlan firstPlan = Planner.CreateInstall(
            firstRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered("agents/first.toml", "first")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));

        TransactionAt(applicationData).Execute(firstPlan);

        SquadReceipt firstReceipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(
            firstRoot,
            SquadDeploymentScope.Global));
        SquadReceipt sharedReceipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(
            secondRoot,
            SquadDeploymentScope.Global));
        Assert.Equal("agents/first.toml", Assert.Single(firstReceipt.Files).RelativePath);
        Assert.Equal(firstReceipt.InstalledAtUtc, sharedReceipt.InstalledAtUtc);
        Assert.Equal("agents/first.toml", Assert.Single(sharedReceipt.Files).RelativePath);

        string globalState = Path.Combine(
            SquadPhysicalRootIdentities.Resolve(home, Semantics).PhysicalPath,
            ".kyber-weave",
            "squad",
            "global");
        AssertSameLocation(globalState, store.ResolveStateDirectory(firstRoot, SquadDeploymentScope.Global));
        AssertSameLocation(globalState, store.ResolveStateDirectory(secondRoot, SquadDeploymentScope.Global));
        Assert.False(
            Files.DirectoryExists(Path.Combine(Path.GetDirectoryName(globalState)!, "roots")),
            "A per-root binding directory must not exist once global state is home-anchored.");
        string[] statePaths = CaptureTree(Files, globalState).Keys.ToArray();
        Assert.NotEmpty(statePaths);
        Assert.All(statePaths, relativePath =>
        {
            Assert.DoesNotContain("customer-alpha", relativePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customer-beta", relativePath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(firstRoot, StoreAt(applicationData).SerializeReceipt(firstReceipt),
            StringComparison.Ordinal);
        Assert.DoesNotContain(secondRoot, StoreAt(applicationData).SerializeReceipt(sharedReceipt),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverFailedFirstGlobalInstallRestoresApplicationDataTopology(
        bool preserveSharedApplicationData)
    {
        int probeStepCount = ProbeGlobalInstallStepCount();

        for (int failureSequence = 1; failureSequence <= probeStepCount; failureSequence++)
        {
            DeploymentScope workspace = NewScope();
            string targetRoot = NewDirectory(Path.Combine(workspace.Path, "target"));
            string applicationData = Path.Combine(workspace.Path, "application-data");
            Files.CreateDirectory(Path.Combine(applicationData, "home"));
            if (preserveSharedApplicationData)
            {
                Write(applicationData, "home/.kyber-weave/shared.keep", "shared home store");
                Write(applicationData, "home/.kyber-weave/squad/shared.keep", "shared squad");
            }

            SquadDeploymentPlan plan = CreateGlobalInstallPlan(targetRoot);
            SortedDictionary<string, TreeEntry> before = CaptureTree(workspace.Path);
            FailingObserver failure = new(Files, workspace.Path, failureSequence);
            SquadTransaction transaction = TransactionAt(applicationData, failure);

            Assert.Throws<InjectedSquadTransactionFailure>(() => transaction.Execute(plan));
            AssertTreesEqual(before, CaptureTree(workspace.Path));

            RestoreTree(
                workspace.Path,
                Assert.IsType<SortedDictionary<string, TreeEntry>>(
                    failure.InterruptedTreeSnapshot));
            transaction.Recover(targetRoot, SquadDeploymentScope.Global);
            SortedDictionary<string, TreeEntry> recovered = CaptureTree(workspace.Path);
            transaction.Recover(targetRoot, SquadDeploymentScope.Global);

            AssertTreesEqual(before, recovered);
            AssertTreesEqual(recovered, CaptureTree(workspace.Path));
        }
    }

    /// <summary>
    ///     Global state no longer keys on the target root at all. Lexical, symlink and unrelated roots
    ///     all resolve to the one home-anchored lock, receipt and journal, so the alias and binding
    ///     machinery that used to keep them apart has nothing left to decide.
    /// </summary>
    [Fact]
    public void ResolveGlobalStateIgnoresTargetRootAndAnchorsOnHomeForEveryAlias()
    {
        string targetRoot = NewDirectory(Path.Combine(Root, "target"));
        string aliasRoot = Path.Combine(Root, "target-alias");
        string unrelatedRoot = NewDirectory(Path.Combine(Root, "unrelated"));
        string applicationData = Path.Combine(Root, "application-data");
        string home = NewDirectory(Path.Combine(applicationData, "home"));
        Files.CreateDirectoryLink(
            aliasRoot,
            Path.GetRelativePath(Path.GetDirectoryName(aliasRoot)!, targetRoot));
        SquadStateStore store = StoreAt(applicationData);
        string globalState = Path.Combine(
            SquadPhysicalRootIdentities.Resolve(home, Semantics).PhysicalPath,
            ".kyber-weave",
            "squad",
            "global");

        AssertSameLocation(
            Path.Combine(globalState, "squad.lock.yml"),
            store.ResolveLockPath(targetRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            store.ResolveLockPath(targetRoot, SquadDeploymentScope.Global),
            store.ResolveLockPath(aliasRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global),
            store.ResolveTransactionDirectory(aliasRoot, SquadDeploymentScope.Global));
        AssertSameLocation(
            Path.Combine(globalState, "squad.receipt.json"),
            store.ResolveReceiptPath(unrelatedRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(Path.Combine(targetRoot, "."), SquadDeploymentScope.Global));
    }

    /// <summary>
    ///     On a case-insensitive filesystem the two spellings name one directory, so they must resolve to
    ///     the same home-anchored receipt. The real-volume sibling of this case runs in the integration
    ///     project; here the fake's own name rule supplies the behaviour on every host.
    /// </summary>
    [Fact]
    public void ResolveGlobalStateCaseAliasesShareHomeAnchoredReceiptOnCaseInsensitiveFileSystemInMemory()
    {
        InMemorySquadState state = NewState(caseSensitive: false);
        string targetRoot = Path.Combine(state.Files.Root, "CaseSensitiveProbe");
        string caseAlias = Path.Combine(state.Files.Root, "casesensitiveprobe");
        state.Files.CreateDirectory(targetRoot);
        Assert.True(state.Files.DirectoryExists(caseAlias));
        string expected = Path.Combine(
            SquadPhysicalRootIdentities.Resolve(state.UserPaths.HomeDirectory, state.Semantics).PhysicalPath,
            ".kyber-weave",
            "squad",
            "global",
            "squad.receipt.json");

        Assert.Equal(
            state.Semantics.Canonicalize(expected),
            state.Semantics.Canonicalize(state.Store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global)));
        Assert.Equal(
            state.Semantics.Canonicalize(expected),
            state.Semantics.Canonicalize(state.Store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global)));
    }

    /// <summary>
    ///     Case-distinct sibling roots on a case-sensitive filesystem keep separate physical identities,
    ///     transactions and leases, so two deployments of them proceed independently. The real-volume
    ///     sibling of this case runs in the integration project.
    /// </summary>
    [Fact]
    public void ResolvePhysicalRootCaseDistinctSiblingsRemainDistinctOnCaseSensitiveFileSystemInMemory()
    {
        string upperRoot = NewDirectory(Path.Combine(Root, "Root"));
        string lowerRoot = NewDirectory(Path.Combine(Root, "root"));
        string symlinkRoot = Path.Combine(Root, "root-link");
        Files.CreateDirectoryLink(
            symlinkRoot,
            Path.GetRelativePath(Path.GetDirectoryName(symlinkRoot)!, upperRoot));
        SquadPhysicalRootIdentity upperIdentity =
            SquadPhysicalRootIdentities.Resolve(upperRoot, Semantics);
        SquadPhysicalRootIdentity lowerIdentity =
            SquadPhysicalRootIdentities.Resolve(lowerRoot, Semantics);
        SquadPhysicalRootIdentity symlinkIdentity =
            SquadPhysicalRootIdentities.Resolve(symlinkRoot, Semantics);

        Assert.NotEqual(upperIdentity.PhysicalPath, lowerIdentity.PhysicalPath);
        Assert.NotEqual(upperIdentity.Key, lowerIdentity.Key);
        Assert.NotEqual(
            State.Store.ResolveTransactionDirectory(upperRoot, SquadDeploymentScope.Project),
            State.Store.ResolveTransactionDirectory(lowerRoot, SquadDeploymentScope.Project));
        Assert.Equal(upperIdentity, symlinkIdentity);
        Assert.NotEqual(
            State.Store.ResolveLeaseIdentity(upperRoot, SquadDeploymentScope.Project),
            State.Store.ResolveLeaseIdentity(lowerRoot, SquadDeploymentScope.Project));

        SquadDeploymentPlan upperPlan = Planner.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/upper.toml", "upper")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadDeploymentPlan lowerPlan = Planner.CreateInstall(
            lowerRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/lower.toml", "lower")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));

        // Holding the first root's lease refuses the alias of it and nothing else, so the second
        // deployment proves the sibling's lease is a different name without blocking a thread.
        State.Transaction().Execute(upperPlan);
        using IDisposable held = new InProcessLease(State.LeaseHost).Hold(SquadLeaseNames.ForRoot(
            State.Store.ResolveLeaseIdentity(upperRoot, SquadDeploymentScope.Project)));
        State.Transaction().Execute(lowerPlan);
        SquadDeploymentPlan aliasPlan = Planner.CreateInstall(
            symlinkRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".warp/roles/alias.md", "alias", "warp")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        AssertActiveLeaseConflict(Record.Exception(() => State.Transaction().Execute(aliasPlan)));

        Assert.Equal("upper", Read(upperRoot, ".codex/agents/upper.toml"));
        Assert.Equal("lower", Read(lowerRoot, ".codex/agents/lower.toml"));
    }

    /// <summary>
    ///     A case alias of a root on a case-insensitive filesystem is one physical root, so it shares the
    ///     receipt and the lease, and a second deployment of it is refused while the first runs. The
    ///     real-volume sibling of this case runs in the integration project.
    /// </summary>
    [Fact]
    public void ResolvePhysicalRootCaseAliasesConvergeOnCaseInsensitiveFileSystemInMemory()
    {
        InMemorySquadState state = NewState(caseSensitive: false);
        string actualRoot = Path.Combine(state.Files.Root, "CaseProbe");
        string caseAlias = Path.Combine(state.Files.Root, "caseprobe");
        string symlinkAlias = Path.Combine(state.Files.Root, "case-probe-link");
        state.Files.CreateDirectory(actualRoot);
        state.Files.CreateDirectoryLink(
            symlinkAlias,
            Path.GetRelativePath(state.Files.Root, actualRoot));
        SquadPhysicalRootIdentity actualIdentity =
            SquadPhysicalRootIdentities.Resolve(actualRoot, state.Semantics);
        SquadPhysicalRootIdentity aliasIdentity =
            SquadPhysicalRootIdentities.Resolve(caseAlias, state.Semantics);

        Assert.Equal(actualIdentity, SquadPhysicalRootIdentities.Resolve(symlinkAlias, state.Semantics));
        Assert.Equal(actualIdentity.PhysicalPath, aliasIdentity.PhysicalPath);
        Assert.Equal(actualIdentity.Key, aliasIdentity.Key);
        Assert.Equal(
            state.Store.ResolveReceiptPath(actualRoot, SquadDeploymentScope.Global),
            state.Store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global));
        Assert.Equal(
            state.Store.ResolveLeaseIdentity(actualRoot, SquadDeploymentScope.Project),
            state.Store.ResolveLeaseIdentity(caseAlias, SquadDeploymentScope.Project));

        SquadDeploymentPlan aliasPlan = state.Planner.CreateInstall(
            caseAlias,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".warp/roles/alias.md", "alias", "warp")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        using IDisposable held = new InProcessLease(state.LeaseHost).Hold(SquadLeaseNames.ForRoot(
            state.Store.ResolveLeaseIdentity(actualRoot, SquadDeploymentScope.Project)));

        AssertActiveLeaseConflict(Record.Exception(() => state.Transaction().Execute(aliasPlan)));
    }

    [Fact]
    public void ExecuteRetargetedRootAliasFailsBeforeWrites()
    {
        DeploymentScope workspace = NewScope();
        string firstRoot = NewDirectory(Path.Combine(workspace.Path, "first"));
        string secondRoot = NewDirectory(Path.Combine(workspace.Path, "second"));
        string aliasRoot = Path.Combine(workspace.Path, "root-alias");
        Files.CreateDirectoryLink(
            aliasRoot,
            Path.GetRelativePath(Path.GetDirectoryName(aliasRoot)!, firstRoot));
        SquadDeploymentPlan plan = Planner.CreateInstall(
            aliasRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "generated")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Files.DeleteTreeNoFollow(aliasRoot);
        Files.CreateDirectoryLink(
            aliasRoot,
            Path.GetRelativePath(Path.GetDirectoryName(aliasRoot)!, secondRoot));
        SortedDictionary<string, TreeEntry> before = CaptureTree(workspace.Path);

        Exception? exception = Record.Exception(() =>
            TransactionAt(Path.Combine(workspace.Path, "application-data")).Execute(plan));

        SquadDeploymentConflictException
            conflict = Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.Contains("root", conflict.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(before, CaptureTree(workspace.Path));
    }

    [Fact]
    public void SerializeReceiptLayout_GlobalInstall_WritesV2WithPerTargetRootsLayout()
    {
        SquadStateStore store = StoreAt(Root);
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(".codex/agents/conductor.toml", Digest("conductor"), "codex", false)
            ]);

        string json = store.SerializeReceipt(receipt);

        Assert.Contains("\"schema\": \"kyber-squad.receipt/v2\"", json, StringComparison.Ordinal);
        Assert.Contains("\"layout\": \"per-target-roots\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeReceiptLayout_ProjectInstall_StaysV1WithoutLayout()
    {
        SquadStateStore store = StoreAt(Root);
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(".codex/agents/conductor.toml", Digest("conductor"), "codex", false)
            ]);

        string json = store.SerializeReceipt(receipt);

        Assert.Contains("\"schema\": \"kyber-squad.receipt/v1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"layout\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scope\": \"project\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeReceiptLayout_V2GlobalReceiptRoundTrips()
    {
        SquadStateStore store = StoreAt(Root);
        string validV2Json = """
                             {
                               "schema": "kyber-squad.receipt/v2",
                               "scope": "global",
                               "layout": "per-target-roots",
                               "targetRoot": ".",
                               "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                               "degradations": [],
                               "files": []
                             }
                             """;

        SquadReceipt receipt = store.DeserializeReceipt(validV2Json);

        Assert.Equal("kyber-squad.receipt/v2", receipt.Schema);
    }

    [Fact]
    public void DeserializeReceiptLayout_V2ProjectReceipt_RejectedWithInvalidDataException()
    {
        SquadStateStore store = StoreAt(Root);
        string invalidV2Json = """
                               {
                                 "schema": "kyber-squad.receipt/v2",
                                 "scope": "project",
                                 "layout": "per-target-roots",
                                 "targetRoot": ".",
                                 "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                                 "degradations": [],
                                 "files": []
                               }
                               """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(invalidV2Json));

        Assert.Contains("v2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeReceiptLayout_V2MissingLayout_RejectedWithInvalidDataException()
    {
        SquadStateStore store = StoreAt(Root);
        string invalidJson = """
                             {
                               "schema": "kyber-squad.receipt/v2",
                               "targetRoot": ".",
                               "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                               "degradations": [],
                               "files": []
                             }
                             """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(invalidJson));

        Assert.Contains("layout", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeReceiptLayout_V2UnknownLayout_RejectedWithInvalidDataException()
    {
        SquadStateStore store = StoreAt(Root);
        string invalidJson = """
                             {
                               "schema": "kyber-squad.receipt/v2",
                               "layout": "unknown-layout",
                               "targetRoot": ".",
                               "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                               "degradations": [],
                               "files": []
                             }
                             """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(invalidJson));

        Assert.Contains("layout", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown-layout", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("per-target-roots", ".codex/agents/conductor.toml")]
    [InlineData("single-root", "agents/conductor.toml")]
    public void DeserializeReceiptLayout_V2LayoutContradictsPaths_RejectedWithInvalidDataException(
        string declaredLayout,
        string relativePath)
    {
        SquadStateStore store = StoreAt(Root);
        string contradictingJson = $$"""
                                     {
                                       "schema": "kyber-squad.receipt/v2",
                                       "layout": "{{declaredLayout}}",
                                       "targetRoot": ".",
                                       "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                                       "degradations": [],
                                       "files": [
                                         {
                                           "relativePath": "{{relativePath}}",
                                           "sha256": "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd",
                                           "target": "codex",
                                           "adopted": false
                                         }
                                       ]
                                     }
                                     """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(contradictingJson));

        Assert.Contains("layout", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(declaredLayout, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeReceiptLayout_V1LegacyMixedPrefixedAndBarePaths_RejectedWithInvalidDataException()
    {
        SquadStateStore store = StoreAt(Root);
        string mixedJson = """
                           {
                             "schema": "kyber-squad.receipt/v1",
                             "scope": "global",
                             "targetRoot": ".",
                             "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                             "degradations": [],
                             "files": [
                               {
                                 "relativePath": ".codex/agents/conductor.toml",
                                 "sha256": "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd",
                                 "target": "codex",
                                 "adopted": false
                               },
                               {
                                 "relativePath": "agents/copilot.md",
                                 "sha256": "def456def456def456def456def456def456def456def456def456def456deef",
                                 "target": "copilot",
                                 "adopted": false
                               }
                             ]
                           }
                           """;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(mixedJson));

        Assert.Contains("mix", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeReceiptLayout_V1GlobalWithBarePaths_AcceptedAsPerTargetLayout()
    {
        SquadStateStore store = StoreAt(Root);
        string barePathsJson = """
                               {
                                 "schema": "kyber-squad.receipt/v1",
                                 "scope": "global",
                                 "targetRoot": ".",
                                 "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                                 "degradations": [],
                                 "files": [
                                   {
                                     "relativePath": "agents/conductor.toml",
                                     "sha256": "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd",
                                     "target": "codex",
                                     "adopted": false
                                   }
                                 ]
                               }
                               """;

        SquadReceipt receipt = store.DeserializeReceipt(barePathsJson);

        Assert.Equal("kyber-squad.receipt/v1", receipt.Schema);
        Assert.Single(receipt.Files);
        Assert.Equal("agents/conductor.toml", receipt.Files[0].RelativePath);

        string reserialized = store.SerializeReceipt(receipt);
        Assert.Contains("\"layout\": \"per-target-roots\"", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeReceiptLayout_V1GlobalWithPrefixedPaths_AcceptedAsSingleRootLayout()
    {
        SquadStateStore store = StoreAt(Root);
        string prefixedPathsJson = """
                                   {
                                     "schema": "kyber-squad.receipt/v1",
                                     "scope": "global",
                                     "targetRoot": ".",
                                     "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                                     "degradations": [],
                                     "files": [
                                       {
                                         "relativePath": ".codex/agents/conductor.toml",
                                         "sha256": "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd",
                                         "target": "codex",
                                         "adopted": false
                                       },
                                       {
                                         "relativePath": ".github/agents/reviewer.agent.md",
                                         "sha256": "def456def456def456def456def456def456def456def456def456def456deef",
                                         "target": "copilot",
                                         "adopted": false
                                       }
                                     ]
                                   }
                                   """;

        SquadReceipt receipt = store.DeserializeReceipt(prefixedPathsJson);

        Assert.Equal("kyber-squad.receipt/v1", receipt.Schema);
        Assert.Equal(2, receipt.Files.Count);

        string reserialized = store.SerializeReceipt(receipt);
        Assert.Contains("\"layout\": \"single-root\"", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallReceiptLayout_LegacySingleRootReceipt_DeletesCleanFilesUnderRecordedRoot()
    {
        DeploymentScope workspace = NewScope();
        string recordedRoot = NewDirectory(Path.Combine(workspace.Path, "legacy-install"));
        string applicationData = Path.Combine(workspace.Path, "app-data");
        string fakeCodexRoot = NewDirectory(Path.Combine(workspace.Path, "fake-codex"));
        string fakeCopilotRoot = NewDirectory(Path.Combine(workspace.Path, "fake-copilot"));

        Write(recordedRoot, ".codex/agents/conductor.toml", "conductor clean");
        Write(recordedRoot, ".github/agents/reviewer.agent.md", "copilot clean");
        Write(recordedRoot, ".codex/agents/edited.toml", "edited content");

        SquadReceipt legacyReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor clean"),
                    "codex",
                    false),
                new SquadOwnedFile(
                    ".github/agents/reviewer.agent.md",
                    Digest("copilot clean"),
                    "copilot",
                    false),
                new SquadOwnedFile(
                    ".codex/agents/edited.toml",
                    Digest("original installed content"),
                    "codex",
                    false)
            ]);

        FakeSquadGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = fakeCodexRoot,
            [SquadTarget.Copilot] = fakeCopilotRoot
        });

        SquadDeploymentPlan uninstallPlan = Planner.CreateUninstall(
            recordedRoot,
            SquadDeploymentScope.Global,
            legacyReceipt,
            globalRoots);

        TransactionAt(applicationData).Execute(uninstallPlan);

        Assert.False(Files.FileExists(ToPlatformPath(recordedRoot, ".codex/agents/conductor.toml")));
        Assert.False(Files.FileExists(ToPlatformPath(recordedRoot, ".github/agents/reviewer.agent.md")));
        Assert.True(Files.FileExists(ToPlatformPath(recordedRoot, ".codex/agents/edited.toml")));
        Assert.Empty(Files.EnumerateFiles(fakeCodexRoot, "*", recursive: true));
        Assert.Empty(Files.EnumerateFiles(fakeCopilotRoot, "*", recursive: true));

        SquadReceipt? retainedReceipt = StoreAt(applicationData).ReadReceipt(recordedRoot, SquadDeploymentScope.Global);
        Assert.NotNull(retainedReceipt);
        Assert.Single(retainedReceipt.Files);
        Assert.Equal(".codex/agents/edited.toml", retainedReceipt.Files[0].RelativePath);
        Assert.Equal(SquadReceiptLayout.SingleRoot, retainedReceipt.Layout);

        string retainedJson = StoreAt(applicationData).SerializeReceipt(retainedReceipt);
        Assert.Contains("\"schema\": \"kyber-squad.receipt/v2\"", retainedJson, StringComparison.Ordinal);
        Assert.Contains("\"layout\": \"single-root\"", retainedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallReceiptLayout_LegacyReceiptAllClean_DeletesEverythingThenLockAndReceipt()
    {
        DeploymentScope workspace = NewScope();
        string recordedRoot = NewDirectory(Path.Combine(workspace.Path, "legacy-install"));
        string applicationData = Path.Combine(workspace.Path, "app-data");
        string fakeCodexRoot = NewDirectory(Path.Combine(workspace.Path, "fake-codex"));
        string fakeCopilotRoot = NewDirectory(Path.Combine(workspace.Path, "fake-copilot"));

        Write(recordedRoot, ".codex/agents/conductor.toml", "conductor clean");
        Write(recordedRoot, ".github/agents/reviewer.agent.md", "copilot clean");

        SquadReceipt legacyReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor clean"),
                    "codex",
                    false),
                new SquadOwnedFile(
                    ".github/agents/reviewer.agent.md",
                    Digest("copilot clean"),
                    "copilot",
                    false)
            ]);

        SquadStateStore stateStore = StoreAt(applicationData);
        string receiptPath = stateStore.ResolveReceiptPath(recordedRoot, SquadDeploymentScope.Global);
        string lockPath = stateStore.ResolveLockPath(recordedRoot, SquadDeploymentScope.Global);
        Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        Files.WriteAllText(receiptPath, stateStore.SerializeReceipt(legacyReceipt));
        Files.WriteAllText(lockPath, stateStore.SerializeLock(Lock()));

        FakeSquadGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = fakeCodexRoot,
            [SquadTarget.Copilot] = fakeCopilotRoot
        });

        SquadDeploymentPlan uninstallPlan = Planner.CreateUninstall(
            recordedRoot,
            SquadDeploymentScope.Global,
            legacyReceipt,
            globalRoots);

        TransactionAt(applicationData).Execute(uninstallPlan);

        Assert.False(Files.FileExists(ToPlatformPath(recordedRoot, ".codex/agents/conductor.toml")));
        Assert.False(Files.FileExists(ToPlatformPath(recordedRoot, ".github/agents/reviewer.agent.md")));
        Assert.Empty(Files.EnumerateFiles(fakeCodexRoot, "*", recursive: true));
        Assert.Empty(Files.EnumerateFiles(fakeCopilotRoot, "*", recursive: true));

        Assert.False(Files.FileExists(receiptPath));
        Assert.False(Files.FileExists(lockPath));
        Assert.Null(stateStore.ReadReceipt(recordedRoot, SquadDeploymentScope.Global));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateReceiptLayout_LegacySingleRootReceipt_ThrowsSquadDeploymentConflictException(bool dryRun)
    {
        string recordedRoot = NewDirectory(Path.Combine(Root, "legacy-install"));

        Write(recordedRoot, ".codex/agents/conductor.toml", "conductor content");

        SquadReceipt legacyReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor content"),
                    "codex",
                    false)
            ]);

        SquadStateStore stateStore = State.Store;
        string stateDir = stateStore.ResolveStateDirectory(recordedRoot, SquadDeploymentScope.Global);
        string receiptPath = stateStore.ResolveReceiptPath(recordedRoot, SquadDeploymentScope.Global);
        Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        Files.WriteAllText(receiptPath, SerializeAsLegacyV1Json(legacyReceipt));

        SortedDictionary<string, byte[]> receiptSnapshotBefore = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotBefore = Snapshot(recordedRoot);

        using FakeSquadReleaseSource releaseSource = FakeSquadReleaseSource.Failing(new NotImplementedException());
        FakeSquadGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = Path.Combine(Root, "fake-codex")
        });
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(
            State,
            releaseSource,
            FakeSquadRenderer.Empty(),
            globalRoots);

        SquadDeploymentConflictException exception = await Assert.ThrowsAsync<SquadDeploymentConflictException>(() =>
            service.UpdateAsync(new SquadUpdateRequest(
                recordedRoot,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex],
                DryRun: dryRun)));

        Assert.Contains("squad uninstall --global", exception.Message, StringComparison.Ordinal);
        Assert.Contains("squad install --global", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, releaseSource.CallCount);

        SortedDictionary<string, byte[]> receiptSnapshotAfter = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotAfter = Snapshot(recordedRoot);
        AssertSnapshotsEqual(receiptSnapshotBefore, receiptSnapshotAfter);
        AssertSnapshotsEqual(deploymentSnapshotBefore, deploymentSnapshotAfter);
    }

    [Fact]
    public async Task InstallReceiptLayout_ExistingLegacySingleRootReceipt_ThrowsSquadDeploymentConflictException()
    {
        string recordedRoot = NewDirectory(Path.Combine(Root, "legacy-install"));

        Write(recordedRoot, ".codex/agents/conductor.toml", "conductor content");

        SquadReceipt legacyReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor content"),
                    "codex",
                    false)
            ]);

        SquadStateStore stateStore = State.Store;
        string stateDir = stateStore.ResolveStateDirectory(recordedRoot, SquadDeploymentScope.Global);
        string receiptPath = stateStore.ResolveReceiptPath(recordedRoot, SquadDeploymentScope.Global);
        Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        Files.WriteAllText(receiptPath, SerializeAsLegacyV1Json(legacyReceipt));

        SortedDictionary<string, byte[]> receiptSnapshotBefore = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotBefore = Snapshot(recordedRoot);

        using FakeSquadReleaseSource releaseSource = FakeSquadReleaseSource.Failing(new NotImplementedException());
        FakeSquadGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = Path.Combine(Root, "fake-codex")
        });
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(
            State,
            releaseSource,
            FakeSquadRenderer.Empty(),
            globalRoots);

        SquadDeploymentConflictException exception = await Assert.ThrowsAsync<SquadDeploymentConflictException>(() =>
            service.InstallAsync(new SquadInstallRequest(
                recordedRoot,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex])));

        Assert.Contains("squad uninstall --global", exception.Message, StringComparison.Ordinal);
        Assert.Contains("squad install --global", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, releaseSource.CallCount);

        SortedDictionary<string, byte[]> receiptSnapshotAfter = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotAfter = Snapshot(recordedRoot);
        AssertSnapshotsEqual(receiptSnapshotBefore, receiptSnapshotAfter);
        AssertSnapshotsEqual(deploymentSnapshotBefore, deploymentSnapshotAfter);
    }

    [Fact]
    public void UpdateReceiptLayout_V1BarePaths_RoundTripsAsV2PerTargetRoots()
    {
        string targetRoot = Root;
        SquadStateStore store = StoreAt(Root);

        Write(targetRoot, "agents/conductor.toml", "conductor content");

        SquadReceipt barePathsReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            targetRoot,
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    "agents/conductor.toml",
                    Digest("conductor content"),
                    "codex",
                    false)
            ]);

        SquadDeploymentPlan updatePlan = Planner.CreateUpdate(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock("1.2.4"),
            [Rendered("agents/conductor.toml", "updated content")],
            barePathsReceipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        string receiptJson = store.SerializeReceipt(updatePlan.Receipt);

        Assert.Contains("\"schema\": \"kyber-squad.receipt/v2\"", receiptJson, StringComparison.Ordinal);
        Assert.Contains("\"layout\": \"per-target-roots\"", receiptJson, StringComparison.Ordinal);
    }

    [Fact]
    public void
        CreateUpdateReceiptLayout_NullResolverMixedRetainedAndRenderedPaths_ThrowsSquadDeploymentConflictException()
    {
        string targetRoot = Root;

        // The prefixed codex entry is re-rendered under its own old identity and is locally
        // edited, so it is retained as-is (still prefixed) rather than replaced. The claude
        // entry is a brand-new render with a modern bare path. Combined, the update's own
        // output would mix prefixed and bare paths.
        Write(targetRoot, ".codex/agents/conductor.toml", "edited content");

        SquadReceipt previousReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("original content"),
                    "codex",
                    false)
            ]);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Planner.CreateUpdate(
                targetRoot,
                SquadDeploymentScope.Global,
                Lock("1.2.4"),
                [
                    Rendered(".codex/agents/conductor.toml", "updated content"),
                    Rendered("agents/reviewer.md", "reviewer body", "claude")
                ],
                previousReceipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1))));

        Assert.Contains("squad uninstall --global", exception.Message, StringComparison.Ordinal);
        Assert.Contains("squad install --global", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUpdateReceiptLayout_NullResolverAllCleanLegacyReceipt_LayoutMatchesClassification()
    {
        string targetRoot = Root;

        Write(targetRoot, ".codex/agents/conductor.toml", "conductor content");

        SquadReceipt previousReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor content"),
                    "codex",
                    false)
            ]);

        SquadDeploymentPlan plan = Planner.CreateUpdate(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated conductor content")],
            previousReceipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        Assert.Equal(
            SquadDeploymentPlanRules.ClassifyGlobalLayout(plan.Receipt.Files),
            plan.Receipt.Layout);
        Assert.Equal(SquadReceiptLayout.SingleRoot, plan.Receipt.Layout);
    }

    /// <summary>Runs one global install and returns how many durable steps it reported.</summary>
    /// <returns>The step count.</returns>
    private int ProbeGlobalInstallStepCount()
    {
        DeploymentScope probe = NewScope();
        string targetRoot = NewDirectory(Path.Combine(probe.Path, "target"));
        RecordingObserver observer = new(Files, targetRoot);
        TransactionAt(probe.Path, observer).Execute(CreateGlobalInstallPlan(targetRoot));
        return observer.Steps.Count;
    }
}

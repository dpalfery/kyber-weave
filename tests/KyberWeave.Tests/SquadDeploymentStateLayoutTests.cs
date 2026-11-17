using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Deployment-state contracts: receipt layout and state resolution. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateLayoutTests : SquadDeploymentStateTestBase
{
    /// <summary>
    ///     Global state lives beneath the injected home at <c>.kyber-weave/squad/global</c>, not beneath
    ///     the platform application data directory, and the receipt names the home as its anchor.
    /// </summary>
    [Fact]
    public void ResolveStateGlobalScopeLivesUnderHomeAnchoredSquadState()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string home = Path.Combine(applicationData, "home");
        Directory.CreateDirectory(home);
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan plan = SquadDeploymentPlanner.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "body")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        string physicalHome = SquadPhysicalRootIdentity.Resolve(home, SquadFileSystemPathSemantics.Instance).PhysicalPath;

        // The planner cannot know the home anchor, so it records the unanchored marker. The transaction
        // stamps the physical home on the plan as it writes, which is what this test asserts afterwards.
        Assert.Equal(SquadStateStore.UnanchoredTargetRoot, plan.Receipt.TargetRoot);
        new SquadTransaction(store).Execute(plan);

        string stateDirectory = store.ResolveStateDirectory(
            fixture.Path,
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
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string home = Path.Combine(applicationData, "home");
        Directory.CreateDirectory(home);
        string firstRoot = Path.Combine(fixture.Path, "customer-alpha");
        string secondRoot = Path.Combine(fixture.Path, "customer-beta");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        SquadStateStore store = Store(applicationData);
        // Bare paths, not dot-prefixed: a fresh CreateInstall always renders through the modern
        // per-target layout (NormalizeRenderedFiles never treats a new render as legacy), so a
        // dot-prefixed path here would only manufacture a self-contradicting receipt of this
        // test's own making against the read-side layout cross-check (#91), not exercise
        // anything CreateInstall itself does.
        SquadDeploymentPlan firstPlan = SquadDeploymentPlanner.CreateInstall(
            firstRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered("agents/first.toml", "first")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));

        new SquadTransaction(store).Execute(firstPlan);

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
            SquadPhysicalRootIdentity.Resolve(home, SquadFileSystemPathSemantics.Instance).PhysicalPath,
            ".kyber-weave",
            "squad",
            "global");
        AssertSameLocation(globalState, store.ResolveStateDirectory(firstRoot, SquadDeploymentScope.Global));
        AssertSameLocation(globalState, store.ResolveStateDirectory(secondRoot, SquadDeploymentScope.Global));
        Assert.False(
            Directory.Exists(Path.Combine(Path.GetDirectoryName(globalState)!, "roots")),
            "A per-root binding directory must not exist once global state is home-anchored.");
        string[] statePaths = Directory.EnumerateFileSystemEntries(
                globalState,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(globalState, path).Replace(
                Path.DirectorySeparatorChar,
                '/'))
            .ToArray();
        Assert.NotEmpty(statePaths);
        Assert.All(statePaths, relativePath =>
        {
            Assert.DoesNotContain("customer-alpha", relativePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customer-beta", relativePath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(firstRoot, Store(applicationData).SerializeReceipt(firstReceipt),
            StringComparison.Ordinal);
        Assert.DoesNotContain(secondRoot, Store(applicationData).SerializeReceipt(sharedReceipt),
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     A global install across two targets that render the same relative path must deploy to
    ///     each target's own root, and status must verify the bytes there — not against the
    ///     deployment root, which holds none of the files.
    /// </summary>
    [Fact]
    public void ExecuteGlobalInstallSharingPathsAcrossTargetsDeploysAndStatusVerifiesPerTargetRoots()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string claudeRoot = Path.Combine(fixture.Path, "claude-home", ".claude");
        string codexRoot = Path.Combine(fixture.Path, "codex-home", ".codex");
        Directory.CreateDirectory(claudeRoot);
        Directory.CreateDirectory(codexRoot);
        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Claude] = claudeRoot,
            [SquadTarget.Codex] = codexRoot
        });
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan plan = SquadDeploymentPlanner.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Global,
            Lock(),
            [
                Rendered("agents/conductor.md", "claude conductor", "claude"),
                Rendered("agents/conductor.md", "codex conductor")
            ],
            [],
            false,
            new FixedTimeProvider(InstalledAt),
            globalRoots);

        new SquadTransaction(store).Execute(plan);

        Assert.Equal("claude conductor", Read(claudeRoot, "agents/conductor.md"));
        Assert.Equal("codex conductor", Read(codexRoot, "agents/conductor.md"));
        // A Global receipt always serializes on the v2 schema (A3), so what SquadTransaction
        // persisted and re-read differs from the plan's own v1-labeled in-memory receipt only
        // in that one field.
        AssertReceiptEqual(
            plan.Receipt with { Schema = SquadStateStore.ReceiptSchemaV2 },
            Assert.IsType<SquadReceipt>(store.ReadReceipt(fixture.Path, SquadDeploymentScope.Global)));

        SquadStatusCommand command = SquadTestCommands.Status(stateStore: store, globalRoots: globalRoots);
        CapturedConsoleExecution<int> clean = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = fixture.Path,
                Global = true
            }));
        Assert.Equal(0, clean.Result);
        Assert.Contains("(claude)", clean.Output, StringComparison.Ordinal);
        Assert.Contains("(codex)", clean.Output, StringComparison.Ordinal);

        Write(claudeRoot, "agents/conductor.md", "operator edit");
        CapturedConsoleExecution<int> drifted = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = fixture.Path,
                Global = true
            }));

        Assert.Equal(1, drifted.Result);
        Assert.Contains("drift", drifted.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(claude)", drifted.Output, StringComparison.Ordinal);
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
            using TempDirectory fixture = new();
            string targetRoot = Path.Combine(fixture.Path, "target");
            string applicationData = Path.Combine(fixture.Path, "application-data");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(Path.Combine(applicationData, "home"));
            if (preserveSharedApplicationData)
            {
                Write(applicationData, "home/.kyber-weave/shared.keep", "shared home store");
                Write(applicationData, "home/.kyber-weave/squad/shared.keep", "shared squad");
            }

            SquadStateStore store = Store(applicationData);
            SquadDeploymentPlan plan = CreateGlobalInstallPlan(targetRoot);
            SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
            FailingObserver failure = new(fixture.Path, failureSequence);
            SquadTransaction transaction = new(store, failure);

            Assert.Throws<InjectedSquadTransactionFailure>(() => transaction.Execute(plan));
            AssertTreesEqual(before, CaptureTree(fixture.Path));

            RestoreTree(
                fixture.Path,
                Assert.IsType<SortedDictionary<string, TreeEntry>>(
                    failure.InterruptedTreeSnapshot));
            transaction.Recover(targetRoot, SquadDeploymentScope.Global);
            SortedDictionary<string, TreeEntry> recovered = CaptureTree(fixture.Path);
            transaction.Recover(targetRoot, SquadDeploymentScope.Global);

            AssertTreesEqual(before, recovered);
            AssertTreesEqual(recovered, CaptureTree(fixture.Path));
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
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string aliasRoot = Path.Combine(fixture.Path, "target-alias");
        string unrelatedRoot = Path.Combine(fixture.Path, "unrelated");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string home = Path.Combine(applicationData, "home");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(unrelatedRoot);
        Directory.CreateDirectory(home);
        Directory.CreateSymbolicLink(aliasRoot, targetRoot);
        SquadStateStore store = Store(applicationData);
        string globalState = Path.Combine(
            SquadPhysicalRootIdentity.Resolve(home, SquadFileSystemPathSemantics.Instance).PhysicalPath,
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
    ///     the same home-anchored receipt. On a case-sensitive filesystem the probe cannot be built and the
    ///     test returns without asserting; that case is covered by the Windows sibling test.
    /// </summary>
    [Fact]
    public void ResolveGlobalStateCaseAliasesShareHomeAnchoredReceiptOnCaseInsensitiveFilesystem()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "CaseSensitiveProbe");
        string caseAlias = Path.Combine(fixture.Path, "casesensitiveprobe");
        Directory.CreateDirectory(targetRoot);
        if (!Directory.Exists(caseAlias))
            return;

        string applicationData = Path.Combine(fixture.Path, "application-data");
        string home = Path.Combine(applicationData, "home");
        Directory.CreateDirectory(home);
        SquadStateStore store = Store(applicationData);
        string expected = Path.Combine(
            SquadPhysicalRootIdentity.Resolve(home, SquadFileSystemPathSemantics.Instance).PhysicalPath,
            ".kyber-weave",
            "squad",
            "global",
            "squad.receipt.json");

        AssertSameLocation(expected, store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global));
        AssertSameLocation(expected, store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global));
    }

    [Fact]
    public async Task ResolvePhysicalRootCaseDistinctSiblingsRemainDistinctWhenSupported()
    {
        using TempDirectory fixture = new();
        string upperRoot = Path.Combine(fixture.Path, "Root");
        string lowerRoot = Path.Combine(fixture.Path, "root");
        string symlinkRoot = Path.Combine(fixture.Path, "root-link");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(upperRoot);
        if (Directory.Exists(lowerRoot))
        {
            Assert.Equal(
                SquadPhysicalRootIdentity.Resolve(upperRoot, SquadFileSystemPathSemantics.Instance),
                SquadPhysicalRootIdentity.Resolve(lowerRoot, SquadFileSystemPathSemantics.Instance));
            return;
        }

        Directory.CreateDirectory(lowerRoot);
        Directory.CreateSymbolicLink(symlinkRoot, upperRoot);
        SquadPhysicalRootIdentity upperIdentity = SquadPhysicalRootIdentity.Resolve(upperRoot, SquadFileSystemPathSemantics.Instance);
        SquadPhysicalRootIdentity lowerIdentity = SquadPhysicalRootIdentity.Resolve(lowerRoot, SquadFileSystemPathSemantics.Instance);
        SquadPhysicalRootIdentity symlinkIdentity = SquadPhysicalRootIdentity.Resolve(symlinkRoot, SquadFileSystemPathSemantics.Instance);
        SquadStateStore store = Store(applicationData);

        Assert.NotEqual(upperIdentity.PhysicalPath, lowerIdentity.PhysicalPath);
        Assert.NotEqual(upperIdentity.Key, lowerIdentity.Key);
        Assert.NotEqual(
            store.ResolveTransactionDirectory(upperRoot, SquadDeploymentScope.Project),
            store.ResolveTransactionDirectory(lowerRoot, SquadDeploymentScope.Project));
        Assert.Equal(upperIdentity, symlinkIdentity);

        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadDeploymentPlan activePlan = SquadDeploymentPlanner.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/upper.toml", "upper")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadDeploymentPlan siblingPlan = SquadDeploymentPlanner.CreateInstall(
            lowerRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/lower.toml", "lower")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadTransaction activeTransaction = new(store, blocker);
        Task activeExecution = Task.Run(() => activeTransaction.Execute(activePlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The first case-distinct root did not acquire its physical-root lease.");

        Exception? siblingException;
        try
        {
            SquadTransaction siblingTransaction = new(store);
            siblingException = Record.Exception(() => siblingTransaction.Execute(siblingPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        Assert.Null(siblingException);
        Assert.Equal("upper", Read(upperRoot, ".codex/agents/upper.toml"));
        Assert.Equal("lower", Read(lowerRoot, ".codex/agents/lower.toml"));
    }

    [Fact]
    public async Task ResolvePhysicalRootCaseAliasesConvergeWhenFilesystemIsInsensitive()
    {
        using TempDirectory fixture = new();
        string actualRoot = Path.Combine(fixture.Path, "CaseProbe");
        string caseAlias = Path.Combine(fixture.Path, "caseprobe");
        string symlinkAlias = Path.Combine(fixture.Path, "case-probe-link");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(actualRoot);
        Directory.CreateSymbolicLink(symlinkAlias, actualRoot);
        SquadPhysicalRootIdentity actualIdentity = SquadPhysicalRootIdentity.Resolve(actualRoot, SquadFileSystemPathSemantics.Instance);

        Assert.Equal(actualIdentity, SquadPhysicalRootIdentity.Resolve(symlinkAlias, SquadFileSystemPathSemantics.Instance));
        if (!Directory.Exists(caseAlias))
        {
            Assert.NotEqual(
                Path.GetFullPath(actualRoot),
                Path.GetFullPath(caseAlias));
            return;
        }

        SquadPhysicalRootIdentity aliasIdentity = SquadPhysicalRootIdentity.Resolve(caseAlias, SquadFileSystemPathSemantics.Instance);
        SquadStateStore store = Store(applicationData);
        Assert.Equal(actualIdentity.PhysicalPath, aliasIdentity.PhysicalPath);
        Assert.Equal(actualIdentity.Key, aliasIdentity.Key);
        Assert.Equal(
            store.ResolveReceiptPath(actualRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global));

        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadDeploymentPlan activePlan = SquadDeploymentPlanner.CreateInstall(
            actualRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/actual.toml", "actual")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        // Project scope, not global: the lease of a project is its physical root, so a case alias of that
        // root contends. Global state is leased on the home, which this root is not, so a global alias
        // would correctly not contend with a project at an unrelated root.
        SquadDeploymentPlan aliasPlan = SquadDeploymentPlanner.CreateInstall(
            caseAlias,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".warp/roles/alias.md", "alias", "warp")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadTransaction activeTransaction = new(store, blocker);
        Task activeExecution = Task.Run(() => activeTransaction.Execute(activePlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The physical-root alias did not acquire its shared lease.");

        Exception? aliasException;
        try
        {
            SquadTransaction aliasTransaction = new(store);
            aliasException = Record.Exception(() => aliasTransaction.Execute(aliasPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        AssertActiveLeaseConflict(aliasException);
    }

    [Fact]
    public void ExecuteRetargetedRootAliasFailsBeforeWrites()
    {
        using TempDirectory fixture = new();
        string firstRoot = Path.Combine(fixture.Path, "first");
        string secondRoot = Path.Combine(fixture.Path, "second");
        string aliasRoot = Path.Combine(fixture.Path, "root-alias");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateSymbolicLink(aliasRoot, firstRoot);
        SquadDeploymentPlan plan = SquadDeploymentPlanner.CreateInstall(
            aliasRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "generated")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Directory.Delete(aliasRoot);
        Directory.CreateSymbolicLink(aliasRoot, secondRoot);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        Exception? exception = Record.Exception(() =>
            new SquadTransaction(Store(applicationData)).Execute(plan));

        SquadDeploymentConflictException
            conflict = Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.Contains("root", conflict.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Fact]
    public void SerializeReceiptLayout_GlobalInstall_WritesV2WithPerTargetRootsLayout()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        SquadStateStore store = Store(applicationData);
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
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        SquadStateStore store = Store(applicationData);
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
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
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
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
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
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
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
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
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
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
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
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
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
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
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
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
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
        using TempDirectory fixture = new();
        string recordedRoot = Path.Combine(fixture.Path, "legacy-install");
        Directory.CreateDirectory(recordedRoot);
        string applicationData = Path.Combine(fixture.Path, "app-data");
        string fakeCodexRoot = Path.Combine(fixture.Path, "fake-codex");
        string fakeCopilotRoot = Path.Combine(fixture.Path, "fake-copilot");
        Directory.CreateDirectory(fakeCodexRoot);
        Directory.CreateDirectory(fakeCopilotRoot);

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

        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = fakeCodexRoot,
            [SquadTarget.Copilot] = fakeCopilotRoot
        });

        SquadDeploymentPlan uninstallPlan = SquadDeploymentPlanner.CreateUninstall(
            recordedRoot,
            SquadDeploymentScope.Global,
            legacyReceipt,
            globalRoots);

        new SquadTransaction(Store(applicationData)).Execute(uninstallPlan);

        Assert.False(File.Exists(Path.Combine(recordedRoot, ".codex/agents/conductor.toml")));
        Assert.False(File.Exists(Path.Combine(recordedRoot, ".github/agents/reviewer.agent.md")));
        Assert.True(File.Exists(Path.Combine(recordedRoot, ".codex/agents/edited.toml")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fakeCodexRoot, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fakeCopilotRoot, "*", SearchOption.AllDirectories));

        SquadReceipt? retainedReceipt = Store(applicationData).ReadReceipt(recordedRoot, SquadDeploymentScope.Global);
        Assert.NotNull(retainedReceipt);
        Assert.Single(retainedReceipt.Files);
        Assert.Equal(".codex/agents/edited.toml", retainedReceipt.Files[0].RelativePath);
        Assert.Equal(SquadReceiptLayout.SingleRoot, retainedReceipt.Layout);

        string retainedJson = Store(applicationData).SerializeReceipt(retainedReceipt);
        Assert.Contains("\"schema\": \"kyber-squad.receipt/v2\"", retainedJson, StringComparison.Ordinal);
        Assert.Contains("\"layout\": \"single-root\"", retainedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallReceiptLayout_LegacyReceiptAllClean_DeletesEverythingThenLockAndReceipt()
    {
        using TempDirectory fixture = new();
        string recordedRoot = Path.Combine(fixture.Path, "legacy-install");
        Directory.CreateDirectory(recordedRoot);
        string applicationData = Path.Combine(fixture.Path, "app-data");
        string fakeCodexRoot = Path.Combine(fixture.Path, "fake-codex");
        string fakeCopilotRoot = Path.Combine(fixture.Path, "fake-copilot");
        Directory.CreateDirectory(fakeCodexRoot);
        Directory.CreateDirectory(fakeCopilotRoot);

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

        SquadStateStore stateStore = Store(applicationData);
        string receiptPath = stateStore.ResolveReceiptPath(recordedRoot, SquadDeploymentScope.Global);
        string lockPath = stateStore.ResolveLockPath(recordedRoot, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, stateStore.SerializeReceipt(legacyReceipt), Encoding.UTF8);
        File.WriteAllText(lockPath, stateStore.SerializeLock(Lock()), Encoding.UTF8);

        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = fakeCodexRoot,
            [SquadTarget.Copilot] = fakeCopilotRoot
        });

        SquadDeploymentPlan uninstallPlan = SquadDeploymentPlanner.CreateUninstall(
            recordedRoot,
            SquadDeploymentScope.Global,
            legacyReceipt,
            globalRoots);

        new SquadTransaction(stateStore).Execute(uninstallPlan);

        Assert.False(File.Exists(Path.Combine(recordedRoot, ".codex/agents/conductor.toml")));
        Assert.False(File.Exists(Path.Combine(recordedRoot, ".github/agents/reviewer.agent.md")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fakeCodexRoot, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fakeCopilotRoot, "*", SearchOption.AllDirectories));

        Assert.False(File.Exists(receiptPath));
        Assert.False(File.Exists(lockPath));
        Assert.Null(stateStore.ReadReceipt(recordedRoot, SquadDeploymentScope.Global));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateReceiptLayout_LegacySingleRootReceipt_ThrowsSquadDeploymentConflictException(bool dryRun)
    {
        using TempDirectory fixture = new();
        string recordedRoot = Path.Combine(fixture.Path, "legacy-install");
        Directory.CreateDirectory(recordedRoot);
        string applicationData = Path.Combine(fixture.Path, "app-data");

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

        SquadStateStore stateStore = Store(applicationData);
        string stateDir = stateStore.ResolveStateDirectory(recordedRoot, SquadDeploymentScope.Global);
        string receiptPath = stateStore.ResolveReceiptPath(recordedRoot, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllTextAsync(
            receiptPath,
            SerializeAsLegacyV1Json(legacyReceipt),
            Encoding.UTF8);

        SortedDictionary<string, byte[]> receiptSnapshotBefore = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotBefore = Snapshot(recordedRoot);

        using RecordingReleaseSource releaseSource = new();
        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = Path.Combine(fixture.Path, "fake-codex")
        });
        SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(releaseSource, SquadHostDefaults.ResolveRenderer(), stateStore,
            globalRoots: globalRoots);

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
        using TempDirectory fixture = new();
        string recordedRoot = Path.Combine(fixture.Path, "legacy-install");
        Directory.CreateDirectory(recordedRoot);
        string applicationData = Path.Combine(fixture.Path, "app-data");

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

        SquadStateStore stateStore = Store(applicationData);
        string stateDir = stateStore.ResolveStateDirectory(recordedRoot, SquadDeploymentScope.Global);
        string receiptPath = stateStore.ResolveReceiptPath(recordedRoot, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllTextAsync(
            receiptPath,
            SerializeAsLegacyV1Json(legacyReceipt),
            Encoding.UTF8);

        SortedDictionary<string, byte[]> receiptSnapshotBefore = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotBefore = Snapshot(recordedRoot);

        using RecordingReleaseSource releaseSource = new();
        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = Path.Combine(fixture.Path, "fake-codex")
        });
        SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(releaseSource, SquadHostDefaults.ResolveRenderer(), stateStore,
            globalRoots: globalRoots);

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
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "app-data");
        string targetRoot = fixture.Path;
        SquadStateStore store = Store(applicationData);

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

        SquadDeploymentPlan updatePlan = SquadDeploymentPlanner.CreateUpdate(
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
        using TempDirectory fixture = new();
        string targetRoot = fixture.Path;

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
            SquadDeploymentPlanner.CreateUpdate(
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
        using TempDirectory fixture = new();
        string targetRoot = fixture.Path;

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

        SquadDeploymentPlan plan = SquadDeploymentPlanner.CreateUpdate(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated conductor content")],
            previousReceipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        Assert.Equal(
            SquadDeploymentPlan.ClassifyGlobalLayout(plan.Receipt.Files),
            plan.Receipt.Layout);
        Assert.Equal(SquadReceiptLayout.SingleRoot, plan.Receipt.Layout);
    }
}
